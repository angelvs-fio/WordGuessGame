using System.Text.Json;

namespace WordGuessGame.Services;

public record AnswerSuggestionResult(IReadOnlyList<string>? Suggestions, string? Error, int StatusCode)
{
    public bool IsSuccess => Error is null;
}

public class AnswerSuggestionService(IConfiguration config, IHttpClientFactory httpClientFactory)
{
    private const int WantedCount = 9;
    private const int MaxWordsPerSuggestion = 2;

    private readonly object _historyLock = new();
    private string _historyTopic = "";
    private const int MaxExcludedInPrompt = 40;
    private readonly List<string> _history = [];

    private List<string> GetHistory(string topic)
    {
        lock (_historyLock)
        {
            if (!string.Equals(_historyTopic, topic.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _historyTopic = topic.Trim();
                _history.Clear();
            }
            return [.. _history];
        }
    }

    private void RememberSuggestions(string topic, IEnumerable<string> items)
    {
        lock (_historyLock)
        {
            if (string.Equals(_historyTopic, topic.Trim(), StringComparison.OrdinalIgnoreCase))
                _history.AddRange(items);
        }
    }

    public async Task<AnswerSuggestionResult> SuggestAsync(string topic)
    {
        var apiKey = config["GROQ_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return new AnswerSuggestionResult(null, "Groq API key not configured.", 503);

        if (string.IsNullOrWhiteSpace(topic))
            return new AnswerSuggestionResult(null, "Set a topic first.", 400);

        var history = GetHistory(topic);
        var previous = new HashSet<string>(history, StringComparer.OrdinalIgnoreCase);
        var avoid = previous.Count > 0
            ? $"- Do NOT use any of these, they were already suggested: {string.Join(", ", history.TakeLast(MaxExcludedInPrompt))}.\n"
            : "";

        var prompt =
            $"The topic of a drawing-and-guessing game is: \"{topic}\".\n" +
            $"Give exactly {WantedCount} different things that match this topic and are easy to draw.\n" +
            "Rules:\n" +
            $"- Each item must be 1 or {MaxWordsPerSuggestion} words only.\n" +
            "- Items must be concrete, well known and clearly related to the topic.\n" +
            "- Do NOT repeat the topic itself and do NOT include duplicates.\n" +
            avoid +
            "- Do NOT add numbering, explanations or punctuation.\n" +
            "Respond with JSON only, in this format: {\"suggestions\": [\"Item one\", \"Item two\"]}";

        var client = httpClientFactory.CreateClient();
        List<string> best = [];

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var requestBody = new
                {
                    model = "openai/gpt-oss-120b",
                    temperature = 0.8,
                    response_format = new { type = "json_object" },
                    messages = new[]
                    {
                        new { role = "system", content = "You are a helper for a drawing game. Always respond with valid JSON only." },
                        new { role = "user", content = prompt }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                request.Content = JsonContent.Create(requestBody);

                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var hint = (int)response.StatusCode switch
                    {
                        401 => "Invalid API key. Check your Groq:ApiKey config.",
                        429 => "Rate limit exceeded. Try again in a moment.",
                        503 => "Groq service is temporarily unavailable. Try again later.",
                        _ => $"Groq request failed ({(int)response.StatusCode})."
                    };
                    return new AnswerSuggestionResult(null, hint, (int)response.StatusCode);
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                using var parsed = JsonDocument.Parse(content.Trim());
                var cleaned = Clean(parsed.RootElement.GetProperty("suggestions"), topic, previous);
                if (cleaned.Count > best.Count) best = cleaned;
                if (best.Count >= WantedCount) break;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // malformed AI output - retry
            }
            catch (Exception ex)
            {
                return new AnswerSuggestionResult(null, ex.Message, 500);
            }
        }

        if (best.Count > 0) RememberSuggestions(topic, best);

        return best.Count > 0
            ? new AnswerSuggestionResult(best, null, 200)
            : new AnswerSuggestionResult(null, "AI failed to generate valid suggestions. Please try again.", 500);
    }

    private static List<string> Clean(JsonElement array, string topic, HashSet<string> previous)
    {
        var result = new List<string>();
        if (array.ValueKind != JsonValueKind.Array) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { topic.Trim() };
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var s = el.GetString()?.Trim().Trim('.', ',', ';', '"') ?? "";
            if (s.Length == 0) continue;
            if (s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxWordsPerSuggestion) continue;
            if (previous.Contains(s) || !seen.Add(s)) continue;
            result.Add(s);
            if (result.Count == WantedCount) break;
        }
        return result;
    }
}
