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

        // When post-filters are active, fetch more items so we have enough after filtering
        bool hasPostFilters = yearMin.HasValue || yearMax.HasValue || minCommunityRating.HasValue;
        var queryLimit = hasPostFilters ? Math.Min(searchLimit * 10, 500) : searchLimit;

        var internalQuery = new InternalItemsQuery
        {
            IncludeItemTypes = itemTypes.ToArray(),
            Limit = queryLimit,
            IsVirtualItem = false,
            Recursive = true
        };
        ApplyUser(internalQuery, user);

        if (!string.IsNullOrWhiteSpace(query))
        {
            internalQuery.SearchTerm = query;
        }

        if (!string.IsNullOrWhiteSpace(genre))
        {
            internalQuery.Genres = new[] { genre };
        }

        if (!string.IsNullOrWhiteSpace(tags))
        {
            internalQuery.Tags = new[] { tags };
        }

        _logger.LogDebug("Library search: qLen={QLen} type={Type} genre={GenrePresent} yearRange={YearMin}-{YearMax} rating>={Rating}",
            (query ?? string.Empty).Length, mediaType ?? "all", !string.IsNullOrEmpty(genre),
            yearMin, yearMax, minCommunityRating);

        var items = _libraryManager.GetItemsResult(internalQuery).Items;

        // If a query was supplied alongside a genre, SearchTerm may be ignored when genres are set.
        // Fall back to a manual overview/title filter so "space" can match overview text.
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            items = items.Where(i =>
                (i.Name != null && i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (i.Overview != null && i.Overview.Contains(q, StringComparison.OrdinalIgnoreCase))
            ).ToArray();
        }

        IEnumerable<BaseItem> filtered = items;

        // Post-filter by year range
        if (yearMin.HasValue)
        {
            filtered = filtered.Where(i => i.ProductionYear.HasValue && i.ProductionYear.Value >= yearMin.Value);
        }

        if (yearMax.HasValue)
        {
            filtered = filtered.Where(i => i.ProductionYear.HasValue && i.ProductionYear.Value <= yearMax.Value);
        }

        // Post-filter by community rating
        if (minCommunityRating.HasValue)
        {
            filtered = filtered.Where(i => i.CommunityRating.HasValue && i.CommunityRating.Value >= minCommunityRating.Value);
        }

        // When filters are active, sort by community rating so the best content surfaces first
        if (hasPostFilters || !string.IsNullOrWhiteSpace(genre))
        {
            filtered = filtered.OrderByDescending(i => i.CommunityRating ?? 0f);
        }

        return filtered.Take(searchLimit).Select(item => new LibrarySearchResult
        {
            Id = item.Id.ToString("N"),
            Name = item.Name,
            Overview = item.Overview,
            Year = item.ProductionYear,
            Type = item.GetBaseItemKind().ToString(),
            ImageUrl = item.PrimaryImagePath != null
                ? $"/Items/{item.Id}/Images/Primary"
                : null,
            Genres = item.Genres?.Length > 0 ? item.Genres.ToList() : null,
            CommunityRating = item.CommunityRating
        }).ToList();
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
