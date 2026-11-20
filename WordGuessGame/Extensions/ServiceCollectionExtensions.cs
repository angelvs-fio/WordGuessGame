using WordGuessGame.Models;
using WordGuessGame.Services;

namespace WordGuessGame.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGameServices(this IServiceCollection services, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

        services.AddCors(options =>
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

        services.AddSignalR(options =>
        {
            // Give clients more headroom during brief network blips while drawing
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);  // default: 30 s
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);       // default: 15 s (explicit)
            options.HandshakeTimeout = TimeSpan.FromSeconds(30);        // default: 15 s
        });

        services.AddHttpClient();

        // Persistence store selection: prefer Upstash if configured, else file
        var upstashUrl = configuration["UPSTASH_REDIS_REST_URL"];
        var upstashToken = configuration["UPSTASH_REDIS_REST_TOKEN"];

        services.AddSingleton<IResultsStore>(sp =>
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
        services.AddSingleton<PlayerRegistry>(sp =>
        {
            var store = sp.GetRequiredService<IResultsStore>();

            if (store is UpstashResultsStore upstashStore)
            {
                var players = upstashStore.GetPlayers();
                if (players.Length > 0)
                {
                    // Drop scores of players no longer in the set (missing players are added by GameService.GetResults)
                    var playerSet = new HashSet<string>(players, StringComparer.OrdinalIgnoreCase);
                    foreach (var stale in upstashStore.GetResults().Keys.Where(k => !playerSet.Contains(k)))
                        upstashStore.RemoveScore(stale);
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

        services.AddSingleton<GameService>(sp =>
        {
            var store = sp.GetRequiredService<IResultsStore>();
            var reg = sp.GetRequiredService<PlayerRegistry>();
            return new GameService(store, reg);
        });

        services.AddSingleton<TriviaService>();
        services.AddSingleton<AnswerSuggestionService>();
        services.AddSingleton<TopicSuggestionService>();

        return services;
    }
}
