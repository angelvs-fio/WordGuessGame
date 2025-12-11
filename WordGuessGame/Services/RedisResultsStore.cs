using StackExchange.Redis;

namespace WordGuessGame.Services;

public sealed class RedisResultsStore : IResultsStore, IDisposable
{
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly string _scoresKey;
    private readonly string _lastWinnerKey;

    public RedisResultsStore(string connectionString, string prefix = "wordguess")
    {
        // Parse to options so we can enforce AbortOnConnectFail=false and handle TLS
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false; // continue retrying
        // If connection string used rediss:// scheme, Parse usually sets Ssl=true.
        // If not set and provider requires TLS, allow enabling via env string (ssl=true) or leave as-is.
        _redis = ConnectionMultiplexer.Connect(options);
        _db = _redis.GetDatabase();
        _scoresKey = $"{prefix}:scores";
        _lastWinnerKey = $"{prefix}:lastwinner";
    }

    public RedisResultsStore(ConfigurationOptions options, string prefix = "wordguess")
    {
        options.AbortOnConnectFail = false; // continue retrying
        _redis = ConnectionMultiplexer.Connect(options);
        _db = _redis.GetDatabase();
        _scoresKey = $"{prefix}:scores";
        _lastWinnerKey = $"{prefix}:lastwinner";
    }

    public IDictionary<string, int> GetResults()
    {
        var entries = _db.HashGetAll(_scoresKey);
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = (string)entry.Name;
            var valStr = (string?)entry.Value;
            if (int.TryParse(valStr, out var val))
                dict[name] = val;
        }
        return dict;
    }

    public void WriteResults(IDictionary<string, int> dict)
    {
        if (dict.Count == 0)
        {
            _db.KeyDelete(_scoresKey);
            return;
        }
        var entries = dict.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray();
        _db.HashSet(_scoresKey, entries);
    }

    public string? GetLastWinner()
    {
        var val = _db.StringGet(_lastWinnerKey);
        return val.HasValue ? (string)val! : null;
    }

    public void SetLastWinner(string winner)
    {
        _db.StringSet(_lastWinnerKey, winner);
    }

    public void Dispose()
    {
        _redis.Dispose();
    }
}
