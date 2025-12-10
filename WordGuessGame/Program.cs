using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using WordGuessGame.Models;
using WordGuessGame.Models.Enums;
using WordGuessGame.Services;

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

// Load players from results.json (use dictionary keys as player names)
builder.Services.AddSingleton<PlayerRegistry>(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var resultsPath = Path.Combine(env.ContentRootPath, "results.json");
    string[] playersFromResults = Array.Empty<string>();

    try
    {
        if (File.Exists(resultsPath))
        {
            var json = File.ReadAllText(resultsPath);
            // results.json is a name->points dictionary
            var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();
            playersFromResults = dict.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
    catch
    {
        // If parsing fails, fall back to empty list
        playersFromResults = Array.Empty<string>();
    }

    return new PlayerRegistry(playersFromResults);
});

builder.Services.AddSingleton<GameService>(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var reg = sp.GetRequiredService<PlayerRegistry>();
    var resultsPath = Path.Combine(env.ContentRootPath, "results.json");
    return new GameService(resultsPath, reg);
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

// Serve players as-is (already ordered when loaded from results.json)
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

app.Run();

// Hub delegates to the service
public class GuessHub(GameService service) : Hub
{
    private readonly GameService _svc = service;
    private static string? _currentPainter; // track painter name

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
        await base.OnConnectedAsync();
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