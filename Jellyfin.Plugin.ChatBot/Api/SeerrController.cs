using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ChatBot.Api.Models;
using Jellyfin.Plugin.ChatBot.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot.Api;

[ApiController]
[Route("ChatBot/Seerr")]
[Authorize]
public class SeerrController : ControllerBase
{
    private readonly SeerrService _seerrService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SeerrController> _logger;

    public SeerrController(
        SeerrService seerrService,
        IHttpClientFactory httpClientFactory,
        ILogger<SeerrController> logger)
    {
        _seerrService = seerrService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<string>> TestConnection(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        if (string.IsNullOrWhiteSpace(config.SeerrUrl) ||
            !Uri.TryCreate(config.SeerrUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return BadRequest("Seerr URL must be a valid http:// or https:// URL.");
        }

        if (string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return BadRequest("Seerr API key is not set.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("X-Api-Key", config.SeerrApiKey);

            var url = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/status";
            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(500, $"Seerr returned {(int)response.StatusCode}. Check URL and API key.");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
            var version = json.TryGetProperty("version", out var v) ? v.GetString() : "unknown";
            return Ok($"Connected to Jellyseerr/Overseerr {version}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Seerr");
            return StatusCode(500, "Connection failed. Check URL and API key.");
        }
    }

    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<SeerrSearchResult>>> Search(
        [FromQuery] string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
        {
            return BadRequest("Invalid search query.");
        }

        var config = Plugin.Instance!.Configuration;
        if (!config.SeerrEnabled)
        {
            return BadRequest("Seerr integration is not enabled.");
        }

        var results = await _seerrService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }

    [HttpGet("RequestOptions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<JsonElement>> GetRequestOptions(
        [FromQuery] string mediaType,
        [FromQuery] int tmdbId,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.SeerrEnabled)
        {
            return BadRequest("Seerr integration is not enabled.");
        }
        if (mediaType != "movie" && mediaType != "tv")
        {
            return BadRequest("Invalid media type.");
        }
        if (tmdbId <= 0)
        {
            return BadRequest("Invalid tmdbId.");
        }

        try
        {
            var service = mediaType == "movie" ? "radarr" : "sonarr";
            var serversRaw = await _seerrService.GetServicesAsync(service, cancellationToken).ConfigureAwait(false);

            var servers = new List<object>();
            if (serversRaw.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in serversRaw.EnumerateArray())
                {
                    var id = s.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : -1;
                    if (id < 0) continue;

                    var detail = await _seerrService.GetServiceDetailsAsync(service, id, cancellationToken).ConfigureAwait(false);

                    var profiles = new List<object>();
                    if (detail.TryGetProperty("profiles", out var profArr) && profArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in profArr.EnumerateArray())
                        {
                            profiles.Add(new
                            {
                                id = p.TryGetProperty("id", out var pid) ? pid.GetInt32() : 0,
                                name = p.TryGetProperty("name", out var pn) ? pn.GetString() : ""
                            });
                        }
                    }

                    var rootFolders = new List<object>();
                    if (detail.TryGetProperty("rootFolders", out var rfArr) && rfArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rfArr.EnumerateArray())
                        {
                            rootFolders.Add(new
                            {
                                path = r.TryGetProperty("path", out var rp) ? rp.GetString() : ""
                            });
                        }
                    }

                    servers.Add(new
                    {
                        id,
                        name = s.TryGetProperty("name", out var nEl) ? nEl.GetString() : "",
                        is4k = s.TryGetProperty("is4k", out var k) && k.GetBoolean(),
                        isDefault = s.TryGetProperty("isDefault", out var d) && d.GetBoolean(),
                        activeProfileId = s.TryGetProperty("activeProfileId", out var ap) ? ap.GetInt32() : 0,
                        activeDirectory = s.TryGetProperty("activeDirectory", out var ad) ? ad.GetString() : "",
                        profiles,
                        rootFolders
                    });
                }
            }

            var seasons = new List<object>();
            if (mediaType == "tv")
            {
                var media = await _seerrService.GetMediaDetailsAsync(mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
                if (media.TryGetProperty("seasons", out var seasonsArr) && seasonsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in seasonsArr.EnumerateArray())
                    {
                        var num = s.TryGetProperty("seasonNumber", out var sn) ? sn.GetInt32() : -1;
                        if (num < 0) continue;
                        seasons.Add(new
                        {
                            seasonNumber = num,
                            name = s.TryGetProperty("name", out var snn) ? snn.GetString() : $"Season {num}",
                            episodeCount = s.TryGetProperty("episodeCount", out var ec) ? ec.GetInt32() : 0
                        });
                    }
                }
            }

            return Ok(JsonSerializer.SerializeToElement(new { servers, seasons }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Seerr request options");
            return StatusCode(500, "Failed to load request options from Jellyseerr.");
        }
    }

    [HttpPost("Request")]
    [RequestSizeLimit(16_000)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<JsonElement>> RequestMedia(
        [FromBody] SeerrRequestDto request,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.SeerrEnabled)
        {
            return BadRequest("Seerr integration is not enabled.");
        }

        // Validate media type to prevent arbitrary values being forwarded to Seerr
        if (request.MediaType != "movie" && request.MediaType != "tv")
        {
            return BadRequest("Invalid media type. Must be 'movie' or 'tv'.");
        }

        if (request.MediaId <= 0)
        {
            return BadRequest("Invalid media ID.");
        }

        var jfUserIdValue = GetCurrentJellyfinUserId() ?? Guid.Empty;
        if (!RateLimiter.TryAcquire(RateLimiter.Bucket.SeerrRequest, jfUserIdValue))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many requests. Try again later.");
        }

        // MEDIUM-1: validate that serverId/profileId/rootFolder come from the set Jellyseerr actually offers for this media type.
        if (request.ServerId.HasValue || request.ProfileId.HasValue || !string.IsNullOrWhiteSpace(request.RootFolder))
        {
            if (!await ValidateRequestTargetsAsync(request, cancellationToken).ConfigureAwait(false))
            {
                return BadRequest("Invalid server, profile, or root folder selection.");
            }
        }

        int? seerrUserId = null;
        if (jfUserIdValue != Guid.Empty)
        {
            seerrUserId = await _seerrService.ResolveSeerrUserIdAsync(jfUserIdValue, cancellationToken).ConfigureAwait(false);
        }

        if (!seerrUserId.HasValue)
        {
            return BadRequest(
                "Your Jellyfin account isn't linked to a Jellyseerr account. " +
                "Sign in to Jellyseerr once (via Jellyfin) so it can map your user, then try again.");
        }

        try
        {
            var result = await _seerrService.RequestMediaAsync(request, seerrUserId, cancellationToken).ConfigureAwait(false);
            return Ok(result);
        }
        catch (InvalidOperationException)
        {
            // MEDIUM-2: do not reflect upstream error body to the client; log is already captured in the service.
            return BadRequest("Jellyseerr rejected the request.");
        }
    }

    private async Task<bool> ValidateRequestTargetsAsync(SeerrRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            var service = request.MediaType == "movie" ? "radarr" : "sonarr";
            var serversRaw = await _seerrService.GetServicesAsync(service, cancellationToken).ConfigureAwait(false);

            if (serversRaw.ValueKind != JsonValueKind.Array) return false;

            foreach (var s in serversRaw.EnumerateArray())
            {
                if (!s.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var sid)) continue;
                if (request.ServerId.HasValue && request.ServerId.Value != sid) continue;

                var is4k = s.TryGetProperty("is4k", out var k4) && k4.GetBoolean();
                if (request.Is4K.HasValue && request.Is4K.Value != is4k) continue;

                var detail = await _seerrService.GetServiceDetailsAsync(service, sid, cancellationToken).ConfigureAwait(false);

                bool profileOk = !request.ProfileId.HasValue;
                if (!profileOk && detail.TryGetProperty("profiles", out var profArr) && profArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in profArr.EnumerateArray())
                    {
                        if (p.TryGetProperty("id", out var pid) && pid.TryGetInt32(out var pidVal) && pidVal == request.ProfileId.GetValueOrDefault())
                        {
                            profileOk = true; break;
                        }
                    }
                }

                bool rootOk = string.IsNullOrWhiteSpace(request.RootFolder);
                if (!rootOk && detail.TryGetProperty("rootFolders", out var rfArr) && rfArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in rfArr.EnumerateArray())
                    {
                        if (r.TryGetProperty("path", out var rp) && rp.GetString() == request.RootFolder)
                        {
                            rootOk = true; break;
                        }
                    }
                }

                if (profileOk && rootOk)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate Jellyseerr request targets; rejecting.");
            return false;
        }
    }

    private Guid? GetCurrentJellyfinUserId()
    {
        var raw = User.FindFirstValue("Jellyfin-UserId")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("userId");

        if (Guid.TryParse(raw, out var id)) return id;
        return null;
    }
}
