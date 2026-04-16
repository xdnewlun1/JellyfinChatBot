using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ChatBot.Api.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot.Services;

public class WatchHistoryService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<WatchHistoryService> _logger;

    private static readonly MethodInfo? _getUserById =
        typeof(IUserManager).GetMethod("GetUserById", new[] { typeof(Guid) });
    private static readonly PropertyInfo? _queryUserProp =
        typeof(InternalItemsQuery).GetProperty("User");

    public WatchHistoryService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<WatchHistoryService> logger)
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

    /// <summary>
    /// Get the user's watch history, sorted by most recently played.
    /// </summary>
    public List<WatchHistoryItem> GetWatchHistory(Guid userId, string? mediaType = null, int limit = 30)
    {
        limit = Math.Clamp(limit, 1, 100);

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
            return new List<WatchHistoryItem>();
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = itemTypes.ToArray(),
            IsPlayed = true,
            IsVirtualItem = false,
            Recursive = true,
            Limit = limit,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) }
        };
        ApplyUser(query, user);

        _logger.LogDebug("Fetching watch history for user {User}, type={Type}, limit={Limit}",
            userId, mediaType ?? "all", limit);

        var items = _libraryManager.GetItemsResult(query).Items;

        return items.Select(item => new WatchHistoryItem
        {
            Id = item.Id.ToString("N"),
            Name = item.Name,
            Overview = item.Overview,
            Year = item.ProductionYear,
            Type = item.GetBaseItemKind().ToString(),
            Genres = item.Genres?.Length > 0 ? item.Genres.ToList() : null,
            CommunityRating = item.CommunityRating
        }).ToList();
    }
}
