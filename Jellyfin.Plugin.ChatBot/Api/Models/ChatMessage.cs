using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ChatBot.Api.Models;

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
