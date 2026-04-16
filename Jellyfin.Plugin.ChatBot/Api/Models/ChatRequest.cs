using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ChatBot.Api.Models;

public class ChatRequest
{
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();
}
