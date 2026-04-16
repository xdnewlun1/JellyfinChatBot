using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ChatBot.Api.Models;

public class ChatResponse
{
    [JsonPropertyName("reply")]
    public string Reply { get; set; } = string.Empty;

    [JsonPropertyName("libraryResults")]
    public List<LibrarySearchResult>? LibraryResults { get; set; }

    [JsonPropertyName("seerrResults")]
    public List<SeerrSearchResult>? SeerrResults { get; set; }

    [JsonPropertyName("tmdbResults")]
    public List<TmdbResult>? TmdbResults { get; set; }
}
