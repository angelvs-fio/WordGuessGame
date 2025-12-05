namespace WordGuessGame.Models;

public sealed class PlayerStats
{
    public PlayerStats(string user) { User = user; }
    public string User { get; }
    public int Points { get; set; }
    public int TotalGuesses { get; set; }
}