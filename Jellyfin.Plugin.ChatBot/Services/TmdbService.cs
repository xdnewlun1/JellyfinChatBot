using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ChatBot.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot.Services;

public class TmdbService
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/w500";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbService> _logger;

    // Cached genre maps: name (lowercase) → id
    private readonly ConcurrentDictionary<string, Dictionary<string, int>> _genreCache = new();
    private readonly SemaphoreSlim _genreLock = new(1, 1);

    public TmdbService(IHttpClientFactory httpClientFactory, ILogger<TmdbService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Discover movies or TV shows using TMDB filters.
    /// </summary>
    public async Task<List<TmdbResult>> DiscoverAsync(
        string mediaType,
        string? genres,
        int? yearMin,
        int? yearMax,
        string? sortBy,
        float? minRating,
        CancellationToken cancellationToken)
    {
        var type = mediaType?.Equals("tv", StringComparison.OrdinalIgnoreCase) == true ? "tv" : "movie";
        var url = $"{BaseUrl}/discover/{type}?language=en-US&page=1";

        url += $"&sort_by={Uri.EscapeDataString(sortBy ?? "popularity.desc")}";

        if (!string.IsNullOrWhiteSpace(genres))
        {
            var genreIds = await ResolveGenreIdsAsync(type, genres, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(genreIds))
            {
                url += $"&with_genres={genreIds}";
            }
        }

        if (yearMin.HasValue)
        {
            url += type == "movie"
                ? $"&primary_release_date.gte={yearMin.Value}-01-01"
                : $"&first_air_date.gte={yearMin.Value}-01-01";
        }

        if (yearMax.HasValue)
        {
            url += type == "movie"
                ? $"&primary_release_date.lte={yearMax.Value}-12-31"
                : $"&first_air_date.lte={yearMax.Value}-12-31";
        }

        if (minRating.HasValue)
        {
            url += $"&vote_average.gte={minRating.Value.ToString("F1", CultureInfo.InvariantCulture)}";
            url += "&vote_count.gte=50";
        }

        var json = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseResults(json, type);
    }

    /// <summary>
    /// Get TMDB recommendations for a title. Searches for the title first, then fetches recommendations.
    /// </summary>
    public async Task<List<TmdbResult>> GetRecommendationsAsync(
        string title,
        string? mediaType,
        CancellationToken cancellationToken)
    {
        // Determine which media type(s) to search
        var types = new List<string>();
        if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Equals("movie", StringComparison.OrdinalIgnoreCase))
            types.Add("movie");
        if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase))
            types.Add("tv");

        // Search for the title on TMDB to get its ID
        int? foundId = null;
        string? foundType = null;

        foreach (var type in types)
        {
            var searchUrl = $"{BaseUrl}/search/{type}?language=en-US&page=1&query={Uri.EscapeDataString(title)}";
            var searchJson = await FetchAsync(searchUrl, cancellationToken).ConfigureAwait(false);

            if (searchJson.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                if (first.TryGetProperty("id", out var idProp))
                {
                    foundId = idProp.GetInt32();
                    foundType = type;
                    break;
                }
            }
        }

        if (foundId == null || foundType == null)
        {
            return new List<TmdbResult>();
        }

        // Fetch recommendations
        var recsUrl = $"{BaseUrl}/{foundType}/{foundId}/recommendations?language=en-US&page=1";
        var recsJson = await FetchAsync(recsUrl, cancellationToken).ConfigureAwait(false);
        var recs = ParseResults(recsJson, foundType);

        // Also fetch similar titles and merge (deduped)
        var similarUrl = $"{BaseUrl}/{foundType}/{foundId}/similar?language=en-US&page=1";
        var similarJson = await FetchAsync(similarUrl, cancellationToken).ConfigureAwait(false);
        var similar = ParseResults(similarJson, foundType);

        var seenIds = new HashSet<int>(recs.Select(r => r.Id));
        foreach (var s in similar)
        {
            if (seenIds.Add(s.Id))
            {
                recs.Add(s);
            }
        }

        return recs.Take(20).ToList();
    }

    private async Task<JsonElement> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var apiKey = config.TmdbApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("TMDB API key is not configured.");
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // V4 read access tokens are long JWTs, V3 API keys are short
        if (apiKey.Length > 100)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        else
        {
            var separator = url.Contains('?') ? '&' : '?';
            request.RequestUri = new Uri($"{url}{separator}api_key={apiKey}");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
    }

    private List<TmdbResult> ParseResults(JsonElement json, string type)
    {
        var results = new List<TmdbResult>();

        if (!json.TryGetProperty("results", out var array))
        {
            return results;
        }

        foreach (var item in array.EnumerateArray())
        {
            var titleProp = type == "tv" ? "name" : "title";
            var dateProp = type == "tv" ? "first_air_date" : "release_date";

            var title = item.TryGetProperty(titleProp, out var t) ? t.GetString() ?? "" : "";
            var dateStr = item.TryGetProperty(dateProp, out var d) ? d.GetString() : null;
            int? year = null;
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 && int.TryParse(dateStr[..4], out var y))
            {
                year = y;
            }

            var poster = item.TryGetProperty("poster_path", out var pp) && pp.ValueKind == JsonValueKind.String
                ? $"{ImageBaseUrl}{pp.GetString()}"
                : null;

            var rating = item.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number
                ? (float?)va.GetSingle()
                : null;

            var genreNames = new List<string>();
            if (item.TryGetProperty("genre_ids", out var gids))
            {
                var genreMap = GetCachedReverseGenreMap(type);
                foreach (var gid in gids.EnumerateArray())
                {
                    if (gid.ValueKind == JsonValueKind.Number && genreMap.TryGetValue(gid.GetInt32(), out var name))
                    {
                        genreNames.Add(name);
                    }
                }
            }

            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;

            results.Add(new TmdbResult
            {
                Id = id,
                Title = title,
                Overview = item.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
                Year = year,
                MediaType = type,
                Rating = rating,
                Genres = genreNames.Count > 0 ? genreNames : null,
                PosterUrl = poster
            });
        }

        return results;
    }

    private async Task<string> ResolveGenreIdsAsync(string type, string genreNames, CancellationToken cancellationToken)
    {
        var map = await GetGenreMapAsync(type, cancellationToken).ConfigureAwait(false);
        var ids = new List<int>();

        foreach (var name in genreNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (map.TryGetValue(name.ToLowerInvariant(), out var id))
            {
                ids.Add(id);
            }
            else
            {
                _logger.LogDebug("TMDB genre not found: {Genre}", name);
            }
        }

        return string.Join(",", ids);
    }

    private async Task<Dictionary<string, int>> GetGenreMapAsync(string type, CancellationToken cancellationToken)
    {
        if (_genreCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        await _genreLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_genreCache.TryGetValue(type, out cached))
            {
                return cached;
            }

            var url = $"{BaseUrl}/genre/{type}/list?language=en-US";
            var json = await FetchAsync(url, cancellationToken).ConfigureAwait(false);

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (json.TryGetProperty("genres", out var genres))
            {
                foreach (var g in genres.EnumerateArray())
                {
                    var name = g.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var id = g.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                    if (!string.IsNullOrEmpty(name) && id > 0)
                    {
                        map[name.ToLowerInvariant()] = id;
                    }
                }
            }

            _genreCache[type] = map;
            _logger.LogInformation("Cached {Count} TMDB genres for {Type}", map.Count, type);
            return map;
        }
        finally
        {
            _genreLock.Release();
        }
    }

    /// <summary>
    /// Reverse map: genre ID → display name. Used when parsing discover/recommendation results.
    /// </summary>
    private Dictionary<int, string> GetCachedReverseGenreMap(string type)
    {
        if (_genreCache.TryGetValue(type, out var map))
        {
            return map.ToDictionary(kvp => kvp.Value, kvp => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kvp.Key));
        }

        return new Dictionary<int, string>();
    }
}
