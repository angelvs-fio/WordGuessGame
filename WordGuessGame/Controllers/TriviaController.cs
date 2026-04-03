using WordGuessGame.Services;

namespace WordGuessGame.Controllers;

public static class TriviaController
{
    public static IEndpointRouteBuilder MapTriviaController(this IEndpointRouteBuilder app)
    {
        app.MapGet("/trivia/generate", async (TriviaService trivia) =>
        {
            var result = await trivia.GenerateTriviaAsync();
            return result.IsSuccess
                ? Results.Json(new { question = result.Question, answer = result.Answer })
                : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        });

        return app;
    }
}
