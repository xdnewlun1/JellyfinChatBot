using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ChatBot.Api.Models;

public class SeerrRequestDto
{
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("mediaId")]
    public int MediaId { get; set; }

    [JsonPropertyName("seasons")]
    public int[]? Seasons { get; set; }

    [JsonPropertyName("serverId")]
    public int? ServerId { get; set; }

    [JsonPropertyName("profileId")]
    public int? ProfileId { get; set; }

    [JsonPropertyName("rootFolder")]
    public string? RootFolder { get; set; }

    [JsonPropertyName("is4k")]
    public bool? Is4K { get; set; }
}
