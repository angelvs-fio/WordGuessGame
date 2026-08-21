using System.Net.Http;
using System.Text;
using System.Text.Json;
using WordGuessGame.Models;

namespace WordGuessGame.Services;

// Minimal Upstash REST client-backed store
public sealed class UpstashResultsStore : IResultsStore, IDisposable
{
    private const int MaxWinnersHistory = 500;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly string _scoresKey;
    private readonly string _lastWinnerKey;
    private readonly string _activePlayersKey;
    private readonly string _playersKey;
    private readonly string _topicKey;
    private readonly string _winnersHistoryKey;

    public UpstashResultsStore(string baseUrl, string token, string prefix = "wordguess")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _scoresKey = $"{prefix}:scores";
        _lastWinnerKey = $"{prefix}:lastwinner";
        _activePlayersKey = $"{prefix}:active";
        _playersKey = $"{prefix}:players";
        _topicKey = $"{prefix}:topic";
        _winnersHistoryKey = $"{prefix}:winnershistory";
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public IDictionary<string, int> GetResults()
    {
        try
        {
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
                // Two possible formats: flat array [field, value, ...] or array of arrays [[field, value], ...]
                if (result.GetArrayLength() > 0 && result[0].ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in result.EnumerateArray())
                    {
                        if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2)
                        {
                            var name = pair[0].GetString();
                            var valStr = pair[1].GetString();
                            if (!string.IsNullOrEmpty(name) && int.TryParse(valStr, out var val))
                                dict[name] = val;
                        }
                    }
                }
                else
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
            // Always reset the hash to ensure removed players don't linger
            var urlDel = $"{_baseUrl}/del/{Uri.EscapeDataString(_scoresKey)}";
            _http.PostAsync(urlDel, null).GetAwaiter().GetResult();

            if (dict.Count == 0)
            {
                return;
            }
            // Build HSET URL with path segments: /hset/key/field/value/field/value...
            var segments = new List<string>
            {
                _baseUrl.TrimEnd('/'),
                "hset",
                Uri.EscapeDataString(_scoresKey)
            };
            foreach (var kv in dict)
            {
                segments.Add(Uri.EscapeDataString(kv.Key));
                segments.Add(Uri.EscapeDataString(kv.Value.ToString()));
            }
            var url = string.Join('/', segments);
            _http.PostAsync(url, null).GetAwaiter().GetResult();
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

    public string? GetTopic()
    {
        try
        {
            var url = $"{_baseUrl}/get/{Uri.EscapeDataString(_topicKey)}";
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

    public void SetTopic(string topic)
    {
        try
        {
            var url = $"{_baseUrl}/set/{Uri.EscapeDataString(_topicKey)}/{Uri.EscapeDataString(topic)}";
            _http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    // Active players management using Redis Set
    public void AddActivePlayer(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var url = $"{_baseUrl}/sadd/{Uri.EscapeDataString(_activePlayersKey)}/{Uri.EscapeDataString(name)}";
            _http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    public void RemoveActivePlayer(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var url = $"{_baseUrl}/srem/{Uri.EscapeDataString(_activePlayersKey)}/{Uri.EscapeDataString(name)}";
            _http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    public string[] GetActivePlayers()
    {
        try
        {
            var url = $"{_baseUrl}/smembers/{Uri.EscapeDataString(_activePlayersKey)}";
            var resp = _http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return Array.Empty<string>();
            if (result.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            return result.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // Persistent players list via Redis Set
    public string[] GetPlayers()
    {
        try
        {
            var url = $"{_baseUrl}/smembers/{Uri.EscapeDataString(_playersKey)}";
            var resp = _http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return Array.Empty<string>();
            if (result.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            return result.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public void SetPlayers(IEnumerable<string> players)
    {
        try
        {
            // Reset the players set then add provided players
            var urlDel = $"{_baseUrl}/del/{Uri.EscapeDataString(_playersKey)}";
            _http.PostAsync(urlDel, null).GetAwaiter().GetResult();
            var list = players?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
            if (list.Length == 0) return;
            var segments = new List<string>
            {
                _baseUrl.TrimEnd('/'),
                "sadd",
                Uri.EscapeDataString(_playersKey)
            };
            foreach (var p in list)
            {
                segments.Add(Uri.EscapeDataString(p));
            }
            var url = string.Join('/', segments);
            _http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    public void AddPlayer(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var url = $"{_baseUrl}/sadd/{Uri.EscapeDataString(_playersKey)}/{Uri.EscapeDataString(name)}";
            _http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    public void RemovePlayer(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var url = $"{_baseUrl}/srem/{Uri.EscapeDataString(_playersKey)}/{Uri.EscapeDataString(name)}";
            _http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    // Winners history: stored as a Redis list, most recent win at the head
    public IReadOnlyList<WinnerRecord> GetWinnersHistory()
    {
        try
        {
            var url = $"{_baseUrl}/lrange/{Uri.EscapeDataString(_winnersHistoryKey)}/0/-1";
            var resp = _http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return Array.Empty<WinnerRecord>();
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return Array.Empty<WinnerRecord>();

            var list = new List<WinnerRecord>();
            foreach (var item in result.EnumerateArray())
            {
                var raw = item.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    using var entry = JsonDocument.Parse(raw);
                    var player = entry.RootElement.GetProperty("player").GetString();
                    var date = entry.RootElement.GetProperty("date").GetDateTimeOffset();
                    if (!string.IsNullOrWhiteSpace(player))
                        list.Add(new WinnerRecord(player, date));
                }
                catch { /* skip malformed entry */ }
            }
            return list;
        }
        catch
        {
            return Array.Empty<WinnerRecord>();
        }
    }

    public void AddWinnerHistory(string player, DateTimeOffset when)
    {
        try
        {
            var entry = JsonSerializer.Serialize(new { player, date = when });
            var urlPush = $"{_baseUrl}/lpush/{Uri.EscapeDataString(_winnersHistoryKey)}/{Uri.EscapeDataString(entry)}";
            _http.PostAsync(urlPush, null).GetAwaiter().GetResult();

            // Keep the history bounded
            var urlTrim = $"{_baseUrl}/ltrim/{Uri.EscapeDataString(_winnersHistoryKey)}/0/{MaxWinnersHistory - 1}";
            _http.PostAsync(urlTrim, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    public void ClearWinnersHistory()
    {
        try
        {
            var url = $"{_baseUrl}/del/{Uri.EscapeDataString(_winnersHistoryKey)}";
            _http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
