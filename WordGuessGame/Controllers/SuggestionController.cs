using WordGuessGame.Services;

namespace WordGuessGame.Controllers;

public static class SuggestionController
{
    public static IEndpointRouteBuilder MapSuggestionController(this IEndpointRouteBuilder app)
    {
        app.MapGet("/answers/suggest", async (GameService game, AnswerSuggestionService suggestions) =>
        {
            var result = await suggestions.SuggestAsync(game.GetTopic() ?? string.Empty);
            return result.IsSuccess
                ? Results.Json(new { suggestions = result.Suggestions })
                : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        });

        return app;
    }
}
