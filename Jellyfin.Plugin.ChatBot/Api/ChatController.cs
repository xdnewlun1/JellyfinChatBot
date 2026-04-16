using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ChatBot.Api.Models;
using Jellyfin.Plugin.ChatBot.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot.Api;

[ApiController]
[Route("ChatBot/Chat")]
public class ChatController : ControllerBase
{
    private readonly OllamaService _ollamaService;
    private readonly LibrarySearchService _librarySearchService;
    private readonly SeerrService _seerrService;
    private readonly TmdbService _tmdbService;
    private readonly WatchHistoryService _watchHistoryService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        OllamaService ollamaService,
        LibrarySearchService librarySearchService,
        SeerrService seerrService,
        TmdbService tmdbService,
        WatchHistoryService watchHistoryService,
        ILogger<ChatController> logger)
    {
        _ollamaService = ollamaService;
        _librarySearchService = librarySearchService;
        _seerrService = seerrService;
        _tmdbService = tmdbService;
        _watchHistoryService = watchHistoryService;
        _logger = logger;
    }

    private const int MaxMessageLength = 4000;
    private const int MaxMessagesPerRequest = 100;

    [HttpPost]
    [Authorize]
    [RequestSizeLimit(512_000)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ChatResponse>> Chat(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var userId = GetCurrentJellyfinUserId();

        if (!RateLimiter.TryAcquire(RateLimiter.Bucket.Chat, userId))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many chat requests. Slow down a bit.");
        }

        // Input validation: prevent oversized payloads
        if (request.Messages.Count > MaxMessagesPerRequest)
        {
            return BadRequest("Too many messages in request.");
        }

        foreach (var msg in request.Messages)
        {
            if (msg.Content.Length > MaxMessageLength)
            {
                return BadRequest("Message exceeds maximum length.");
            }

            // Validate role to prevent injection into Ollama prompt
            if (msg.Role != "user" && msg.Role != "assistant")
            {
                return BadRequest("Invalid message role.");
            }
        }

        // Build the Ollama message list
        var messages = new List<OllamaChatMessage>();

        // Add system prompt
        messages.Add(new OllamaChatMessage
        {
            Role = "system",
            Content = config.SystemPrompt
        });

        // Add user messages, trimming to max conversation turns (clamped)
        var maxTurns = Math.Clamp(config.MaxConversationTurns, 1, 100);
        var userMessages = request.Messages;
        if (userMessages.Count > maxTurns * 2)
        {
            userMessages = userMessages.Skip(userMessages.Count - maxTurns * 2).ToList();
        }

        foreach (var msg in userMessages)
        {
            messages.Add(new OllamaChatMessage
            {
                Role = msg.Role,
                Content = msg.Content
            });
        }

        // Build available tools
        var tools = BuildTools();

        var chatResponse = new ChatResponse();

        // Allow up to 5 tool-call rounds to prevent infinite loops
        for (int round = 0; round < 5; round++)
        {
            var ollamaResponse = await _ollamaService.ChatAsync(messages, tools, cancellationToken)
                .ConfigureAwait(false);

            var assistantMessage = ollamaResponse.Message;
            if (assistantMessage == null)
            {
                chatResponse.Reply = "I'm sorry, I didn't get a response. Please try again.";
                return Ok(chatResponse);
            }

            // If no tool calls, we have the final response
            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                chatResponse.Reply = assistantMessage.Content;
                return Ok(chatResponse);
            }

            // Add assistant message with tool calls to context
            messages.Add(assistantMessage);

            // Process each tool call
            foreach (var toolCall in assistantMessage.ToolCalls)
            {
                var toolResult = await ExecuteToolAsync(toolCall, chatResponse, userId, cancellationToken)
                    .ConfigureAwait(false);

                messages.Add(new OllamaChatMessage
                {
                    Role = "tool",
                    Content = toolResult
                });
            }
        }

        // If we hit the loop limit, return whatever we have
        if (string.IsNullOrEmpty(chatResponse.Reply))
        {
            chatResponse.Reply = "I encountered an issue processing your request. Please try again.";
        }

        return Ok(chatResponse);
    }

    [HttpGet("DefaultSystemPrompt")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<string> GetDefaultSystemPrompt()
    {
        var defaultConfig = new Configuration.PluginConfiguration();
        return Ok(defaultConfig.SystemPrompt);
    }

    [HttpGet("TestConnection")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<string>> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            var models = await _ollamaService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            return Ok(string.Join(", ", models));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama");
            // Don't leak internal exception details to the client
            return StatusCode(500, "Failed to connect to Ollama. Check the URL and ensure Ollama is running.");
        }
    }

    private List<OllamaTool> BuildTools()
    {
        var config = Plugin.Instance!.Configuration;
        var tools = new List<OllamaTool>
        {
            new OllamaTool
            {
                Function = new OllamaToolFunction
                {
                    Name = "search_library",
                    Description = "Search the Jellyfin media library for movies and TV shows. Matches against title and overview/description text. Supports filtering by genre, year range, tags, and minimum community rating. Use this when the user asks about available content.",
                    Parameters = new OllamaToolParameters
                    {
                        Properties = new Dictionary<string, OllamaToolProperty>
                        {
                            ["query"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Keyword to match in title or overview. Optional if other filters are supplied."
                            },
                            ["media_type"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Restrict to 'movie' or 'series'. Omit for both.",
                                Enum = new List<string> { "movie", "series" }
                            },
                            ["genre"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Exact genre name (e.g. 'Science Fiction', 'Comedy'). Call list_genres first if unsure which genres exist."
                            },
                            ["year_min"] = new OllamaToolProperty
                            {
                                Type = "number",
                                Description = "Minimum production year (e.g. 2020)."
                            },
                            ["year_max"] = new OllamaToolProperty
                            {
                                Type = "number",
                                Description = "Maximum production year (e.g. 2024)."
                            },
                            ["tags"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Filter by a tag on the media item."
                            },
                            ["min_community_rating"] = new OllamaToolProperty
                            {
                                Type = "number",
                                Description = "Minimum community rating (0-10 scale, e.g. 7.5)."
                            }
                        },
                        Required = new List<string>()
                    }
                }
            },
            new OllamaTool
            {
                Function = new OllamaToolFunction
                {
                    Name = "list_genres",
                    Description = "List all genres present in the Jellyfin library. Use this before search_library when the user asks for content by theme/genre and you need the exact genre name.",
                    Parameters = new OllamaToolParameters
                    {
                        Properties = new Dictionary<string, OllamaToolProperty>
                        {
                            ["media_type"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Restrict to 'movie' or 'series'. Omit for both.",
                                Enum = new List<string> { "movie", "series" }
                            }
                        },
                        Required = new List<string>()
                    }
                }
            },
            new OllamaTool
            {
                Function = new OllamaToolFunction
                {
                    Name = "get_watch_history",
                    Description = "Get the user's recently watched movies and TV shows, sorted by most recently played. Returns genres and ratings for each item. Use this to understand the user's preferences for personalized recommendations.",
                    Parameters = new OllamaToolParameters
                    {
                        Properties = new Dictionary<string, OllamaToolProperty>
                        {
                            ["media_type"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Restrict to 'movie' or 'series'. Omit for both.",
                                Enum = new List<string> { "movie", "series" }
                            },
                            ["limit"] = new OllamaToolProperty
                            {
                                Type = "number",
                                Description = "Number of items to return (1-100, default 30)."
                            }
                        },
                        Required = new List<string>()
                    }
                }
            }
        };

        if (config.TmdbEnabled && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            tools.Add(new OllamaTool
            {
                Function = new OllamaToolFunction
                {
                    Name = "discover_tmdb",
                    Description = "Discover movies or TV shows on TMDB by genre, year, rating, and other filters. Great for finding content by mood, theme, or era. Use for recommendations and discovery of content that may or may not be in the library.",
                    Parameters = new OllamaToolParameters
                    {
                        Properties = new Dictionary<string, OllamaToolProperty>
                        {
                            ["media_type"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Required. 'movie' or 'tv'.",
                                Enum = new List<string> { "movie", "tv" }
                            },
                            ["genres"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Comma-separated genre names (e.g. 'Drama,Thriller'). Uses TMDB genre names: Action, Adventure, Animation, Comedy, Crime, Documentary, Drama, Family, Fantasy, History, Horror, Music, Mystery, Romance, Science Fiction, Thriller, War, Western."
                            },
                            ["year_min"] = new OllamaToolProperty
                            {
                                Type = "number",
                                Description = "Minimum release year."
                            },
                            ["year_max"] = new OllamaToolProperty
                            {
                                Type = "number",
                                Description = "Maximum release year."
                            },
                            ["sort_by"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Sort order. Default: 'popularity.desc'.",
                                Enum = new List<string> { "popularity.desc", "vote_average.desc", "primary_release_date.desc", "revenue.desc" }
                            },
                            ["min_rating"] = new OllamaToolProperty
                            {
                                Type = "number",
                                Description = "Minimum TMDB vote average (0-10, e.g. 7.0). Requires at least 50 votes."
                            }
                        },
                        Required = new List<string> { "media_type" }
                    }
                }
            });

            tools.Add(new OllamaTool
            {
                Function = new OllamaToolFunction
                {
                    Name = "get_tmdb_recommendations",
                    Description = "Get movie/TV recommendations similar to a specific title from TMDB. Searches for the title first, then returns similar and recommended titles. Use when the user says 'something like X' or 'movies similar to X'.",
                    Parameters = new OllamaToolParameters
                    {
                        Properties = new Dictionary<string, OllamaToolProperty>
                        {
                            ["title"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "The title to find recommendations for."
                            },
                            ["media_type"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "Restrict to 'movie' or 'tv'. Omit to search both.",
                                Enum = new List<string> { "movie", "tv" }
                            }
                        },
                        Required = new List<string> { "title" }
                    }
                }
            });
        }

        if (config.SeerrEnabled)
        {
            tools.Add(new OllamaTool
            {
                Function = new OllamaToolFunction
                {
                    Name = "search_seerr",
                    Description = "Search for movies and TV shows on TMDB via Jellyseerr to find content that can be requested. Use this when content is NOT in the library and the user wants to request it.",
                    Parameters = new OllamaToolParameters
                    {
                        Properties = new Dictionary<string, OllamaToolProperty>
                        {
                            ["query"] = new OllamaToolProperty
                            {
                                Type = "string",
                                Description = "The search term (movie or show title)"
                            }
                        },
                        Required = new List<string> { "query" }
                    }
                }
            });

            // NOTE: request_media is intentionally NOT exposed as an LLM tool.
            // Media requests must be initiated by the user clicking the "Request"
            // button in the UI, not by the LLM autonomously. This prevents prompt
            // injection attacks from triggering unwanted media requests.
        }

        return tools;
    }

    private async Task<string> ExecuteToolAsync(
        OllamaToolCall toolCall,
        ChatResponse chatResponse,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var functionName = toolCall.Function.Name;
        var args = toolCall.Function.Arguments;

        _logger.LogDebug("Executing tool: {Tool}", functionName);

        try
        {
            switch (functionName)
            {
                case "search_library":
                {
                    var query = GetArgString(args, "query");
                    var mediaType = GetArgString(args, "media_type");
                    var genre = GetArgString(args, "genre");
                    var tags = GetArgString(args, "tags");
                    var yearMin = GetArgInt(args, "year_min");
                    var yearMax = GetArgInt(args, "year_max");
                    var minRating = GetArgDouble(args, "min_community_rating");

                    var results = _librarySearchService.Search(
                        userId,
                        string.IsNullOrWhiteSpace(query) ? null : query,
                        string.IsNullOrWhiteSpace(mediaType) ? null : mediaType,
                        string.IsNullOrWhiteSpace(genre) ? null : genre,
                        yearMin > 0 ? yearMin : null,
                        yearMax > 0 ? yearMax : null,
                        string.IsNullOrWhiteSpace(tags) ? null : tags,
                        minRating > 0 ? minRating : null);
                    chatResponse.LibraryResults = results;

                    if (results.Count == 0)
                    {
                        return JsonSerializer.Serialize(new { found = false, message = "No matching items in the library." });
                    }

                    return JsonSerializer.Serialize(new
                    {
                        found = true,
                        count = results.Count,
                        results = results.Select(r => new { r.Name, r.Year, r.Type, r.Overview, r.Genres, r.CommunityRating })
                    });
                }

                case "list_genres":
                {
                    var mediaType = GetArgString(args, "media_type");
                    var genres = _librarySearchService.GetGenres(
                        userId,
                        string.IsNullOrWhiteSpace(mediaType) ? null : mediaType);
                    return JsonSerializer.Serialize(new { count = genres.Count, genres });
                }

                case "get_watch_history":
                {
                    var mediaType = GetArgString(args, "media_type");
                    var limit = GetArgInt(args, "limit");
                    var items = _watchHistoryService.GetWatchHistory(
                        userId,
                        string.IsNullOrWhiteSpace(mediaType) ? null : mediaType,
                        limit > 0 ? limit : 30);

                    if (items.Count == 0)
                    {
                        return JsonSerializer.Serialize(new { found = false, message = "No watch history found." });
                    }

                    return JsonSerializer.Serialize(new
                    {
                        found = true,
                        count = items.Count,
                        items = items.Select(i => new { i.Name, i.Year, i.Type, i.Genres, i.CommunityRating })
                    });
                }

                case "discover_tmdb":
                {
                    var mediaType = GetArgString(args, "media_type");
                    var genres = GetArgString(args, "genres");
                    var yearMin = GetArgInt(args, "year_min");
                    var yearMax = GetArgInt(args, "year_max");
                    var sortBy = GetArgString(args, "sort_by");
                    var minRating = GetArgFloat(args, "min_rating");

                    var results = await _tmdbService.DiscoverAsync(
                        string.IsNullOrWhiteSpace(mediaType) ? "movie" : mediaType,
                        string.IsNullOrWhiteSpace(genres) ? null : genres,
                        yearMin > 0 ? yearMin : null,
                        yearMax > 0 ? yearMax : null,
                        string.IsNullOrWhiteSpace(sortBy) ? null : sortBy,
                        minRating > 0 ? minRating : null,
                        cancellationToken).ConfigureAwait(false);
                    chatResponse.TmdbResults = results;

                    if (results.Count == 0)
                    {
                        return JsonSerializer.Serialize(new { found = false, message = "No TMDB results matched the filters." });
                    }

                    return JsonSerializer.Serialize(new
                    {
                        found = true,
                        count = results.Count,
                        results = results.Select(r => new { r.Title, r.Year, r.MediaType, r.Overview, r.Rating, r.Genres })
                    });
                }

                case "get_tmdb_recommendations":
                {
                    var title = GetArgString(args, "title");
                    var mediaType = GetArgString(args, "media_type");

                    var results = await _tmdbService.GetRecommendationsAsync(
                        title,
                        string.IsNullOrWhiteSpace(mediaType) ? null : mediaType,
                        cancellationToken).ConfigureAwait(false);
                    chatResponse.TmdbResults = results;

                    if (results.Count == 0)
                    {
                        return JsonSerializer.Serialize(new { found = false, message = $"No recommendations found for '{title}'." });
                    }

                    return JsonSerializer.Serialize(new
                    {
                        found = true,
                        count = results.Count,
                        results = results.Select(r => new { r.Title, r.Year, r.MediaType, r.Overview, r.Rating, r.Genres })
                    });
                }

                case "search_seerr":
                {
                    var query = GetArgString(args, "query");
                    var results = await _seerrService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                    chatResponse.SeerrResults = results;

                    if (results.Count == 0)
                    {
                        return JsonSerializer.Serialize(new { found = false, message = $"No results found for '{query}' on TMDB." });
                    }

                    return JsonSerializer.Serialize(new
                    {
                        found = true,
                        count = results.Count,
                        results = results.Select(r => new { r.Id, r.Title, r.Year, r.MediaType, r.Overview, r.Status })
                    });
                }

                default:
                    return JsonSerializer.Serialize(new { error = $"Unknown tool: {functionName}" });
            }
        }
        catch (Exception ex)
        {
            // Do not leak exception details back to the LLM (which echoes to the user).
            _logger.LogError(ex, "Tool execution failed: {Tool}", functionName);
            return JsonSerializer.Serialize(new { error = $"Tool '{functionName}' failed." });
        }
    }

    private Guid GetCurrentJellyfinUserId()
    {
        var raw = User.FindFirstValue("Jellyfin-UserId")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("userId");

        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private static string GetArgString(JsonElement args, string key)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var val))
        {
            return val.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static int GetArgInt(JsonElement args, string key)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetInt32();
            if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var i)) return i;
        }

        return 0;
    }

    private static float GetArgFloat(JsonElement args, string key)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetSingle();
            if (val.ValueKind == JsonValueKind.String && float.TryParse(val.GetString(), out var f)) return f;
        }

        return 0f;
    }

    private static double GetArgDouble(JsonElement args, string key)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetDouble();
            if (val.ValueKind == JsonValueKind.String && double.TryParse(val.GetString(), out var d)) return d;
        }

        return 0d;
    }
}
