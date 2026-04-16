using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ChatBot.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot.Services;

public class SeerrService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SeerrService> _logger;

    public SeerrService(IHttpClientFactory httpClientFactory, ILogger<SeerrService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Cache Jellyfin userId → Jellyseerr numeric userId (positive hits) and negative markers for unmapped users.
    private static readonly MemoryCache _userCache = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private static readonly TimeSpan _userCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _userCacheNegativeTtl = TimeSpan.FromSeconds(60);

    public static void InvalidateUserCache()
    {
        // Called when plugin configuration changes; behavior may depend on current SeerrEnabled/SeerrUrl.
        _userCache.Clear();
    }

    private HttpClient CreateClient(int? seerrUserId = null)
    {
        var config = Plugin.Instance!.Configuration;

        // LOW-4: fail closed if Seerr integration is disabled.
        if (!config.SeerrEnabled)
        {
            throw new InvalidOperationException("Seerr integration is not enabled.");
        }

        // Validate URL scheme to prevent SSRF via file:// or other dangerous schemes
        if (string.IsNullOrWhiteSpace(config.SeerrUrl) ||
            !Uri.TryCreate(config.SeerrUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new InvalidOperationException("Seerr URL must be a valid http:// or https:// URL.");
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-Api-Key", config.SeerrApiKey);
        if (seerrUserId.HasValue)
        {
            client.DefaultRequestHeaders.Add("X-Api-User", seerrUserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return client;
    }

    public async Task<int?> ResolveSeerrUserIdAsync(Guid jellyfinUserId, CancellationToken cancellationToken = default)
    {
        if (jellyfinUserId == Guid.Empty) return null;

        if (_userCache.TryGetValue(jellyfinUserId, out object? cachedObj))
        {
            // Negative entries are cached as the sentinel -1.
            if (cachedObj is int cachedId)
            {
                return cachedId >= 0 ? cachedId : null;
            }
        }

        var config = Plugin.Instance!.Configuration;
        if (!config.SeerrEnabled || string.IsNullOrEmpty(config.SeerrUrl)) return null;

        var client = CreateClient();
        var jfIdNoDashes = jellyfinUserId.ToString("N");
        var jfIdWithDashes = jellyfinUserId.ToString("D");

        // Page through users until we find a match. Jellyseerr caps take at 100.
        var skip = 0;
        const int take = 100;
        while (true)
        {
            var url = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/user?take={take}&skip={skip}";
            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Seerr /user returned {Status} while resolving Jellyfin user", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
            if (!json.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var count = 0;
            foreach (var u in results.EnumerateArray())
            {
                count++;
                var jfId = u.TryGetProperty("jellyfinUserId", out var jf) ? jf.GetString() : null;
                if (string.IsNullOrEmpty(jfId)) continue;

                if (string.Equals(jfId, jfIdNoDashes, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(jfId, jfIdWithDashes, StringComparison.OrdinalIgnoreCase))
                {
                    if (u.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var seerrId))
                    {
                        _userCache.Set(jellyfinUserId, (object)seerrId, new MemoryCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = _userCacheTtl,
                            Size = 1
                        });
                        return seerrId;
                    }
                }
            }

            if (count < take) break;
            skip += take;
            if (skip > 1000) break; // safety
        }

        // LOW-5 / MEDIUM-4: cache the negative result briefly so an unmapped user doesn't re-paginate on every click.
        _userCache.Set(jellyfinUserId, (object)(-1), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _userCacheNegativeTtl,
            Size = 1
        });

        _logger.LogInformation("No Jellyseerr user found mapped to Jellyfin user {User}", jellyfinUserId);
        return null;
    }

    public async Task<List<SeerrSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.SeerrEnabled || string.IsNullOrEmpty(config.SeerrUrl))
        {
            return new List<SeerrSearchResult>();
        }

        var client = CreateClient();
        var url = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/search?query={Uri.EscapeDataString(query)}&page=1&language=en";

        _logger.LogDebug("Searching Seerr (query length {Len})", (query ?? string.Empty).Length);

        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var results = new List<SeerrSearchResult>();

        if (json.TryGetProperty("results", out var resultsArray))
        {
            foreach (var item in resultsArray.EnumerateArray())
            {
                var mediaType = item.TryGetProperty("mediaType", out var mt) ? mt.GetString() ?? "" : "";
                if (mediaType != "movie" && mediaType != "tv")
                {
                    continue;
                }

                var title = mediaType == "movie"
                    ? (item.TryGetProperty("title", out var t) ? t.GetString() : null)
                    : (item.TryGetProperty("name", out var n) ? n.GetString() : null);

                string? rawDate = null;
                if (mediaType == "movie" && item.TryGetProperty("releaseDate", out var rd))
                {
                    rawDate = rd.GetString();
                }
                else if (mediaType == "tv" && item.TryGetProperty("firstAirDate", out var fa))
                {
                    rawDate = fa.GetString();
                }
                var year = (rawDate != null && rawDate.Length >= 4) ? rawDate.Substring(0, 4) : null;

                var status = 0;
                if (item.TryGetProperty("mediaInfo", out var mi) && mi.TryGetProperty("status", out var s))
                {
                    status = s.GetInt32();
                }

                results.Add(new SeerrSearchResult
                {
                    Id = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                    MediaType = mediaType,
                    Title = title ?? "Unknown",
                    Overview = item.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
                    Year = year,
                    PosterPath = item.TryGetProperty("posterPath", out var pp) ? pp.GetString() : null,
                    Status = status
                });
            }
        }

        return results;
    }

    public async Task<JsonElement> RequestMediaAsync(SeerrRequestDto request, int? onBehalfOfSeerrUserId = null, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.SeerrEnabled || string.IsNullOrEmpty(config.SeerrUrl))
        {
            throw new InvalidOperationException("Seerr integration is not enabled");
        }

        var client = CreateClient(onBehalfOfSeerrUserId);
        var url = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/request";

        // Build payload. Jellyseerr expects different shapes depending on content.
        var payloadDict = new Dictionary<string, object?>
        {
            ["mediaType"] = request.MediaType,
            ["mediaId"] = request.MediaId
        };

        if (request.MediaType == "tv")
        {
            payloadDict["seasons"] = (request.Seasons != null && request.Seasons.Length > 0)
                ? (object)request.Seasons
                : "all";
        }

        if (request.ServerId.HasValue) payloadDict["serverId"] = request.ServerId.Value;
        if (request.ProfileId.HasValue) payloadDict["profileId"] = request.ProfileId.Value;
        if (!string.IsNullOrWhiteSpace(request.RootFolder)) payloadDict["rootFolder"] = request.RootFolder;
        if (request.Is4K.HasValue) payloadDict["is4k"] = request.Is4K.Value;

        object payload = payloadDict;

        _logger.LogInformation("Creating Seerr request: {Type} ID {Id}", request.MediaType, request.MediaId);

        var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Seerr rejected request ({Status}): {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Jellyseerr rejected the request ({(int)response.StatusCode}): {body}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> GetServicesAsync(string service, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        var client = CreateClient();
        var url = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/service/{service}";

        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> GetServiceDetailsAsync(string service, int serverId, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        var client = CreateClient();
        var url = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/service/{service}/{serverId}";

        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> GetMediaDetailsAsync(string mediaType, int tmdbId, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        var client = CreateClient();
        var url = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/{mediaType}/{tmdbId}";

        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
    }
}
