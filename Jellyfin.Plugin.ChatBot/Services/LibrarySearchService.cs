using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ChatBot.Api.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot.Services;

public class LibrarySearchService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<LibrarySearchService> _logger;

    // Resolved via reflection so we don't bake a direct reference to the User type, which moved assemblies
    // between Jellyfin 10.10 (Jellyfin.Data.Entities.User) and 10.11 (Jellyfin.Database.Implementations.Entities.User).
    // Static references to that type would cause TypeLoadException at JIT time when running against the other version.
    private static readonly MethodInfo? _getUserById =
        typeof(IUserManager).GetMethod("GetUserById", new[] { typeof(Guid) });
    private static readonly PropertyInfo? _queryUserProp =
        typeof(InternalItemsQuery).GetProperty("User");

    public LibrarySearchService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<LibrarySearchService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    private object? ResolveUser(Guid userId)
    {
        if (userId == Guid.Empty || _getUserById == null) return null;
        try
        {
            return _getUserById.Invoke(_userManager, new object[] { userId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Jellyfin user {User}", userId);
            return null;
        }
    }

    private static void ApplyUser(InternalItemsQuery query, object user)
    {
        _queryUserProp?.SetValue(query, user);
    }

    // Words that carry no signal in a media query — dropping them keeps "sad movie"
    // from matching every item whose overview contains the word "movie".
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "about", "that", "this", "from", "some", "something",
        "anything", "any", "like", "similar", "movie", "movies", "film", "films", "show",
        "shows", "series", "episode", "episodes", "watch", "watching", "please", "give",
        "find", "looking", "want", "have", "has", "are", "was", "were", "you", "got"
    };

    private static readonly char[] WordSeparators =
    {
        ' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '"', '\'', '\u2019',
        '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_', '&', '+', '*', '#', '@', '|'
    };

    internal static List<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        foreach (var raw in query.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length < 3 || StopWords.Contains(token))
            {
                continue;
            }

            var stem = Stem(token);
            if (stem.Length > 0 && !tokens.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                tokens.Add(stem);
            }
        }

        return tokens;
    }

    // Crude suffix stripping, enough to collapse race/races/racing/racer to one stem so
    // a query word matches the forms that actually appear in overview text.
    internal static string Stem(string word)
    {
        var w = word.ToLowerInvariant();

        if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) w = w[..^3];
        else if (w.Length > 4 && w.EndsWith("ers", StringComparison.Ordinal)) w = w[..^3];
        else if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) w = w[..^2];
        else if (w.Length > 4 && w.EndsWith("er", StringComparison.Ordinal)) w = w[..^2];
        else if (w.Length > 3 && w.EndsWith("es", StringComparison.Ordinal)) w = w[..^2];
        else if (w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal)) w = w[..^1];

        if (w.Length > 3 && w.EndsWith("e", StringComparison.Ordinal)) w = w[..^1];

        return w;
    }

    private static HashSet<string> StemWords(string? text)
    {
        var stems = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return stems;
        }

        foreach (var word in text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length >= 2)
            {
                stems.Add(Stem(word));
            }
        }

        return stems;
    }

    // Whole-word (stemmed) matching, so "car" no longer matches "Oscar" or "Carol".
    // A prefix is allowed only for longer stems, where it is discriminating enough to
    // be worth the recall ("spac" -> "spaceship") rather than noise ("car" -> "carol").
    private static bool Matches(HashSet<string> stems, string token)
    {
        if (stems.Contains(token))
        {
            return true;
        }

        return token.Length >= 4 && stems.Any(s => s.StartsWith(token, StringComparison.Ordinal));
    }

    // Weighted so a title hit outranks an overview hit, an exact phrase outranks any
    // number of scattered hits, and — most importantly for relevance — an item matching
    // every word of the request outranks one matching a single word by coincidence.
    // "race car" should surface Ford v Ferrari, not RuPaul's Drag Race.
    internal static int ScoreText(string? name, string? overview, IEnumerable<string>? facets, IReadOnlyList<string> tokens, string? phrase)
    {
        var score = 0;

        // Only a multi-word phrase earns the bonus. For a single word the substring test
        // is the very false positive the stemmed word matching below exists to avoid —
        // "car" would otherwise score a direct hit on "Oscar".
        if (!string.IsNullOrWhiteSpace(phrase) && phrase.Trim().Contains(' '))
        {
            var trimmed = phrase.Trim();
            if (name != null && name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) score += 40;
            else if (overview != null && overview.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) score += 20;
        }

        if (tokens.Count == 0)
        {
            return score;
        }

        var nameStems = StemWords(name);
        var overviewStems = StemWords(overview);
        var facetStems = new HashSet<string>(StringComparer.Ordinal);
        if (facets != null)
        {
            foreach (var facet in facets)
            {
                foreach (var stem in StemWords(facet))
                {
                    facetStems.Add(stem);
                }
            }
        }

        var matched = 0;
        foreach (var token in tokens)
        {
            if (Matches(nameStems, token)) { score += 4; matched++; }
            else if (Matches(overviewStems, token)) { score += 2; matched++; }
            else if (Matches(facetStems, token)) { score += 1; matched++; }
        }

        if (matched == 0)
        {
            return score;
        }

        // Coverage dominates the per-hit weights: matching two requested words beats
        // matching one, however prominently that one appears.
        score += matched * 10;

        if (matched == tokens.Count && tokens.Count > 1)
        {
            score += 15;
        }

        return score;
    }

    private InternalItemsQuery BuildQuery(BaseItemKind[] itemTypes, object user, string? genre, string? tags, int limit)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = itemTypes,
            Limit = limit,
            IsVirtualItem = false,
            Recursive = true
        };
        ApplyUser(query, user);

        if (!string.IsNullOrWhiteSpace(genre))
        {
            query.Genres = new[] { genre };
        }

        if (!string.IsNullOrWhiteSpace(tags))
        {
            query.Tags = new[] { tags };
        }

        return query;
    }

    public List<LibrarySearchResult> Search(
        Guid userId,
        string? query,
        string? mediaType = null,
        string? genre = null,
        int? yearMin = null,
        int? yearMax = null,
        string? tags = null,
        double? minCommunityRating = null)
    {
        var config = Plugin.Instance!.Configuration;
        var searchLimit = Math.Clamp(config.SearchResultLimit, 1, 50);

        var itemTypes = new List<BaseItemKind>();
        if (string.IsNullOrEmpty(mediaType) || mediaType.Equals("movie", StringComparison.OrdinalIgnoreCase))
        {
            itemTypes.Add(BaseItemKind.Movie);
        }

        if (string.IsNullOrEmpty(mediaType) || mediaType.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            itemTypes.Add(BaseItemKind.Series);
        }

        var user = ResolveUser(userId);
        if (user == null)
        {
            // Fail closed: without a user we cannot apply library ACLs, so return nothing.
            return new List<LibrarySearchResult>();
        }

        var types = itemTypes.ToArray();
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var phrase = hasQuery ? query!.Trim() : null;
        var tokens = hasQuery ? Tokenize(phrase!) : new List<string>();

        _logger.LogDebug("Library search: qLen={QLen} tokens={Tokens} type={Type} genre={GenrePresent} yearRange={YearMin}-{YearMax} rating>={Rating}",
            (query ?? string.Empty).Length, tokens.Count, mediaType ?? "all", !string.IsNullOrEmpty(genre),
            yearMin, yearMax, minCommunityRating);

        // Two passes, merged by id. Jellyfin's own SearchTerm is authoritative for
        // titles; the wider scan is what makes thematic queries work, because
        // SearchTerm does not look at overview text.
        var candidates = new Dictionary<Guid, (BaseItem Item, int Score)>();

        void Offer(BaseItem item, int score)
        {
            if (score <= 0)
            {
                return;
            }

            if (!candidates.TryGetValue(item.Id, out var existing) || score > existing.Score)
            {
                candidates[item.Id] = (item, score);
            }
        }

        if (hasQuery)
        {
            var titleQuery = BuildQuery(types, user, genre, tags, Math.Min(searchLimit * 5, 100));
            titleQuery.SearchTerm = phrase;
            foreach (var item in _libraryManager.GetItemsResult(titleQuery).Items)
            {
                // Server-vetted match: score it on content, but never below a floor, so a
                // title Jellyfin matched cannot be filtered out by our own heuristics.
                Offer(item, Math.Max(ScoreText(item.Name, item.Overview, item.Genres?.Concat(item.Tags ?? Array.Empty<string>()), tokens, phrase), 8));
            }
        }

        // Wide scan for thematic matching, and for genre/tag-only browsing.
        var scanLimit = hasQuery || yearMin.HasValue || yearMax.HasValue || minCommunityRating.HasValue
            ? Math.Min(Math.Max(searchLimit * 20, 200), 500)
            : searchLimit;
        var scanQuery = BuildQuery(types, user, genre, tags, scanLimit);
        foreach (var item in _libraryManager.GetItemsResult(scanQuery).Items)
        {
            // With no usable query this is a plain browse, so everything qualifies.
            Offer(item, tokens.Count == 0 && string.IsNullOrWhiteSpace(phrase)
                ? 1
                : ScoreText(item.Name, item.Overview, item.Genres?.Concat(item.Tags ?? Array.Empty<string>()), tokens, phrase));
        }

        IEnumerable<(BaseItem Item, int Score)> filtered = candidates.Values;

        if (yearMin.HasValue)
        {
            filtered = filtered.Where(c => c.Item.ProductionYear.HasValue && c.Item.ProductionYear.Value >= yearMin.Value);
        }

        if (yearMax.HasValue)
        {
            filtered = filtered.Where(c => c.Item.ProductionYear.HasValue && c.Item.ProductionYear.Value <= yearMax.Value);
        }

        if (minCommunityRating.HasValue)
        {
            filtered = filtered.Where(c => c.Item.CommunityRating.HasValue && c.Item.CommunityRating.Value >= minCommunityRating.Value);
        }

        return filtered
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Item.CommunityRating ?? 0f)
            .Take(searchLimit)
            .Select(c => new LibrarySearchResult
            {
                Id = c.Item.Id.ToString("N"),
                Name = c.Item.Name,
                Overview = c.Item.Overview,
                Year = c.Item.ProductionYear,
                Type = c.Item.GetBaseItemKind().ToString(),
                ImageUrl = c.Item.PrimaryImagePath != null
                    ? $"/Items/{c.Item.Id}/Images/Primary"
                    : null,
                Genres = c.Item.Genres?.Length > 0 ? c.Item.Genres.ToList() : null,
                CommunityRating = c.Item.CommunityRating
            })
            .ToList();
    }

    public List<string> GetGenres(Guid userId, string? mediaType = null)
    {
        var itemTypes = new List<BaseItemKind>();
        if (string.IsNullOrEmpty(mediaType) || mediaType.Equals("movie", StringComparison.OrdinalIgnoreCase))
        {
            itemTypes.Add(BaseItemKind.Movie);
        }
        if (string.IsNullOrEmpty(mediaType) || mediaType.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            itemTypes.Add(BaseItemKind.Series);
        }

        var user = ResolveUser(userId);
        if (user == null) return new List<string>();

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = itemTypes.ToArray(),
            IsVirtualItem = false,
            Recursive = true
        };
        ApplyUser(query, user);

        var items = _libraryManager.GetItemsResult(query).Items;

        return items
            .SelectMany(i => i.Genres ?? Array.Empty<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g)
            .ToList();
    }
}
