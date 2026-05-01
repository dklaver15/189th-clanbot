using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.AI;

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

public sealed record AiResult(string Text, int InputTokens, int OutputTokens)
{
    /// <summary>Estimated USD cost at Sonnet 4.6 list pricing ($3/$15 per MTok).</summary>
    public decimal EstimatedCostUsd =>
        (InputTokens * 3m / 1_000_000m) + (OutputTokens * 15m / 1_000_000m);
}

public sealed class ClaudeOptions
{
    public required string ApiKey { get; set; }
    public string Model { get; set; } = "claude-sonnet-4-6";
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
            throw new HttpRequestException(
                $"Claude API returned {(int)response.StatusCode} {response.StatusCode}: {error}");
        }

        var payload = await response.Content.ReadFromJsonAsync<MessagesResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty response body from Claude API");

        var text = string.Join("\n",
            payload.Content
                .Where(c => c.Type == "text" && !string.IsNullOrEmpty(c.Text))
                .Select(c => c.Text));

        var result = new AiResult(text, payload.Usage.InputTokens, payload.Usage.OutputTokens);

        logger.LogInformation(
            "Claude {Model} usage: {Input} in + {Output} out tokens (~{Cost:C4})",
            _options.Model, result.InputTokens, result.OutputTokens, result.EstimatedCostUsd);

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
        public required IReadOnlyList<ContentBlock> Content { get; init; }
        public required Usage Usage { get; init; }
    }

    private sealed record ContentBlock(string Type, string? Text);

    private sealed record Usage(int InputTokens, int OutputTokens);
}