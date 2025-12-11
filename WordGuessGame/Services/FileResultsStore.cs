using System.Text.Json;

namespace WordGuessGame.Services;

public sealed class FileResultsStore : IResultsStore
{
    private readonly string _resultsPath;
    private readonly string _lastWinnerPath;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public FileResultsStore(string resultsPath)
    {
        _resultsPath = resultsPath;
        var dir = Path.GetDirectoryName(resultsPath) ?? string.Empty;
        _lastWinnerPath = Path.Combine(dir, "lastwinner.txt");
    }

    public IDictionary<string, int> GetResults()
    {
        try
        {
            if (!File.Exists(_resultsPath))
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var fs = File.OpenRead(_resultsPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(fs)
                       ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, int>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void WriteResults(IDictionary<string, int> dict)
    {
        var ordered = dict.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(ordered, _opts);
        File.WriteAllText(_resultsPath, json);
    }

    public string? GetLastWinner()
    {
        try
        {
            if (!File.Exists(_lastWinnerPath)) return null;
            var text = File.ReadAllText(_lastWinnerPath).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public void SetLastWinner(string winner)
    {
        try
        {
            File.WriteAllText(_lastWinnerPath, winner);
        }
        catch
        {
            // ignore
        }
    }
}
