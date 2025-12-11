using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WordGuessGame.Services;

// Minimal Upstash REST client-backed store
public sealed class UpstashResultsStore : IResultsStore, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly string _scoresKey;
    private readonly string _lastWinnerKey;
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public UpstashResultsStore(string baseUrl, string token, string prefix = "wordguess")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _scoresKey = $"{prefix}:scores";
        _lastWinnerKey = $"{prefix}:lastwinner";
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public IDictionary<string, int> GetResults()
    {
        try
        {
            // HGETALL returns array like [field, value, field, value]
            var url = $"{_baseUrl}/hgetall/{Uri.EscapeDataString(_scoresKey)}";
            var resp = _http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result))
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (result.ValueKind == JsonValueKind.Array)
            {
                var arr = result.EnumerateArray().ToArray();
                for (int i = 0; i + 1 < arr.Length; i += 2)
                {
                    var name = arr[i].GetString() ?? string.Empty;
                    var valStr = arr[i + 1].GetString();
                    if (!string.IsNullOrEmpty(name) && int.TryParse(valStr, out var val))
                        dict[name] = val;
                }
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void WriteResults(IDictionary<string, int> dict)
    {
        try
        {
            if (dict.Count == 0)
            {
                // DEL key
                var urlDel = $"{_baseUrl}/del/{Uri.EscapeDataString(_scoresKey)}";
                _http.PostAsync(urlDel, null).GetAwaiter().GetResult();
                return;
            }
            // HSET key field value ...
            var url = $"{_baseUrl}/hset/{Uri.EscapeDataString(_scoresKey)}";
            var contentFields = new List<string>();
            foreach (var kv in dict)
            {
                contentFields.Add(Uri.EscapeDataString(kv.Key));
                contentFields.Add(Uri.EscapeDataString(kv.Value.ToString()));
            }
            var body = new StringContent(string.Join('/', contentFields), Encoding.UTF8, "text/plain");
            _http.PostAsync(url, body).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }
    }

    public string? GetLastWinner()
    {
        try
        {
            var url = $"{_baseUrl}/get/{Uri.EscapeDataString(_lastWinnerKey)}";
            var resp = _http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
            var val = result.GetString();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        catch { return null; }
    }

    public void SetLastWinner(string winner)
    {
        try
        {
            var url = $"{_baseUrl}/set/{Uri.EscapeDataString(_lastWinnerKey)}/{Uri.EscapeDataString(winner)}";
            _http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
