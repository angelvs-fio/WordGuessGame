using System.Collections.Concurrent;
using System.Text.Json;
using WordGuessGame.Models;
using WordGuessGame.Models.Enums;

namespace WordGuessGame.Services;

public interface IResultsStore
{
    IDictionary<string, int> GetResults();
    void WriteResults(IDictionary<string, int> dict);
    string? GetLastWinner();
    void SetLastWinner(string winner);
    string? GetTopic();
    void SetTopic(string topic);
}

public sealed class GameService
{
    private readonly object _lock = new();
    private readonly IResultsStore _store;
    private readonly PlayerRegistry _reg;

    private string? _secretWord;
    private bool _isGameOver;
    private readonly ConcurrentQueue<GuessMessage> _history = new();
    private readonly ConcurrentDictionary<string, PlayerStats> _stats = new();

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public GameService(IResultsStore store, PlayerRegistry reg)
    {
        _store = store;
        _reg = reg;
        EnsurePlayersPersisted();
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
        var dict = _store.GetResults();
        bool changed = false;

        // If using Upstash, align missing players to the current Upstash players set
        if (_store is UpstashResultsStore upstash)
        {
            var players = upstash.GetPlayers();
            foreach (var p in players)
            {
                if (!dict.ContainsKey(p)) { dict[p] = 0; changed = true; }
            }
        }
        else
        {
            foreach (var p in _reg.Players)
            {
                if (!dict.ContainsKey(p)) { dict[p] = 0; changed = true; }
            }
        }

        if (changed)
        {
            // Persist any missing players with zero scores
            _store.WriteResults(dict);
        }
        return dict;
    }

    public void IncrementPoint(string winner)
    {
        var dict = _store.GetResults();
        if (!dict.ContainsKey(winner))
            dict[winner] = 0;
        dict[winner] += 1;
        _store.WriteResults(dict);
        _store.SetLastWinner(winner);
    }

    public string? GetLastWinner() => _store.GetLastWinner();

    public string? GetTopic() => _store.GetTopic();
    public void SetTopic(string topic)
    {
        var t = (topic ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(t)) return;
        _store.SetTopic(t);
    }

    private void ResetResultsToZero()
    {
        var players = (_store is UpstashResultsStore upstash)
            ? upstash.GetPlayers()
            : _reg.Players;
        var dict = players.ToDictionary(p => p, _ => 0, StringComparer.OrdinalIgnoreCase);
        _store.WriteResults(dict);
    }

    private void EnsurePlayersPersisted()
    {
        var dict = _store.GetResults();
        if (dict.Count == 0)
        {
            var players = (_store is UpstashResultsStore upstash)
                ? upstash.GetPlayers()
                : _reg.Players;
            if (players.Length > 0)
            {
                // Seed all players with zero points
                var seed = players.ToDictionary(p => p, _ => 0, StringComparer.OrdinalIgnoreCase);
                _store.WriteResults(seed);
                return;
            }
        }
        bool changed = false;
        var baseline = (_store is UpstashResultsStore upstash2) ? upstash2.GetPlayers() : _reg.Players;
        foreach (var p in baseline)
        {
            if (!dict.ContainsKey(p)) { dict[p] = 0; changed = true; }
        }
        if (changed)
        {
            _store.WriteResults(dict);
        }
    }
}