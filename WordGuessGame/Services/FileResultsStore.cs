using System.Text.Json;
using WordGuessGame.Models;

namespace WordGuessGame.Services;

public sealed class FileResultsStore : IResultsStore
{
    private const int MaxWinnersHistory = 500;

    private readonly string _resultsPath;
    private readonly string _lastWinnerPath;
    private readonly string _topicPath;
    private readonly string _winnersHistoryPath;
    private readonly string _previousWinnersPath;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private readonly object _scoresLock = new();

    public FileResultsStore(string resultsPath)
    {
        _resultsPath = resultsPath;
        var dir = Path.GetDirectoryName(resultsPath) ?? string.Empty;
        _lastWinnerPath = Path.Combine(dir, "lastwinner.txt");
        _topicPath = Path.Combine(dir, "topic.txt");
        _winnersHistoryPath = Path.Combine(dir, "winnershistory.json");
        _previousWinnersPath = Path.Combine(dir, "previouswinners.json");
    }

    public IDictionary<string, int> GetResults()
    {
        try
        {
            if (!File.Exists(_resultsPath))
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var fs = File.OpenRead(_resultsPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(fs)
                       ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, int>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void EnsureScore(string player) => UpdateScores(d => d.TryAdd(player, 0));
    public void IncrementScore(string player) => UpdateScores(d => { d[player] = d.GetValueOrDefault(player) + 1; return true; });
    public void RemoveScore(string player) => UpdateScores(d => d.Remove(player));

    public void ResetScores(IEnumerable<string> players)
    {
        lock (_scoresLock)
            WriteResultsFile(players.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, _ => 0));
    }

    // Read-modify-write under a lock; `change` returns whether anything changed.
    // Never overwrites a file it couldn't read.
    private void UpdateScores(Func<Dictionary<string, int>, bool> change)
    {
        lock (_scoresLock)
        {
            if (TryReadResults(out var dict) && change(dict))
                WriteResultsFile(dict);
        }
    }

    // Distinguishes "no file yet" (empty, OK to write) from "file unreadable" (must not overwrite)
    private bool TryReadResults(out Dictionary<string, int> dict)
    {
        dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_resultsPath)) return true;
            using var fs = File.OpenRead(_resultsPath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(fs);
            if (raw != null) dict = new Dictionary<string, int>(raw, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void WriteResultsFile(IDictionary<string, int> dict)
    {
        var ordered = dict.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(ordered, _opts);
        File.WriteAllText(_resultsPath, json);
    }

    public string? GetLastWinner()
    {
        try
        {
            if (!File.Exists(_lastWinnerPath)) return null;
            var text = File.ReadAllText(_lastWinnerPath).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public void SetLastWinner(string winner)
    {
        try
        {
            File.WriteAllText(_lastWinnerPath, winner);
        }
        catch
        {
            // ignore
        }
    }

    public string? GetTopic()
    {
        try
        {
            if (!File.Exists(_topicPath)) return null;
            var text = File.ReadAllText(_topicPath).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    public void SetTopic(string topic)
    {
        try
        {
            File.WriteAllText(_topicPath, topic);
        }
        catch { /* ignore */ }
    }

    public IReadOnlyList<WinnerRecord> GetWinnersHistory()
    {
        try
        {
            if (!File.Exists(_winnersHistoryPath)) return Array.Empty<WinnerRecord>();
            using var fs = File.OpenRead(_winnersHistoryPath);
            var list = JsonSerializer.Deserialize<List<WinnerRecord>>(fs);
            return list ?? (IReadOnlyList<WinnerRecord>)Array.Empty<WinnerRecord>();
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
            var list = GetWinnersHistory().ToList();
            list.Insert(0, new WinnerRecord(player, when));
            if (list.Count > MaxWinnersHistory)
                list = list.Take(MaxWinnersHistory).ToList();
            var json = JsonSerializer.Serialize(list, _opts);
            File.WriteAllText(_winnersHistoryPath, json);
        }
        catch { /* ignore */ }
    }

    public IReadOnlyList<string> GetPreviousGameWinners()
    {
        try
        {
            if (!File.Exists(_previousWinnersPath)) return Array.Empty<string>();
            using var fs = File.OpenRead(_previousWinnersPath);
            return JsonSerializer.Deserialize<List<string>>(fs) ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public void SetPreviousGameWinners(IReadOnlyList<string> winners)
    {
        try
        {
            File.WriteAllText(_previousWinnersPath, JsonSerializer.Serialize(winners, _opts));
        }
        catch { /* ignore */ }
    }

    public void ClearWinnersHistory()
    {
        try
        {
            File.WriteAllText(_winnersHistoryPath, "[]");
        }
        catch { /* ignore */ }
    }
}
