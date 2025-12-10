using System.Collections.Concurrent;
using System.Text.Json;
using WordGuessGame.Models;
using WordGuessGame.Models.Enums;

namespace WordGuessGame.Services;

public sealed class GameService
{
    private readonly object _lock = new();
    private readonly string _resultsPath;
    private readonly string _lastWinnerPath; // store last winner alongside results
    private readonly PlayerRegistry _reg;

    private string? _secretWord;
    private bool _isGameOver;
    private readonly ConcurrentQueue<GuessMessage> _history = new();
    private readonly ConcurrentDictionary<string, PlayerStats> _stats = new();

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public GameService(string resultsPath, PlayerRegistry reg)
    {
        _resultsPath = resultsPath;
        var dir = Path.GetDirectoryName(resultsPath) ?? string.Empty;
        _lastWinnerPath = Path.Combine(dir, "lastwinner.txt");
        _reg = reg;
        EnsureResultsFile();
    }

    public bool IsGameOver => _isGameOver;
    public bool HasSecret => !string.IsNullOrWhiteSpace(_secretWord);

    public void ResetGame(bool keepResults)
    {
        lock (_lock)
        {
            _secretWord = null;
            _isGameOver = false;
            while (_history.TryDequeue(out _)) { }
            _stats.Clear();
        }

        if (!keepResults)
            ResetResultsToZero();
    }

    public void ResetKeepResults() => ResetGame(keepResults: true);
    public void ResetWithResults() => ResetGame(keepResults: false);

    public bool TrySetSecret(string secret)
    {
        lock (_lock)
        {
            if (_isGameOver || HasSecret) return false;
            var s = (secret ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return false;
            _secretWord = s;
            return true;
        }
    }

    public GuessResultEnum SubmitGuess(string user, string guess)
    {
        lock (_lock)
        {
            if (_isGameOver) return GuessResultEnum.GameOver;
            if (!HasSecret) return GuessResultEnum.NoSecret;

            var normalizedGuess = (guess ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalizedGuess))
                return GuessResultEnum.Incorrect;

            var isCorrect = string.Equals(_secretWord, normalizedGuess, StringComparison.OrdinalIgnoreCase);

            var stats = _stats.GetOrAdd(user, _ => new PlayerStats(user));

            _history.Enqueue(new GuessMessage
            {
                User = user,
                Guess = normalizedGuess,
                IsCorrect = isCorrect
            });

            if (isCorrect)
            {
                stats.Points += 1;
                _isGameOver = true;
                return GuessResultEnum.Correct;
            }
            return GuessResultEnum.Incorrect;
        }
    }

    public GuessMessage[] GetHistory() => _history.ToArray();
    public PlayerStats[] GetStats() => _stats.Values.ToArray();

    // Results persistence
    public IDictionary<string, int> GetResults()
    {
        var dict = ReadResults();
        foreach (var p in _reg.Players)
            if (!dict.ContainsKey(p)) dict[p] = 0;
        return dict;
    }

    public void IncrementPoint(string winner)
    {
        var dict = ReadResults();
        if (!dict.ContainsKey(winner))
            dict[winner] = 0;
        dict[winner] += 1;
        WriteResults(dict);
        SetLastWinner(winner);
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

    private void SetLastWinner(string winner)
    {
        try
        {
            File.WriteAllText(_lastWinnerPath, winner);
        }
        catch
        {
            // ignore persistence failures for last winner
        }
    }

    private void ResetResultsToZero()
    {
        var dict = _reg.Players.ToDictionary(p => p, _ => 0, StringComparer.OrdinalIgnoreCase);
        WriteResults(dict);
    }

    private void EnsureResultsFile()
    {
        if (!File.Exists(_resultsPath))
            ResetResultsToZero();
    }

    private Dictionary<string, int> ReadResults()
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

    private void WriteResults(IDictionary<string, int> dict)
    {
        var ordered = dict.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(ordered, _opts);
        File.WriteAllText(_resultsPath, json);
    }
}