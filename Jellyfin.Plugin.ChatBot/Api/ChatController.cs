using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly ChatCompletionService _chatService;
    private readonly LibrarySearchService _librarySearchService;
    private readonly SeerrService _seerrService;
    private readonly TmdbService _tmdbService;
    private readonly WatchHistoryService _watchHistoryService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        ChatCompletionService chatService,
        LibrarySearchService librarySearchService,
        SeerrService seerrService,
        TmdbService tmdbService,
        WatchHistoryService watchHistoryService,
        ILogger<ChatController> logger)
    {
        _chatService = chatService;
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

            // Validate role to prevent injection into the model prompt
            if (msg.Role != "user" && msg.Role != "assistant")
            {
                return BadRequest("Invalid message role.");
            }
        }

        // Build the chat completion message list
        var messages = new List<OpenAiChatMessage>();

        // Add system prompt
        messages.Add(new OpenAiChatMessage
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
            messages.Add(new OpenAiChatMessage
            {
                Role = msg.Role,
                Content = msg.Content
            });
        }

        // Build available tools
        var tools = BuildTools();

        var chatResponse = new ChatResponse();

        // Once a model answers, stay on it for the remaining tool rounds — switching
        // models mid-conversation produces incoherent replies.
        string? pinnedModel = null;

        // Allow up to 5 tool-call rounds to prevent infinite loops
        for (int round = 0; round < 5; round++)
        {
            ChatCompletionResult completion;
            try
            {
                completion = await _chatService.ChatAsync(messages, tools, pinnedModel, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Don't leak endpoint URLs, keys or upstream error bodies to the client.
                _logger.LogError(ex, "Chat completion failed for all configured models.");
                chatResponse.Reply = "I couldn't reach the AI backend just now. Please try again in a moment.";
                return Ok(chatResponse);
            }

            pinnedModel = completion.Model;
            var assistantMessage = completion.Message;

            // If no tool calls, we have the final response
            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                chatResponse.Reply = assistantMessage.Content ?? string.Empty;
                return Ok(chatResponse);
            }

            // Add assistant message with tool calls to context. Some backends reject a
            // null content field on the echo, so normalize it.
            assistantMessage.Content ??= string.Empty;
            messages.Add(assistantMessage);

            // Process each tool call
            foreach (var toolCall in assistantMessage.ToolCalls)
            {
                var toolResult = await ExecuteToolAsync(toolCall, chatResponse, userId, cancellationToken)
                    .ConfigureAwait(false);

                messages.Add(new OpenAiChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Content = toolResult
                });
            }
        }

        // Round budget exhausted: the model kept calling tools without ever composing an
        // answer, which is the normal shape of a fruitless search. Ask once more with no
        // tools so it has to reply in prose, rather than showing the user a bare error.
        if (string.IsNullOrEmpty(chatResponse.Reply))
        {
            try
            {
                var final = await _chatService.ChatAsync(messages, null, pinnedModel, cancellationToken)
                    .ConfigureAwait(false);
                chatResponse.Reply = final.Message.Content ?? string.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final tool-free completion failed after the tool round limit.");
            }
        }

        if (string.IsNullOrWhiteSpace(chatResponse.Reply))
        {
            chatResponse.Reply = "I searched but didn't turn up anything matching that. Try rephrasing, or ask me to look for something to request.";
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
            var summary = await _chatService.TestAsync(cancellationToken).ConfigureAwait(false);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach the AI endpoint");
            // Don't leak internal exception details to the client
            return StatusCode(500, "Failed to reach the AI endpoint. Check the base URL, API key and model names, and save settings first.");
        }
    }

    private List<OpenAiTool> BuildTools()
    {
        var config = Plugin.Instance!.Configuration;
        var tools = new List<OpenAiTool>
        {
            new OpenAiTool
            {
                Function = new OpenAiToolFunction
                {
                    Name = "search_library",
                    Description = "Search the Jellyfin media library for movies and TV shows. Matches against title and overview/description text. Supports filtering by genre, year range, tags, and minimum community rating. Use this when the user asks about available content.",
                    Parameters = new OpenAiToolParameters
                    {
                        Properties = new Dictionary<string, OpenAiToolProperty>
                        {
                            ["query"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "Keyword to match in title or overview. Optional if other filters are supplied."
                            },
                            ["media_type"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "Restrict to 'movie' or 'series'. Omit for both.",
                                Enum = new List<string> { "movie", "series" }
                            },
                            ["genre"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "Exact genre name (e.g. 'Science Fiction', 'Comedy'). Call list_genres first if unsure which genres exist."
                            },
                            ["year_min"] = new OpenAiToolProperty
                            {
                                Type = "number",
                                Description = "Minimum production year (e.g. 2020)."
                            },
                            ["year_max"] = new OpenAiToolProperty
                            {
                                Type = "number",
                                Description = "Maximum production year (e.g. 2024)."
                            },
                            ["tags"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "Filter by a tag on the media item."
                            },
                            ["min_community_rating"] = new OpenAiToolProperty
                            {
                                Type = "number",
                                Description = "Minimum community rating (0-10 scale, e.g. 7.5)."
                            }
                        },
                        Required = new List<string>()
                    }
                }
            },
            new OpenAiTool
            {
                Function = new OpenAiToolFunction
                {
                    Name = "list_genres",
                    Description = "List all genres present in the Jellyfin library. Use this before search_library when the user asks for content by theme/genre and you need the exact genre name.",
                    Parameters = new OpenAiToolParameters
                    {
                        Properties = new Dictionary<string, OpenAiToolProperty>
                        {
                            ["media_type"] = new OpenAiToolProperty
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
            new OpenAiTool
            {
                Function = new OpenAiToolFunction
                {
                    Name = "get_watch_history",
                    Description = "Get the user's recently watched movies and TV shows, sorted by most recently played. Returns genres and ratings for each item. Use this to understand the user's preferences for personalized recommendations.",
                    Parameters = new OpenAiToolParameters
                    {
                        Properties = new Dictionary<string, OpenAiToolProperty>
                        {
                            ["media_type"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "Restrict to 'movie' or 'series'. Omit for both.",
                                Enum = new List<string> { "movie", "series" }
                            },
                            ["limit"] = new OpenAiToolProperty
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
            tools.Add(new OpenAiTool
            {
                Function = new OpenAiToolFunction
                {
                    Name = "discover_tmdb",
                    Description = "Discover movies or TV shows on TMDB by genre, year, rating, and other filters. Great for finding content by mood, theme, or era. Use for recommendations and discovery of content that may or may not be in the library.",
                    Parameters = new OpenAiToolParameters
                    {
                        Properties = new Dictionary<string, OpenAiToolProperty>
                        {
                            ["media_type"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "Required. 'movie' or 'tv'.",
                                Enum = new List<string> { "movie", "tv" }
                            },
                            ["genres"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "Comma-separated genre names (e.g. 'Drama,Thriller'). Uses TMDB genre names: Action, Adventure, Animation, Comedy, Crime, Documentary, Drama, Family, Fantasy, History, Horror, Music, Mystery, Romance, Science Fiction, Thriller, War, Western."
                            },
                            ["year_min"] = new OpenAiToolProperty
                            {
                                Type = "number",
                                Description = "Minimum release year."
                            },
                            ["year_max"] = new OpenAiToolProperty
                            {
                                Type = "number",
                                Description = "Maximum release year."
                            },
                            ["sort_by"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "Sort order. Default: 'popularity.desc'.",
                                Enum = new List<string> { "popularity.desc", "vote_average.desc", "primary_release_date.desc", "revenue.desc" }
                            },
                            ["min_rating"] = new OpenAiToolProperty
                            {
                                Type = "number",
                                Description = "Minimum TMDB vote average (0-10, e.g. 7.0). Requires at least 50 votes."
                            }
                        },
                        Required = new List<string> { "media_type" }
                    }
                }
            });

            tools.Add(new OpenAiTool
            {
                Function = new OpenAiToolFunction
                {
                    Name = "get_tmdb_recommendations",
                    Description = "Get movie/TV recommendations similar to a specific title from TMDB. Searches for the title first, then returns similar and recommended titles. Use when the user says 'something like X' or 'movies similar to X'.",
                    Parameters = new OpenAiToolParameters
                    {
                        Properties = new Dictionary<string, OpenAiToolProperty>
                        {
                            ["title"] = new OpenAiToolProperty
                            {
                                Type = "string",
                                Description = "The title to find recommendations for."
                            },
                            ["media_type"] = new OpenAiToolProperty
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
            tools.Add(new OpenAiTool
            {
                Function = new OpenAiToolFunction
                {
                    Name = "search_seerr",
                    Description = "Search for movies and TV shows on TMDB via Jellyseerr to find content that can be requested. Use this when content is NOT in the library and the user wants to request it.",
                    Parameters = new OpenAiToolParameters
                    {
                        Properties = new Dictionary<string, OpenAiToolProperty>
                        {
                            ["query"] = new OpenAiToolProperty
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
        OpenAiToolCall toolCall,
        ChatResponse chatResponse,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var functionName = toolCall.Function.Name;
        var args = ParseArguments(toolCall.Function.Arguments);

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

    // Tool arguments arrive as a JSON-encoded string per the OpenAI spec. Models
    // occasionally double-encode it, so unwrap one extra layer of quoting.
    private static JsonElement ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(root.GetString() ?? "{}");
                return inner.RootElement.Clone();
            }

            return root.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // Tool arguments are model output, not a trusted schema: the declared type is a hint
    // the model is free to ignore. Every getter below coerces what it is given and falls
    // back to the "absent" value rather than throwing, since an exception here aborts the
    // whole tool call and the user just sees "that tool failed".
    private static bool TryGetArg(JsonElement args, string key, out JsonElement value)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string GetArgString(JsonElement args, string key)
    {
        if (!TryGetArg(args, key, out var val))
        {
            return string.Empty;
        }

        return val.ValueKind switch
        {
            JsonValueKind.String => val.GetString() ?? string.Empty,
            // Models sometimes answer a string parameter with a number or bool.
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => val.GetRawText(),
            // ...or with a list, where the tools document a comma-separated string.
            JsonValueKind.Array => string.Join(
                ",",
                val.EnumerateArray()
                   .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                   .Where(v => !string.IsNullOrWhiteSpace(v))),
            _ => string.Empty
        };
    }

    private static int GetArgInt(JsonElement args, string key)
    {
        if (!TryGetArg(args, key, out var val))
        {
            return 0;
        }

        // A JSON number that isn't an integer (limit: 10.0) makes GetInt32 throw, so round.
        if (val.ValueKind == JsonValueKind.Number)
        {
            if (val.TryGetInt32(out var i)) return i;
            if (val.TryGetDouble(out var d) && d >= int.MinValue && d <= int.MaxValue) return (int)Math.Round(d);
            return 0;
        }

        if (val.ValueKind == JsonValueKind.String)
        {
            var raw = val.GetString();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && d >= int.MinValue && d <= int.MaxValue) return (int)Math.Round(d);
        }

        return 0;
    }

    private static float GetArgFloat(JsonElement args, string key)
    {
        if (!TryGetArg(args, key, out var val))
        {
            return 0f;
        }

        if (val.ValueKind == JsonValueKind.Number && val.TryGetSingle(out var f)) return f;
        if (val.ValueKind == JsonValueKind.String
            && float.TryParse(val.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0f;
    }

    private static double GetArgDouble(JsonElement args, string key)
    {
        if (!TryGetArg(args, key, out var val))
        {
            return 0d;
        }

        if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var d)) return d;
        if (val.ValueKind == JsonValueKind.String
            && double.TryParse(val.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0d;
    }
}
