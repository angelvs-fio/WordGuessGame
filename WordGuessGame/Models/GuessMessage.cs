namespace WordGuessGame.Models;

public record GuessMessage
{
    public string User { get; init; } = "";
    public string Guess { get; init; } = "";
    public bool IsCorrect { get; init; }
}