using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.AI;

/// <summary>
/// Thrown when the Claude API returns a successful (2xx) response that contains
/// no usable text content. This is distinct from an HTTP error: the call
/// "succeeded" but produced nothing to use. Callers treat it like a transient
/// failure so the surrounding retry/catch-up logic engages rather than emitting
/// an empty artifact.
/// </summary>
public sealed class EmptyCompletionException(string? stopReason)
    : Exception($"Claude returned an empty completion (stop_reason={stopReason ?? "null"})")
{
    public string? StopReason { get; } = stopReason;
}

public interface IAiService
{
    /// <summary>
    /// Single-shot completion. Returns the assistant's text content.
    /// Throws on HTTP/API errors — caller decides retry policy.
    /// </summary>
    Task<AiResult> GenerateAsync(
        string systemPrompt,
        string userMessage,
        int maxTokens = 1500,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a Claude completion. <see cref="Model"/> is the model id returned
/// by the API (which may be a snapshot like "claude-sonnet-4-6-20250529" even
/// when the request asked for an alias like "claude-sonnet-4-6"), so callers
/// that surface it to users get the actual model that produced the response.
/// </summary>
public sealed record AiResult(string Text, string Model, int InputTokens, int OutputTokens)
{
    /// <summary>Estimated USD cost at Sonnet 4.6 list pricing ($3/$15 per MTok).</summary>
    public decimal EstimatedCostUsd =>
        (InputTokens * 3m / 1_000_000m) + (OutputTokens * 15m / 1_000_000m);
}

public sealed class ClaudeOptions
{
    public required string ApiKey { get; set; }
    public string Model { get; set; } = "claude-sonnet-5";
    public string ApiVersion { get; set; } = "2023-06-01";
    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1/";
}

public sealed class ClaudeAiService(
    HttpClient httpClient,
    IOptions<ClaudeOptions> options,
    ILogger<ClaudeAiService> logger) : IAiService
{
    private readonly ClaudeOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<AiResult> GenerateAsync(
        string systemPrompt,
        string userMessage,
        int maxTokens = 1500,
        CancellationToken ct = default)
    {
        var request = new MessagesRequest
        {
            Model = _options.Model,
            MaxTokens = maxTokens,
            System = systemPrompt,
            Messages = [new Message("user", userMessage)]
        };

        using var response = await httpClient.PostAsJsonAsync("messages", request, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "Claude API error {Status}: {Body}",
                response.StatusCode, error);
            // Pass the status code through (not just in the message) so callers
            // can distinguish transient failures (429/5xx) worth retrying from
            // non-transient ones (401 invalid-api-key, other 4xx) that should
            // fail fast. WeeklyOfficerBriefingService.IsTransient reads this.
            throw new HttpRequestException(
                $"Claude API returned {(int)response.StatusCode} {response.StatusCode}: {error}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<MessagesResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty response body from Claude API");

        var text = string.Join("\n",
            payload.Content
                .Where(c => c.Type == "text" && !string.IsNullOrEmpty(c.Text))
                .Select(c => c.Text));

        // A 2xx response with no usable text block is not a valid completion:
        // it happens when the model stops with no text (e.g. stop_reason
        // "max_tokens" before any text, "pause_turn", a tool-only turn, or a
        // transient server-side glitch). Returning "" here would let callers
        // post an empty artifact (a 0-byte briefing) and consider the run a
        // success. Throw instead so the caller's retry/catch-up logic engages,
        // and log enough to diagnose which case it was.
        if (string.IsNullOrWhiteSpace(text))
        {
            var blockTypes = string.Join(",", payload.Content.Select(c => c.Type));
            logger.LogWarning(
                "Claude {Model} returned no text content (stop_reason={StopReason}, blocks=[{Blocks}], {Output} out tokens)",
                string.IsNullOrEmpty(payload.Model) ? _options.Model : payload.Model,
                payload.StopReason ?? "null",
                blockTypes,
                payload.Usage.OutputTokens);
            throw new EmptyCompletionException(payload.StopReason);
        }

        // Fall back to the configured model id if the API ever omits the field;
        // the response field is required per the Messages API spec, so this is defensive.
        var modelUsed = string.IsNullOrEmpty(payload.Model) ? _options.Model : payload.Model;

        var result = new AiResult(text, modelUsed, payload.Usage.InputTokens, payload.Usage.OutputTokens);

        // Log stop_reason alongside usage. This is the single field that tells a
        // truncated answer ("max_tokens" — raise MaxOutputTokens) apart from one
        // the model chose to end early ("end_turn" — a prompt/model-behavior
        // issue, not a length cap). Without it, a short briefing is ambiguous.
        logger.LogInformation(
            "Claude {Model} usage: {Input} in + {Output} out tokens, stop_reason={StopReason} (~{Cost:C4})",
            result.Model, result.InputTokens, result.OutputTokens,
            payload.StopReason ?? "null", result.EstimatedCostUsd);

        return result;
    }

    // --- DTOs (private — implementation detail) -----------------------------

    private sealed record MessagesRequest
    {
        public required string Model { get; init; }
        public required int MaxTokens { get; init; }
        public string? System { get; init; }
        public required IReadOnlyList<Message> Messages { get; init; }
    }

    private sealed record Message(string Role, string Content);

    private sealed record MessagesResponse
    {
        public string? Model { get; init; }
        public string? StopReason { get; init; }
        public required IReadOnlyList<ContentBlock> Content { get; init; }
        public required Usage Usage { get; init; }
    }

    private sealed record ContentBlock(string Type, string? Text);

    private sealed record Usage(int InputTokens, int OutputTokens);
}