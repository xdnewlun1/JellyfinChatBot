using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ChatBot.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot.Services;

// Talks to any OpenAI-compatible /chat/completions endpoint (Ollama's /v1, LM Studio,
// llama.cpp, vLLM, LiteLLM, OpenAI, Groq, OpenRouter, ...).
//
// Model selection walks a chain: the configured Model first, then each entry of
// FallbackModels in order. A model that fails for a retryable reason is put on a short
// cooldown so every subsequent chat turn doesn't pay the full timeout on a dead primary.
public class ChatCompletionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChatCompletionService> _logger;

    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

    private static readonly char[] FallbackSeparators = { ',', ';', '\n', '\r' };

    // Thinking models that aren't given a separate reasoning channel inline their
    // reasoning in the reply. Strip it so users never see the scratchpad.
    private static readonly Regex ThinkBlockRegex = new(
        @"<(think|thinking|reasoning)>.*?</\1>\s*",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // XML-style tool calls, as emitted by models trained on that convention. The
    // namespace prefix varies by model, so it is matched loosely.
    private static readonly Regex InvokeBlockRegex = new(
        @"<\s*(?:[\w.-]+:)?invoke\b[^>]*\bname\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)<\s*/\s*(?:[\w.-]+:)?invoke\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvokeParameterRegex = new(
        @"<\s*(?:[\w.-]+:)?parameter\b[^>]*\bname\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)<\s*/\s*(?:[\w.-]+:)?parameter\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Harmony channel routing used by gpt-oss: "to=search_library" or "to=functions.x".
    private static readonly Regex HarmonyRouteRegex = new(
        @"\bto\s*=\s*(?:functions\s*\.\s*)?([A-Za-z_][\w-]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Harmony control tokens (<|start|>, <|message|>, <|call|>, <|end|>, ...) and the
    // wrapper element around XML invoke blocks. Stripped so that even when nothing is
    // recovered, the user is not shown raw scaffolding.
    private static readonly Regex ControlTokenRegex = new(
        @"<\|[^|>]*\|>",
        RegexOptions.Compiled);

    // The role/route header left behind once Harmony tokens are removed, e.g. the
    // "assistant to=search_library" in "<|start|>assistant to=search_library<|message|>".
    // Only applied when control tokens were actually present, so a normal reply opening
    // with one of these words is untouched.
    private static readonly Regex HarmonyHeaderRegex = new(
        @"^\s*(?:assistant|developer|system|user|tool)\b\s*(?:to\s*=\s*(?:functions\s*\.\s*)?[\w.-]+)?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FunctionCallsWrapperRegex = new(
        @"<\s*/?\s*(?:[\w.-]+:)?function_calls\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // An opening tag with no closing tag: the whole remainder is reasoning.
    private static readonly Regex UnclosedThinkRegex = new(
        @"<(think|thinking|reasoning)>.*$",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ChatCompletionService(IHttpClientFactory httpClientFactory, ILogger<ChatCompletionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Called when the admin saves settings, so a corrected URL/key retries immediately
    // instead of waiting out a cooldown from the broken configuration.
    public static void ResetCooldowns() => _cooldowns.Clear();

    public async Task<ChatCompletionResult> ChatAsync(
        List<OpenAiChatMessage> messages,
        List<OpenAiTool>? tools,
        string? preferredModel,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        var baseUrl = ResolveBaseUrl(config.ApiBaseUrl);
        var chain = BuildModelChain(config, preferredModel);

        // Skip models we know are down, unless that would leave us with nothing to try.
        var candidates = chain.Where(m => !IsCoolingDown(baseUrl, m)).ToList();
        if (candidates.Count == 0)
        {
            candidates = chain;
        }

        Exception? lastError = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var model = candidates[i];
            try
            {
                var message = await SendWithDegradationAsync(baseUrl, model, messages, tools, config, cancellationToken)
                    .ConfigureAwait(false);
                ClearCooldown(baseUrl, model);
                return new ChatCompletionResult { Message = message, Model = model };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ChatBackendException ex) when (!ex.Retryable)
            {
                // Auth or configuration problems hit every model identically — fail fast.
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                MarkCooldown(baseUrl, model);
                _logger.LogWarning(
                    "Model '{Model}' failed ({Reason}); {Remaining} fallback(s) remaining.",
                    model,
                    ex.Message,
                    candidates.Count - i - 1);
            }
        }

        throw new ChatBackendException(
            $"All configured models failed: {string.Join(", ", candidates)}.",
            true,
            lastError);
    }

    // Backends vary in what optional parameters they accept. Rather than probing
    // capabilities, drop the optional parts one at a time on a 400 before giving up on
    // the model: reasoning_effort first (cosmetic), then tools (costs library search).
    private async Task<OpenAiChatMessage> SendWithDegradationAsync(
        string baseUrl,
        string model,
        List<OpenAiChatMessage> messages,
        List<OpenAiTool>? tools,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var wantsReasoning = config.EnableThinking;
        var hasTools = tools is { Count: > 0 };

        var attempts = new List<(bool Reasoning, bool Tools, string Describe)>
        {
            (wantsReasoning, hasTools, "full")
        };

        if (wantsReasoning)
        {
            attempts.Add((false, hasTools, "without reasoning_effort"));
        }

        if (hasTools)
        {
            attempts.Add((false, false, "without tools"));
        }

        ChatBackendException? lastBadRequest = null;

        foreach (var attempt in attempts)
        {
            try
            {
                return await PostChatAsync(
                    baseUrl,
                    model,
                    messages,
                    attempt.Tools ? tools : null,
                    attempt.Reasoning,
                    config,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ChatBackendException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                lastBadRequest = ex;
                _logger.LogWarning(
                    "Model '{Model}' rejected the request ({Reason}); retrying {Degradation}.",
                    model,
                    ex.Message,
                    attempt.Describe == "full" ? "with reduced parameters" : attempt.Describe);
            }
        }

        throw lastBadRequest!;
    }

    private async Task<OpenAiChatMessage> PostChatAsync(
        string baseUrl,
        string model,
        List<OpenAiChatMessage> messages,
        List<OpenAiTool>? tools,
        bool requestReasoning,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 10, 600)));

        var payload = new OpenAiChatRequest
        {
            Model = model,
            Messages = messages,
            Stream = false,
            Tools = tools,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null,
            Temperature = Math.Clamp(config.Temperature, 0f, 2f),
            ReasoningEffort = requestReasoning ? "medium" : null
        };

        _logger.LogDebug("Sending chat completion request: {Model}", model);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        ApplyAuth(request, config);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ChatBackendException(
                DescribeError(response.StatusCode, body),
                IsRetryable(response.StatusCode),
                null)
            {
                StatusCode = response.StatusCode
            };
        }

        var result = await response.Content
            .ReadFromJsonAsync<OpenAiChatResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var message = result?.Choices?.FirstOrDefault()?.Message
            ?? throw new ChatBackendException("Backend returned no choices.", true, null);

        StripReasoning(message, model);
        TryRecoverToolCalls(message, tools, model);
        NormalizeToolCallIds(message);
        return message;
    }

    public async Task<List<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        var baseUrl = ResolveBaseUrl(config.ApiBaseUrl);

        using var client = CreateClient(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        ApplyAuth(request, config);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ChatBackendException(
                DescribeError(response.StatusCode, body),
                IsRetryable(response.StatusCode),
                null)
            {
                StatusCode = response.StatusCode
            };
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var models = new List<string>();

        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    models.Add(id.GetString() ?? string.Empty);
                }
            }
        }

        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;
    }

    // Summary shown by the "Test AI Connection" button: reports whether the endpoint
    // answers and which configured models it actually knows about.
    public async Task<string> TestAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        var chain = BuildModelChain(config, null);

        List<string>? available = null;
        try
        {
            available = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not every OpenAI-compatible server implements /models — fall through to a probe.
            _logger.LogDebug(ex, "Model listing unavailable; falling back to a chat probe.");
        }

        if (available is { Count: > 0 })
        {
            var summary = chain.Select(m =>
                available.Contains(m, StringComparer.OrdinalIgnoreCase) ? $"{m} (ok)" : $"{m} (not listed)");
            return $"Connected. {available.Count} model(s) available. Chain: {string.Join(" -> ", summary)}";
        }

        var probe = await ChatAsync(
            new List<OpenAiChatMessage> { new() { Role = "user", Content = "ping" } },
            null,
            null,
            cancellationToken).ConfigureAwait(false);

        return $"Connected. Model listing unsupported; '{probe.Model}' answered a test prompt.";
    }

    // Configured model first, then fallbacks in order, de-duplicated.
    // A preferred model (the one already answering this request) is rotated to the front
    // so a multi-round tool conversation stays on one model instead of switching mid-thread.
    public List<string> BuildModelChain(PluginConfiguration config, string? preferredModel)
    {
        var chain = new List<string>();

        void Add(string? candidate)
        {
            var model = candidate?.Trim();
            if (!string.IsNullOrEmpty(model) && !chain.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                chain.Add(model);
            }
        }

        Add(config.Model);
        foreach (var fallback in config.FallbackModels.Split(FallbackSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            Add(fallback);
        }

        if (chain.Count == 0)
        {
            throw new ChatBackendException("No model configured. Set a model in the ChatBot plugin settings.", false, null);
        }

        if (!string.IsNullOrWhiteSpace(preferredModel))
        {
            var preferred = preferredModel.Trim();
            var index = chain.FindIndex(m => string.Equals(m, preferred, StringComparison.OrdinalIgnoreCase));
            if (index > 0)
            {
                chain = chain.Skip(index).Concat(chain.Take(index)).ToList();
            }
            else if (index < 0)
            {
                chain.Insert(0, preferred);
            }
        }

        return chain;
    }

    private HttpClient CreateClient(TimeSpan timeout)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = timeout;
        return client;
    }

    private static void ApplyAuth(HttpRequestMessage request, PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
        }
    }

    // Accepts "http://host:11434" (bare host — the common Ollama mistake) and appends the
    // /v1 prefix, but leaves any explicit path alone so gateway URLs keep working.
    internal static string ResolveBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ChatBackendException(
                "AI endpoint URL must be a valid http:// or https:// URL.",
                false,
                null);
        }

        var normalized = url.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(uri.AbsolutePath.Trim('/')))
        {
            normalized += "/v1";
        }

        return normalized;
    }

    // Servers differ on whether tool calls carry an id; the tool-result message must
    // reference one, so synthesize a stable id when it is missing.
    private static void NormalizeToolCallIds(OpenAiChatMessage message)
    {
        if (message.ToolCalls == null)
        {
            return;
        }

        for (var i = 0; i < message.ToolCalls.Count; i++)
        {
            if (string.IsNullOrEmpty(message.ToolCalls[i].Id))
            {
                message.ToolCalls[i].Id = $"call_{i}_{Guid.NewGuid():N}";
            }
        }
    }

    // Keeps model reasoning out of the user-visible reply, whether the backend split it
    // into its own field or left it inline as <think> tags. The message is echoed back
    // into the next request, so the reasoning fields are cleared rather than forwarded.
    private void StripReasoning(OpenAiChatMessage message, string model)
    {
        var separated = message.ReasoningContent ?? message.Reasoning;
        message.ReasoningContent = null;
        message.Reasoning = null;

        if (!string.IsNullOrWhiteSpace(separated))
        {
            _logger.LogDebug("Model '{Model}' returned {Length} chars of separated reasoning.", model, separated.Length);
        }

        if (string.IsNullOrEmpty(message.Content))
        {
            return;
        }

        var cleaned = ThinkBlockRegex.Replace(message.Content, string.Empty);
        cleaned = UnclosedThinkRegex.Replace(cleaned, string.Empty);
        cleaned = cleaned.Trim();

        if (cleaned.Length != message.Content.Length)
        {
            _logger.LogDebug("Stripped inline reasoning tags from '{Model}' reply.", model);
        }

        message.Content = cleaned;
    }

    // Not every backend parses a model's tool-call syntax: Ollama templates without a
    // tool parser and vLLM without --enable-auto-tool-choice both hand the call back as
    // plain text, so the user sees raw JSON in the chat bubble instead of results.
    // Recover the calls when the response carried none but the content looks like one.
    private void TryRecoverToolCalls(OpenAiChatMessage message, List<OpenAiTool>? tools, string model)
    {
        if (message.ToolCalls is { Count: > 0 } || tools is not { Count: > 0 })
        {
            return;
        }

        var content = message.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        // '{' covers JSON calls, '<' covers XML invoke blocks and Harmony control
        // tokens. Anything with neither is ordinary prose.
        if (content.IndexOf('{') < 0 && content.IndexOf('<') < 0)
        {
            return;
        }

        // Only names the model was actually offered count, so an ordinary reply that
        // happens to contain JSON is never mistaken for a tool call.
        var known = new HashSet<string>(tools.Select(t => t.Function.Name), StringComparer.OrdinalIgnoreCase);

        // Models emit tool calls as text in several conventions; try each in turn.
        if (TryRecoverFromJson(content, known, out var calls, out var remainder)
            || TryRecoverFromInvokeBlocks(content, known, out calls, out remainder)
            || TryRecoverFromHarmony(content, known, out calls, out remainder))
        {
            message.ToolCalls = calls;
            message.Content = Tidy(remainder);

            _logger.LogWarning(
                "Model '{Model}' emitted {Count} tool call(s) as text rather than structured output — "
                + "the backend is not parsing tool calls for this model. Recovered them from the reply.",
                model,
                calls.Count);
            return;
        }

        // Nothing recovered, but scaffolding may still be showing. Never leave raw
        // control tokens in a reply the user reads.
        var tidied = Tidy(content);
        if (tidied.Length != content.Length)
        {
            _logger.LogWarning("Stripped tool-call scaffolding from '{Model}' reply.", model);
            message.Content = tidied;
        }
    }

    private static string Tidy(string text)
    {
        var hadControlTokens = ControlTokenRegex.IsMatch(text);

        // A space, not an empty string: removing "<|message|>" outright would weld the
        // route name onto the reply text ("to=whatever" + "hi" -> "whateverhi"), and the
        // header pattern would then eat the reply along with the route.
        text = ControlTokenRegex.Replace(text, " ");
        text = FunctionCallsWrapperRegex.Replace(text, string.Empty);

        if (hadControlTokens)
        {
            text = HarmonyHeaderRegex.Replace(text, string.Empty);
            text = Regex.Replace(text, "[ \t]{2,}", " ");
        }

        return text.Trim();
    }

    private static bool TryRecoverFromJson(
        string content,
        HashSet<string> known,
        out List<OpenAiToolCall> calls,
        out string remainder)
    {
        foreach (var (json, start, length) in EnumerateJsonCandidates(content))
        {
            if (TryParseToolCalls(json, known, out calls, out var embeddedContent))
            {
                remainder = embeddedContent ?? content[..start] + content[(start + length)..];
                return true;
            }
        }

        calls = new List<OpenAiToolCall>();
        remainder = content;
        return false;
    }

    // <invoke name="search_library"><parameter name="query">car</parameter>...</invoke>
    private static bool TryRecoverFromInvokeBlocks(
        string content,
        HashSet<string> known,
        out List<OpenAiToolCall> calls,
        out string remainder)
    {
        calls = new List<OpenAiToolCall>();
        remainder = content;

        var matches = InvokeBlockRegex.Matches(content);
        if (matches.Count == 0)
        {
            return false;
        }

        var consumed = new List<(int Start, int Length)>();

        foreach (Match match in matches)
        {
            var name = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (!known.Contains(name))
            {
                continue;
            }

            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match parameter in InvokeParameterRegex.Matches(match.Groups[2].Value))
            {
                var key = WebUtility.HtmlDecode(parameter.Groups[1].Value).Trim();
                if (key.Length > 0)
                {
                    // Values stay strings; the argument getters coerce them.
                    arguments[key] = WebUtility.HtmlDecode(parameter.Groups[2].Value).Trim();
                }
            }

            calls.Add(new OpenAiToolCall
            {
                Function = new OpenAiToolCallFunction
                {
                    Name = name,
                    Arguments = JsonSerializer.Serialize(arguments)
                }
            });
            consumed.Add((match.Index, match.Length));
        }

        if (calls.Count == 0)
        {
            return false;
        }

        remainder = RemoveSpans(content, consumed);
        return true;
    }

    // Harmony routing: "<|start|>assistant to=search_library<|message|>{...}<|call|>"
    private static bool TryRecoverFromHarmony(
        string content,
        HashSet<string> known,
        out List<OpenAiToolCall> calls,
        out string remainder)
    {
        calls = new List<OpenAiToolCall>();
        remainder = content;

        var route = HarmonyRouteRegex.Match(content);
        if (!route.Success)
        {
            return false;
        }

        var name = route.Groups[1].Value;
        if (!known.Contains(name))
        {
            return false;
        }

        // Arguments are whatever JSON object follows the route, if any.
        var arguments = "{}";
        var consumed = new List<(int Start, int Length)> { (route.Index, route.Length) };

        foreach (var (json, start, length) in EnumerateJsonCandidates(content[route.Index..]))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    arguments = json;
                    consumed.Add((route.Index + start, length));
                }
            }
            catch (JsonException)
            {
                // Not an arguments object; fall through to the empty default.
            }

            break;
        }

        calls.Add(new OpenAiToolCall
        {
            Function = new OpenAiToolCallFunction { Name = name, Arguments = arguments }
        });

        remainder = RemoveSpans(content, consumed);
        return true;
    }

    private static string RemoveSpans(string text, List<(int Start, int Length)> spans)
    {
        foreach (var span in spans.OrderByDescending(s => s.Start))
        {
            text = text[..span.Start] + text[(span.Start + span.Length)..];
        }

        return text;
    }

    // Balanced JSON spans in order of appearance. Handles a bare object, a fenced code
    // block, and marker-wrapped forms (<tool_call>{...}</tool_call>, [TOOL_CALLS] [...])
    // alike, since all that matters is the JSON between the delimiters.
    private static IEnumerable<(string Json, int Start, int Length)> EnumerateJsonCandidates(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{' && text[i] != '[')
            {
                continue;
            }

            var end = FindBalancedEnd(text, i);
            if (end < 0)
            {
                continue;
            }

            yield return (text[i..(end + 1)], i, end - i + 1);
            i = end;
        }
    }

    private static int FindBalancedEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (depth == 0) return i;
                    if (depth < 0) return -1;
                    break;
            }
        }

        return -1;
    }

    // Accepts the shapes models actually emit: a whole assistant message object with a
    // tool_calls array (what Gemma produces), a bare array of calls, or a single call.
    private static bool TryParseToolCalls(
        string json,
        HashSet<string> known,
        out List<OpenAiToolCall> calls,
        out string? embeddedContent)
    {
        calls = new List<OpenAiToolCall>();
        embeddedContent = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement array;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                array = toolCalls;

                // The model wrapped its own prose in the blob; keep it as the reply text.
                if (root.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.String)
                {
                    embeddedContent = inner.GetString();
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryReadToolCall(root, known, out var single))
                {
                    calls.Add(single);
                    return true;
                }

                return false;
            }
            else
            {
                return false;
            }

            foreach (var element in array.EnumerateArray())
            {
                if (TryReadToolCall(element, known, out var call))
                {
                    calls.Add(call);
                }
            }

            return calls.Count > 0;
        }
    }

    private static bool TryReadToolCall(JsonElement element, HashSet<string> known, out OpenAiToolCall call)
    {
        call = new OpenAiToolCall();

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Either {name, arguments} or the nested {function: {name, arguments}} form.
        var fn = element;
        if (element.TryGetProperty("function", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            fn = nested;
        }

        if (!fn.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var name = nameElement.GetString();
        if (string.IsNullOrEmpty(name) || !known.Contains(name))
        {
            return false;
        }

        var arguments = "{}";
        if (fn.TryGetProperty("arguments", out var argsElement) || fn.TryGetProperty("parameters", out argsElement))
        {
            arguments = argsElement.ValueKind == JsonValueKind.String
                ? argsElement.GetString() ?? "{}"
                : argsElement.GetRawText();
        }

        call = new OpenAiToolCall
        {
            Id = element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : string.Empty,
            Function = new OpenAiToolCallFunction { Name = name, Arguments = arguments }
        };

        return true;
    }

    // 401/403 mean the key or endpoint is wrong — identical for every model, so don't
    // burn the whole chain on it. Everything else is worth another model's shot.
    private static bool IsRetryable(HttpStatusCode status) =>
        status != HttpStatusCode.Unauthorized && status != HttpStatusCode.Forbidden;

    private static string DescribeError(HttpStatusCode status, string body)
    {
        var detail = ExtractErrorMessage(body);
        return string.IsNullOrEmpty(detail)
            ? $"HTTP {(int)status} {status}"
            : $"HTTP {(int)status} {status}: {detail}";
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string text = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                text = error.ValueKind switch
                {
                    JsonValueKind.String => error.GetString() ?? body,
                    JsonValueKind.Object when error.TryGetProperty("message", out var m) => m.GetString() ?? body,
                    _ => body
                };
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg))
            {
                text = msg.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body (HTML error page, plain text) — use it as-is.
        }

        text = text.Trim();
        return text.Length > 300 ? text[..300] + "..." : text;
    }

    private static string CooldownKey(string baseUrl, string model) => baseUrl + "|" + model;

    private static bool IsCoolingDown(string baseUrl, string model) =>
        _cooldowns.TryGetValue(CooldownKey(baseUrl, model), out var until) && DateTime.UtcNow < until;

    private static void MarkCooldown(string baseUrl, string model) =>
        _cooldowns[CooldownKey(baseUrl, model)] = DateTime.UtcNow + CooldownDuration;

    private static void ClearCooldown(string baseUrl, string model) =>
        _cooldowns.TryRemove(CooldownKey(baseUrl, model), out _);
}

public sealed class ChatCompletionResult
{
    public OpenAiChatMessage Message { get; init; } = new();

    // The model that actually answered, so the caller can stay on it for follow-up rounds.
    public string Model { get; init; } = string.Empty;
}

public sealed class ChatBackendException : Exception
{
    public ChatBackendException(string message, bool retryable, Exception? inner)
        : base(message, inner)
    {
        Retryable = retryable;
    }

    // Whether trying a different model could plausibly succeed.
    public bool Retryable { get; }

    public HttpStatusCode? StatusCode { get; init; }
}
