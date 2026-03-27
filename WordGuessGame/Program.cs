using Microsoft.AspNetCore.SignalR;
using WordGuessGame.Models;
using WordGuessGame.Models.Enums;
using WordGuessGame.Services;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var p = policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        if (allowedOrigins.Length > 0)
            p.WithOrigins(allowedOrigins);
        else
            p.SetIsOriginAllowed(_ => true);
    });
});

builder.Services.AddSignalR(options =>
{
    // Give clients more headroom during brief network blips while drawing
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);  // default: 30 s
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);       // default: 15 s (explicit)
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);        // default: 15 s
});

builder.Services.AddHttpClient();

// Persistence store selection: prefer Upstash if configured, else file
var upstashUrl = builder.Configuration["UPSTASH_REDIS_REST_URL"];
var upstashToken = builder.Configuration["UPSTASH_REDIS_REST_TOKEN"];

builder.Services.AddSingleton<IResultsStore>(sp =>
{
    var loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
    var logger = loggerFactory?.CreateLogger("Startup");

    if (!string.IsNullOrWhiteSpace(upstashUrl) && !string.IsNullOrWhiteSpace(upstashToken))
    {
        try
        {
            var store = new UpstashResultsStore(upstashUrl!, upstashToken!);
            logger?.LogInformation("Using Upstash Redis REST results store.");
            return store;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to initialize Upstash store; falling back to file store.");
        }
    }

    var env = sp.GetRequiredService<IHostEnvironment>();
    var resultsPath = Path.Combine(env.ContentRootPath, "results.json");
    logger?.LogInformation("Using file results store at {path}.", resultsPath);
    return new FileResultsStore(resultsPath);
});

// Load players from Redis players set if Upstash is used; else from results
builder.Services.AddSingleton<PlayerRegistry>(sp =>
{
    var store = sp.GetRequiredService<IResultsStore>();

    // Try Upstash-specific players set
    if (store is UpstashResultsStore upstashStore)
    {
        var players = upstashStore.GetPlayers();
        if (players.Length > 0)
        {
            // Sync scores to exactly this set (preserve scores where present)
            try
            {
                var current = upstashStore.GetResults();
                var synced = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in players)
                {
                    synced[p] = current.TryGetValue(p, out var score) ? score : 0;
                }
                upstashStore.WriteResults(synced);
            }
            catch { }
            return new PlayerRegistry(players);
        }
    }

    // Fallback: use keys from existing results
    string[] playersFromResults;
    try
    {
        var dict = store.GetResults();
        playersFromResults = dict.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    catch
    {
        playersFromResults = Array.Empty<string>();
    }

    return new PlayerRegistry(playersFromResults);
});

builder.Services.AddSingleton<GameService>(sp =>
{
    var store = sp.GetRequiredService<IResultsStore>();
    var reg = sp.GetRequiredService<PlayerRegistry>();
    return new GameService(store, reg);
});

var app = builder.Build();

// Only enforce HTTPS/HSTS in production; allow HTTP on LAN during development
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.UseHsts();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Enable CORS before hubs
app.UseCors();

app.MapHub<GuessHub>("/hub/guess");

// Health endpoint
app.MapGet("/health", () => Results.Json(new { status = "ok" }));

// Serve players dynamically: Upstash players set if available; else from results keys
app.MapGet("/players", (IResultsStore store) =>
{
    if (store is UpstashResultsStore upstash)
    {
        var players = upstash.GetPlayers();
        return Results.Json(players);
    }
    try
    {
        var dict = store.GetResults();
        var players = dict.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        return Results.Json(players);
    }
    catch
    {
        return Results.Json(Array.Empty<string>());
    }
});

// Serve results ordered by name ascending and include last winner flag
app.MapGet("/results", (GameService svc) =>
{
    var points = svc.GetResults();
    var lastWinner = svc.GetLastWinner();
    var ordered = points.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => new { name = kv.Key, points = kv.Value, isLastWinner = string.Equals(kv.Key, lastWinner, StringComparison.Ordinal) });
    return Results.Json(ordered);
});

// Topic endpoints
app.MapGet("/topic", (GameService svc) =>
{
    var topic = svc.GetTopic() ?? string.Empty;
    return Results.Json(new { topic });
});

app.MapPost("/topic/set", async (HttpContext ctx, GameService svc, IHubContext<GuessHub> hub) =>
{
    var topic = ctx.Request.Query["topic"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(topic)) return Results.BadRequest(new { error = "topic required" });
    svc.SetTopic(topic);
    await hub.Clients.All.SendAsync("TopicUpdated", new { topic });
    return Results.Json(new { topic });
});

// Manage players: add/remove in Redis players set (Upstash store only)
app.MapPost("/players/manage/add", (HttpContext ctx, IResultsStore store) =>
{
    if (store is not UpstashResultsStore upstash) return Results.BadRequest(new { error = "Upstash store required" });
    var name = ctx.Request.Query["name"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "name required" });
    upstash.AddPlayer(name);
    // Ensure scores include the player
    var current = upstash.GetResults();
    if (!current.ContainsKey(name)) current[name] = 0;
    upstash.WriteResults(current);
    var players = upstash.GetPlayers();
    return Results.Json(new { players, results = current.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => new { name = kv.Key, points = kv.Value }) });
});

app.MapPost("/players/manage/remove", (HttpContext ctx, IResultsStore store) =>
{
    if (store is not UpstashResultsStore upstash) return Results.BadRequest(new { error = "Upstash store required" });
    var name = ctx.Request.Query["name"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "name required" });
    upstash.RemovePlayer(name);
    // Remove from scores
    var current = upstash.GetResults();
    if (current.Remove(name))
    {
        upstash.WriteResults(current);
    }
    var players = upstash.GetPlayers();
    return Results.Json(new { players, results = current.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => new { name = kv.Key, points = kv.Value }) });
});

// AI-powered trivia question generator (Groq — free tier, no credit card needed)
app.MapGet("/trivia/generate", async (IConfiguration config, IHttpClientFactory httpClientFactory) =>
{
    var apiKey = config["GROQ_API_KEY"];
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Json(new { error = "Groq API key not configured." }, statusCode: 503);

    const string prompt =
        "Generate one interesting fact trivia question where the answer is a real number. " +
        "Categories: animal biology (speeds, sizes, weights, lifespans), " +
        "machines & engineering (power, speed, records), astronomy (distances, temperatures, sizes), " +
        "geography (heights, depths, lengths), physics & chemistry (constants, temperatures), world records. " +
        "Rules:\n" +
        "- The \"question\" field must be a full question sentence that asks for the numeric value (include the unit in the question text).\n" +
        "- The \"answer\" field must contain ONLY the numeric value, no text, no units. Can be integer or decimal, positive or negative.\n" +
        "- CRITICAL: Do NOT include the numeric answer value anywhere inside the question text. The question must ask for the number, not state it.\n" +
        "Good example: {\"question\": \"How fast can a cheetah run in km/h?\", \"answer\": \"112\"}\n" +
        "Bad example (forbidden): {\"question\": \"A cheetah runs at 112 km/h. What is this speed?\", \"answer\": \"112\"}\n" +
        "Now generate a NEW question following the good example format.";

    var numericPattern = @"^-?(\d+(\.\d+)?|\.\d+)$";
    var client = httpClientFactory.CreateClient();
    string? lastValidationError = null;

    for (int attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            var requestBody = new
            {
                model = "llama-3.1-8b-instant",
                temperature = 0.9,
                response_format = new { type = "json_object" },
                messages = new[]
                {
                    new { role = "system", content = "You are a trivia question generator. Always respond with valid JSON only." },
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
                return Results.Json(new { error = hint }, statusCode: (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            using var trivia = JsonDocument.Parse(content.Trim());
            var question = trivia.RootElement.GetProperty("question").GetString()?.Trim() ?? "";
            var answerEl = trivia.RootElement.GetProperty("answer");
            var answer = answerEl.ValueKind == JsonValueKind.Number
                ? answerEl.GetRawText()
                : (answerEl.GetString()?.Trim() ?? "");
            answer = answer.Replace(" ", "").Replace("\u00A0", "").Replace(",", ".");

            if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            {
                lastValidationError = "Invalid response from AI.";
                continue;
            }

            // Auto-swap if the model returned the fields in reverse order
            if (!Regex.IsMatch(answer, numericPattern) && Regex.IsMatch(question, numericPattern))
                (question, answer) = (answer, question);

            if (!Regex.IsMatch(answer, numericPattern))
            {
                lastValidationError = "AI returned a non-numeric answer.";
                continue;
            }

            // Retry if the answer number appears inside the question text
            if (answer.Length >= 1 && Regex.IsMatch(question, $@"(?<![0-9.]){Regex.Escape(answer)}(?![0-9.])"))
            {
                lastValidationError = "AI revealed the answer in the question.";
                continue;
            }

            return Results.Json(new { question, answer });
        }
        catch (JsonException)
        {
            lastValidationError = "AI returned malformed JSON.";
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    }

    return Results.Json(new { error = lastValidationError ?? "AI failed to generate a valid question. Please try again." }, statusCode: 500);
});

app.Run();

// Hub delegates to the service
public class GuessHub(GameService service) : Hub
{
    private readonly GameService _svc = service;
    private static string? _currentPainter; // track painter name

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
        if (string.IsNullOrWhiteSpace(n)) return;

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