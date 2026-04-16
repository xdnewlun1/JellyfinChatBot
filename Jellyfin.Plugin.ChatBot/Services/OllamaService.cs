using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ChatBot.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot.Services;

public class OllamaService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(IHttpClientFactory httpClientFactory, ILogger<OllamaService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static void ValidateUrl(string url, string name)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new InvalidOperationException($"{name} URL must be a valid http:// or https:// URL.");
        }
    }

    public async Task<OllamaChatResponse> ChatAsync(
        List<OllamaChatMessage> messages,
        List<OllamaTool>? tools,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        ValidateUrl(config.OllamaUrl, "Ollama");
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(3);

        var request = new OllamaChatRequest
        {
            Model = config.OllamaModel,
            Messages = messages,
            Stream = false,
            Tools = tools,
            Options = new OllamaOptions { Temperature = Math.Clamp(config.Temperature, 0f, 2f) }
        };

        _logger.LogDebug("Sending chat request to Ollama: {Model}", config.OllamaModel);

        var response = await client.PostAsJsonAsync(
            $"{config.OllamaUrl.TrimEnd('/')}/api/chat",
            request,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return result ?? throw new InvalidOperationException("Empty response from Ollama");
    }

    public async Task<List<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        ValidateUrl(config.OllamaUrl, "Ollama");
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var response = await client.GetAsync(
            $"{config.OllamaUrl.TrimEnd('/')}/api/tags",
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var models = new List<string>();

        if (json.TryGetProperty("models", out var modelsArray))
        {
            foreach (var model in modelsArray.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var name))
                {
                    models.Add(name.GetString() ?? string.Empty);
                }
            }
        }

        return models;
    }
}

// Ollama API types

public class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OllamaChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("tools")]
    public List<OllamaTool>? Tools { get; set; }

    [JsonPropertyName("options")]
    public OllamaOptions? Options { get; set; }
}

public class OllamaChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("tool_calls")]
    public List<OllamaToolCall>? ToolCalls { get; set; }
}

public class OllamaOptions
{
    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }
}

public class OllamaTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OllamaToolFunction Function { get; set; } = new();
}

public class OllamaToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public OllamaToolParameters Parameters { get; set; } = new();
}

public class OllamaToolParameters
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, OllamaToolProperty> Properties { get; set; } = new();

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = new();
}

public class OllamaToolProperty
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("enum")]
    public List<string>? Enum { get; set; }
}

public class OllamaToolCall
{
    [JsonPropertyName("function")]
    public OllamaToolCallFunction Function { get; set; } = new();
}

public class OllamaToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }
}

public class OllamaChatResponse
{
    [JsonPropertyName("message")]
    public OllamaChatMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }
}
