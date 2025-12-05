namespace WordGuessGame.Models;

public sealed class PlayerRegistry
{
    public PlayerRegistry(string[] players) => Players = players;
    public string[] Players { get; }
}