using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WordGuessGame.Models;
using WordGuessGame.Models.Enums;
using WordGuessGame.Services;
using System.Collections.Concurrent;

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

builder.Services.AddSignalR();

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

// Serve players as-is (already ordered when loaded from store)
app.MapGet("/players", (PlayerRegistry reg) => Results.Json(reg.Players));

// Serve results ordered by name ascending and include last winner flag
app.MapGet("/results", (GameService svc) =>
{
    var points = svc.GetResults();
    var lastWinner = svc.GetLastWinner();
    var ordered = points.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => new { name = kv.Key, points = kv.Value, isLastWinner = string.Equals(kv.Key, lastWinner, StringComparison.Ordinal) });
    return Results.Json(ordered);
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

app.Run();

// Hub delegates to the service
public class GuessHub(GameService service) : Hub
{
    private readonly GameService _svc = service;
    private static string? _currentPainter; // track painter name

    // Track which connections selected which player names
    private static readonly ConcurrentDictionary<string, string> _connToName = new();

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
            hasSecret = _svc.HasSecret,
            isGameOver = _svc.IsGameOver,
            history = _svc.GetHistory(),
            stats = _svc.GetStats(),
            lastWinner = _svc.GetLastWinner()
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

    public async Task SetSecret(string user, string secret)
    {
        var ok = _svc.TrySetSecret(secret);
        if (!ok)
        {
            await Clients.Caller.SendAsync("Error", "Secret already set or game over.");
            return;
        }
        await Clients.All.SendAsync("SecretSet", new { by = user });
        await BroadcastState();
    }

    public async Task Guess(string user, string guess)
    {
        var result = _svc.SubmitGuess(user, guess);
        switch (result)
        {
            case GuessResultEnum.NoSecret:
                await Clients.Caller.SendAsync("Error", "No secret set yet.");
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
        await Clients.All.SendAsync("ResetKeepResults");
        // Ensure canvas is cleared for everyone
        await Clients.All.SendAsync("CanvasCleared");
        await BroadcastState();
    }

    public async Task ResetWithResults()
    {
        _svc.ResetWithResults();
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

    private Task BroadcastState() =>
        Clients.All.SendAsync("GameState", new
        {
            hasSecret = _svc.HasSecret,
            isGameOver = _svc.IsGameOver,
            history = _svc.GetHistory(),
            stats = _svc.GetStats(),
            lastWinner = _svc.GetLastWinner()
        });
}