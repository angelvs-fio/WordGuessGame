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
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _db = _redis.GetDatabase();
        _scoresKey = $"{prefix}:scores";
        _lastWinnerKey = $"{prefix}:lastwinner";
    }

    public RedisResultsStore(ConfigurationOptions options, string prefix = "wordguess")
    {
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
