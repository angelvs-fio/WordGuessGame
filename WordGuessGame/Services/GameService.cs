using System.Collections.Concurrent;
using System.Text.Json;
using WordGuessGame.Models;
using WordGuessGame.Models.Enums;

namespace WordGuessGame.Services;

public interface IResultsStore
{
    IDictionary<string, int> GetResults();
    // Adds the player with 0 points only if they have no score yet; never overwrites.
    void EnsureScore(string player);
    void IncrementScore(string player);
    void RemoveScore(string player);
    // Wipes all scores and seeds the given players at 0. Only used by "Reset Game".
    void ResetScores(IEnumerable<string> players);
    string? GetLastWinner();
    void SetLastWinner(string winner);
    string? GetTopic();
    void SetTopic(string topic);
    IReadOnlyList<WinnerRecord> GetWinnersHistory();
    void AddWinnerHistory(string player, DateTimeOffset when);
    void ClearWinnersHistory();
    IReadOnlyList<string> GetPreviousGameWinners();
    void SetPreviousGameWinners(IReadOnlyList<string> winners);
}

public sealed class GameService
{
    private readonly object _lock = new();
    private readonly IResultsStore _store;
    private readonly PlayerRegistry _reg;

    private string? _answerWord;
    private bool _isGameOver;
    private readonly ConcurrentQueue<GuessMessage> _history = new();
    private readonly ConcurrentDictionary<string, PlayerStats> _stats = new();

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public GameService(IResultsStore store, PlayerRegistry reg)
    {
        _store = store;
        _reg = reg;
    }

    public bool IsGameOver => _isGameOver;
    public bool HasAnswer => !string.IsNullOrWhiteSpace(_answerWord);
    public int AnswerWordCount =>
        string.IsNullOrWhiteSpace(_answerWord)
            ? 0
            : _answerWord.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public void ResetGame(bool keepResults)
    {
        lock (_lock)
        {
            _answerWord = null;
            _isGameOver = false;
            while (_history.TryDequeue(out _)) { }
            _stats.Clear();
        }

        if (!keepResults)
        {
            CaptureFinalWinners();
            ResetResultsToZero();
            _store.ClearWinnersHistory();
        }
    }

    public void ResetKeepResults() => ResetGame(keepResults: true);
    public void ResetWithResults() => ResetGame(keepResults: false);

    public bool TrySetAnswer(string answer)
    {
        lock (_lock)
        {
            if (_isGameOver || HasAnswer) return false;
            var s = (answer ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return false;
            _answerWord = s;
            return true;
        }
    }

    public GuessResultEnum SubmitGuess(string user, string guess)
    {
        lock (_lock)
        {
            if (_isGameOver) return GuessResultEnum.GameOver;
            if (!HasAnswer) return GuessResultEnum.NoAnswer;

            var normalizedGuess = (guess ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalizedGuess))
                return GuessResultEnum.Incorrect;

            var isCorrect = string.Equals(_answerWord, normalizedGuess, StringComparison.OrdinalIgnoreCase);

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

        // Make sure every known player has a score entry (added at 0, existing scores untouched)
        foreach (var p in KnownPlayers())
        {
            if (!dict.ContainsKey(p))
            {
                _store.EnsureScore(p);
                dict[p] = 0;
            }
        }
        return dict;
    }

    public void IncrementPoint(string winner)
    {
        _store.IncrementScore(winner);
        _store.SetLastWinner(winner);
        _store.AddWinnerHistory(winner, DateTimeOffset.UtcNow);
    }

    public string? GetLastWinner() => _store.GetLastWinner();
    public IReadOnlyList<WinnerRecord> GetWinnersHistory() =>
        _store.GetWinnersHistory().OrderByDescending(w => w.Date).ToList();

    public string? GetTopic() => _store.GetTopic();
    public void SetTopic(string topic)
    {
        var t = (topic ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(t)) return;
        _store.SetTopic(t);
    }

    // Names of the top scorer(s) of the game that was last reset with results (ties included).
    // Persisted in the results store (Redis for Upstash) so it survives restarts.
    public IReadOnlyList<string> PreviousGameWinners => _store.GetPreviousGameWinners();

    private void CaptureFinalWinners()
    {
        var results = _store.GetResults();
        var max = results.Count == 0 ? 0 : results.Values.Max();
        var winners = max <= 0
            ? Array.Empty<string>()
            : results.Where(kv => kv.Value == max)
                     .Select(kv => kv.Key)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                     .ToArray();
        _store.SetPreviousGameWinners(winners);
    }

    // The only place scores are wiped: "Reset Game" (ResetWithResults)
    private void ResetResultsToZero() => _store.ResetScores(KnownPlayers());

    private string[] KnownPlayers() =>
        (_store is UpstashResultsStore upstash) ? upstash.GetPlayers() : _reg.Players;
}