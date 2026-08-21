using Microsoft.AspNetCore.SignalR;
using WordGuessGame.Hubs;
using WordGuessGame.Services;

namespace WordGuessGame.Controllers;

public static class GameController
{
    public static IEndpointRouteBuilder MapGameController(this IEndpointRouteBuilder app)
    {
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

        // Winners history: ordered most-recent-first
        app.MapGet("/winners/history", (GameService svc) =>
        {
            var history = svc.GetWinnersHistory();
            var ordered = history.Select(w => new { player = w.Player, date = w.Date });
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

        return app;
    }
}
