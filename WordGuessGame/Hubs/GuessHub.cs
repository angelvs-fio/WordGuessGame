using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using WordGuessGame.Models;
using WordGuessGame.Models.Enums;
using WordGuessGame.Services;

namespace WordGuessGame.Hubs;

public class GuessHub(GameService service) : Hub
{
    private readonly GameService _svc = service;
    private static string? _currentPainter;

    // Track which connections selected which player names
    private static readonly ConcurrentDictionary<string, string> _connToName = new();

    // Trivia mode state
    private record TriviaAnswer(string Value, DateTimeOffset SubmittedAt);

    private static GameMode _gameMode = GameMode.Drawing;
    private static string? _triviaQuestion;
    private static string? _triviaCorrectAnswer;
    private static readonly ConcurrentDictionary<string, TriviaAnswer> _triviaAnswers = new();

    private static string[] GetActivePlayers()
    {
        return _connToName.Values
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("GameState", new
        {
            hasAnswer = _svc.HasAnswer,
            answerWordCount = _svc.AnswerWordCount,
            isGameOver = _svc.IsGameOver,
            history = _svc.GetHistory(),
            stats = _svc.GetStats(),
            lastWinner = _svc.GetLastWinner(),
            topic = _svc.GetTopic() ?? string.Empty,
            gameMode = _gameMode.ToString().ToLowerInvariant(),
            triviaQuestion = _triviaQuestion ?? string.Empty,
            triviaAnswers = _triviaAnswers.Select(kv => new { user = kv.Key, answer = kv.Value.Value }).ToArray()
        });

        // Inform caller who is the current painter
        await Clients.Caller.SendAsync("PainterSelected", new { painter = _currentPainter ?? string.Empty });

        // Send current active players to the new client
        await Clients.Caller.SendAsync("ActivePlayers", GetActivePlayers());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Remove any mapping for this connection and broadcast updated active players
        if (_connToName.TryRemove(Context.ConnectionId, out _))
        {
            await Clients.All.SendAsync("ActivePlayers", GetActivePlayers());
        }
        await base.OnDisconnectedAsync(exception);
    }

    // Called by clients when a user selects their name from the dropdown
    public async Task SetUserName(string name)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
        {
            _connToName.TryRemove(Context.ConnectionId, out _);
            await Clients.All.SendAsync("ActivePlayers", GetActivePlayers());
            return;
        }

        _connToName[Context.ConnectionId] = n;
        await Clients.All.SendAsync("ActivePlayers", GetActivePlayers());
    }

    public async Task SetAnswer(string user, string answer)
    {
        var ok = _svc.TrySetAnswer(answer);
        if (!ok)
        {
            await Clients.Caller.SendAsync("Error", "Answer already set or game over.");
            return;
        }
        await Clients.All.SendAsync("AnswerSet", new { by = user, wordCount = _svc.AnswerWordCount });
        await BroadcastState();
    }

    public async Task Guess(string user, string guess)
    {
        var result = _svc.SubmitGuess(user, guess);
        switch (result)
        {
            case GuessResultEnum.NoAnswer:
                await Clients.Caller.SendAsync("Error", "No answer set yet.");
                break;
            case GuessResultEnum.GameOver:
                await Clients.Caller.SendAsync("Error", "Game is already over.");
                break;
            case GuessResultEnum.Incorrect:
                await Clients.All.SendAsync("GuessAdded", new GuessMessage { User = user, Guess = guess.Trim(), IsCorrect = false });
                break;
            case GuessResultEnum.Correct:
                await Clients.All.SendAsync("GuessAdded", new GuessMessage { User = user, Guess = guess.Trim(), IsCorrect = true });
                _svc.IncrementPoint(user);
                await Clients.All.SendAsync("GameOver", new { winner = user, stats = _svc.GetStats() });
                break;
        }
        await BroadcastState();
    }

    public async Task ResetKeepResults()
    {
        _svc.ResetKeepResults();
        _triviaQuestion = null;
        _triviaCorrectAnswer = null;
        _triviaAnswers.Clear();
        await Clients.All.SendAsync("ResetKeepResults");
        // Ensure canvas is cleared for everyone
        await Clients.All.SendAsync("CanvasCleared");
        await BroadcastState();
    }

    public async Task ResetWithResults()
    {
        _svc.ResetWithResults();
        _triviaQuestion = null;
        _triviaCorrectAnswer = null;
        _triviaAnswers.Clear();
        await Clients.All.SendAsync("ResetWithResults");
        // Ensure canvas is cleared for everyone
        await Clients.All.SendAsync("CanvasCleared");
        await BroadcastState();
    }

    // Announces the painter to all clients (user is player name or null to clear)
    public Task SelectPainter(string? user)
    {
        _currentPainter = string.IsNullOrWhiteSpace(user) ? null : user;
        return Clients.All.SendAsync("PainterSelected", new { painter = _currentPainter ?? "" });
    }

    // Switch game mode between Drawing and Trivia
    public async Task SwitchGameMode(string mode)
    {
        _gameMode = string.Equals(mode, "trivia", StringComparison.OrdinalIgnoreCase)
            ? GameMode.Trivia
            : GameMode.Drawing;
        _triviaQuestion = null;
        _triviaCorrectAnswer = null;
        _triviaAnswers.Clear();
        await Clients.All.SendAsync("GameModeChanged", new { mode = _gameMode.ToString().ToLowerInvariant() });
    }

    // Painter sets the trivia question and correct answer
    public async Task SetTriviaQuestion(string user, string question, string answer)
    {
        if (_currentPainter == null || !string.Equals(_currentPainter, user, StringComparison.Ordinal))
        {
            await Clients.Caller.SendAsync("Error", "Only the painter can set the trivia question.");
            return;
        }
        var q = (question ?? string.Empty).Trim();
        var a = (answer ?? string.Empty).Trim().Replace(" ", "");
        if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(a))
        {
            await Clients.Caller.SendAsync("Error", "Question and answer are required.");
            return;
        }
        _triviaQuestion = q;
        _triviaCorrectAnswer = a;
        _triviaAnswers.Clear();
        await Clients.All.SendAsync("TriviaQuestionSet", new { question = q, by = user });
    }

    // Player submits a numeric answer for the active trivia question
    public async Task SubmitTriviaAnswer(string user, string answer)
    {
        if (_gameMode != GameMode.Trivia || string.IsNullOrWhiteSpace(_triviaQuestion))
        {
            await Clients.Caller.SendAsync("Error", "No active trivia question.");
            return;
        }
        var a = (answer ?? string.Empty).Trim().Replace(" ", "");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(a))
        {
            await Clients.Caller.SendAsync("Error", "User and answer are required.");
            return;
        }
        _triviaAnswers[user] = new TriviaAnswer(a, DateTimeOffset.UtcNow);
        await Clients.All.SendAsync("TriviaAnswerSubmitted", new { user, answer = a });
        var active = GetActivePlayers();
        var nonPainters = active
            .Where(p => !string.Equals(p, _currentPainter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nonPainters.Length > 0 && nonPainters.All(p => _triviaAnswers.ContainsKey(p)))
            await ResolveTriviaAsync();
    }

    private async Task ResolveTriviaAsync()
    {
        var correctStr = _triviaCorrectAnswer ?? "0";
        double.TryParse(correctStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var correctNum);
        string? winner = null;
        double minDiff = double.MaxValue;
        DateTimeOffset winnerTime = DateTimeOffset.MaxValue;
        foreach (var kvp in _triviaAnswers)
        {
            if (double.TryParse(kvp.Value.Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var playerVal))
            {
                var diff = Math.Abs(playerVal - correctNum);
                if (diff < minDiff || (diff == minDiff && kvp.Value.SubmittedAt < winnerTime))
                {
                    minDiff = diff;
                    winner = kvp.Key;
                    winnerTime = kvp.Value.SubmittedAt;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(winner))
            _svc.IncrementPoint(winner);
        await Clients.All.SendAsync("TriviaComplete", new
        {
            correctAnswer = correctStr,
            winner = winner ?? string.Empty,
            answers = _triviaAnswers.Select(kv => new { user = kv.Key, answer = kv.Value.Value }).ToArray()
        });
    }

    // Painter-only: broadcast stroke segments to all viewers
    public async Task DrawStroke(string user, double x1, double y1, double x2, double y2, string color, double size)
    {
        if (_currentPainter != null && string.Equals(_currentPainter, user, StringComparison.Ordinal))
        {
            await Clients.All.SendAsync("Stroke", new { x1, y1, x2, y2, color, size });
        }
        else
        {
            await Clients.Caller.SendAsync("Error", "Only the current painter can draw.");
        }
    }

    // Painter-only: clear canvas
    public async Task ClearCanvas(string user)
    {
        if (_currentPainter != null && string.Equals(_currentPainter, user, StringComparison.Ordinal))
        {
            await Clients.All.SendAsync("CanvasCleared");
        }
        else
        {
            await Clients.Caller.SendAsync("Error", "Only the current painter can clear the canvas.");
        }
    }

    // Painter-only: broadcast shapes (line, rect, circle)
    public async Task DrawShape(string user, string type, object payload)
    {
        if (_currentPainter != null && string.Equals(_currentPainter, user, StringComparison.Ordinal))
        {
            await Clients.All.SendAsync("Shape", new { type, payload });
        }
        else
        {
            await Clients.Caller.SendAsync("Error", "Only the current painter can draw shapes.");
        }
    }

    // Painter-only: set topic and broadcast
    public async Task SetTopic(string user, string topic)
    {
        if (_currentPainter == null || !string.Equals(_currentPainter, user, StringComparison.Ordinal))
        {
            await Clients.Caller.SendAsync("Error", "Only the current painter can set the topic.");
            return;
        }
        var t = (topic ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(t))
        {
            await Clients.Caller.SendAsync("Error", "Topic is required.");
            return;
        }
        _svc.SetTopic(t);
        await Clients.All.SendAsync("TopicUpdated", new { topic = t });
        await BroadcastState();
    }

    private Task BroadcastState() =>
        Clients.All.SendAsync("GameState", new
        {
            hasAnswer = _svc.HasAnswer,
            answerWordCount = _svc.AnswerWordCount,
            isGameOver = _svc.IsGameOver,
            history = _svc.GetHistory(),
            stats = _svc.GetStats(),
            lastWinner = _svc.GetLastWinner(),
            topic = _svc.GetTopic() ?? string.Empty,
            gameMode = _gameMode.ToString().ToLowerInvariant(),
            triviaQuestion = _triviaQuestion ?? string.Empty,
            triviaAnswers = _triviaAnswers.Select(kv => new { user = kv.Key, answer = kv.Value.Value }).ToArray()
        });
}
