using System.Text.Json;
using System.Text.RegularExpressions;

namespace WordGuessGame.Services;

public record TriviaGenerateResult(string? Question, string? Answer, string? Error, int StatusCode)
{
    public bool IsSuccess => Error is null;
}

public class TriviaService(IConfiguration config, IHttpClientFactory httpClientFactory)
{
    private static readonly string[] Categories =
    [
        // Animals
        "land animal top speeds (km/h)",
        "bird flight speeds (km/h)",
        "marine animal sizes or depths (metres or kg)",
        "animal lifespans (years)",
        "animal weight records — heaviest or lightest species (kg or grams)",
        "bird egg sizes or incubation periods (cm or days)",
        "deep-sea creature depth records (metres)",
        "animal migration distances (km)",
        "animal diving depths (metres)",
        // Geography & Earth
        "deep-sea trench or ocean depth records (metres)",
        "river lengths around the world (km)",
        "mountain or volcano heights (metres)",
        "desert or lake surface areas (km²)",
        "glacier or ice sheet volumes (km³)",
        "island areas (km²)",
        "waterfall heights (metres)",
        "cave lengths or depths (metres)",
        "country areas (km²)",
        "coastline lengths of countries (km)",
        "annual rainfall extremes by location (mm per year)",
        "temperature extremes on Earth's surface (°C)",
        // Astronomy & Space
        "planet distances from the Sun (million km)",
        "planet surface temperatures (°C)",
        "planet orbital periods (Earth days or years)",
        "star sizes or luminosities relative to the Sun",
        "moon sizes or orbital distances (km)",
        "asteroid or comet sizes (km)",
        "black hole masses (solar masses)",
        "galaxy distances from the Milky Way (million light-years)",
        // Human body & medicine
        "human body measurements (organ weight in grams, bone count, blood volume in litres)",
        "human physiological limits (lung capacity in litres, reaction time in ms)",
        "medical or pharmaceutical facts (half-life in hours, lethal dose in mg/kg)",
        // Physics & Chemistry
        "speed of sound in different materials (m/s)",
        "melting or boiling points of chemical elements (°C)",
        "pressure extremes (atmospheres or bar)",
        "electrical or energy constants (volts, joules, watts)",
        // Engineering & Technology
        "land vehicle speed records (km/h)",
        "aircraft or spacecraft speed records (km/h)",
        "skyscraper or bridge heights and lengths (metres)",
        "historical engineering facts (tunnel length, dam height in metres)",
        "ship or submarine sizes (metres or displacement in tonnes)",
        "power output of turbines or reactors (MW)",
        "telescope aperture or mirror sizes (metres)",
        // Food, agriculture & economics
        "food and agriculture facts (calories per 100g, tonnes produced per year)",
        "alcohol content or fermentation time (% ABV or days)",
        "crop yield records per hectare (tonnes/ha)",
        "world fish catch by species (million tonnes per year)",
        "country GDP or trade volume (billion USD)",
        "historical prices of commodities (USD per tonne or ounce)",
        // Sports & records
        "sports world records (distance in metres, weight in kg, time in seconds)",
        "Olympic records in track and field (seconds or metres)",
        "Olympic records in weightlifting or throwing events (kg or metres)",
        "marathon or ultramarathon records (minutes and seconds)",
        "free-diving or breath-holding world records (metres or seconds)",
        // History & demographics
        "historical population of ancient cities (thousands of people)",
        "historical battle casualties or army sizes (thousands of soldiers)",
        "age of historical structures (years)",
        "historical ship or vehicle dimensions (metres or tonnes)",
        // Geology & environment
        "geological facts (age of rock formation in million years, earthquake magnitude)",
        "tsunami or flood wave heights (metres)",
        "ocean salinity or pH levels"
    ];

    public async Task<TriviaGenerateResult> GenerateTriviaAsync()
    {
        var apiKey = config["GROQ_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return new TriviaGenerateResult(null, null, "Groq API key not configured.", 503);

        var category = Categories[Random.Shared.Next(Categories.Length)];

        var prompt =
            $"Generate one interesting and trivia question specifically about: {category}. " +
            "Generate a trivia question based on well-documented, widely accepted facts. Avoid obscure or uncertain data." +
            "The answer of that question must be a real number. " +
            "Rules:\n" +
            "- The \"question\" field must be a clear question sentence asking for a numeric value. Do NOT include the unit inside the question sentence itself.\n" +
            "- The \"unit\" field must contain only the unit of measurement (e.g. \"km/h\", \"°C\", \"metres\", \"kg\", \"years\"). Keep it short.\n" +
            "- The \"answer\" field must contain ONLY the numeric value, no text, no units. Can be integer or decimal, positive or negative.\n" +
            "- CRITICAL: Do NOT include the numeric answer value anywhere inside the question text. The question must ask for the number, not state it.\n" +
            "- After generating the answer, internally verify if the numeric value is accurate. If unsure, generate a different question.\n" +
            "- Do NOT generate obscure or uncertain facts.\n" +
            "Good example: {\"question\": \"How fast can a cheetah run?\", \"unit\": \"km/h\", \"answer\": \"110\"}\n" +
            "Bad example (forbidden): {\"question\": \"A cheetah runs at 110 km/h. What is this speed?\", \"unit\": \"km/h\", \"answer\": \"110\"}\n" +
            "Now generate a NEW question following the good example format.";

        var numericPattern = @"^-?(\d+(\.\d+)?|\.\d+)$";
        var client = httpClientFactory.CreateClient();
        string? lastValidationError = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var requestBody = new
                {
                    model = "openai/gpt-oss-120b",
                    temperature = 0.0,
                    response_format = new { type = "json_object" },
                    messages = new[]
                    {
                        new { role = "system", content = "You are a trivia question generator. Always respond with valid JSON only." },
                        new { role = "user", content = prompt }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                request.Content = JsonContent.Create(requestBody);

                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var hint = (int)response.StatusCode switch
                    {
                        401 => "Invalid API key. Check your Groq:ApiKey config.",
                        429 => "Rate limit exceeded. Try again in a moment.",
                        503 => "Groq service is temporarily unavailable. Try again later.",
                        _ => $"Groq request failed ({(int)response.StatusCode})."
                    };
                    return new TriviaGenerateResult(null, null, hint, (int)response.StatusCode);
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                using var trivia = JsonDocument.Parse(content.Trim());
                var question = trivia.RootElement.GetProperty("question").GetString()?.Trim() ?? "";
                var answerEl = trivia.RootElement.GetProperty("answer");
                var answer = answerEl.ValueKind == JsonValueKind.Number
                    ? answerEl.GetRawText()
                    : (answerEl.GetString()?.Trim() ?? "");
                answer = answer.Replace(" ", "").Replace("\u00A0", "").Replace(",", ".");

                // Append the unit in brackets to the question if the AI returned one
                if (trivia.RootElement.TryGetProperty("unit", out var unitEl))
                {
                    var unit = unitEl.GetString()?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(unit))
                        question = $"{question} [{unit}]";
                }

                if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
                {
                    lastValidationError = "Invalid response from AI.";
                    continue;
                }

                // Auto-swap if the model returned the fields in reverse order
                if (!Regex.IsMatch(answer, numericPattern) && Regex.IsMatch(question, numericPattern))
                    (question, answer) = (answer, question);

                if (!Regex.IsMatch(answer, numericPattern))
                {
                    lastValidationError = "AI returned a non-numeric answer.";
                    continue;
                }

                // Retry if the answer number appears inside the question text
                if (answer.Length >= 1 && Regex.IsMatch(question, $@"(?<![0-9.]){Regex.Escape(answer)}(?![0-9.])"))
                {
                    lastValidationError = "AI revealed the answer in the question.";
                    continue;
                }

                return new TriviaGenerateResult(question, answer, null, 200);
            }
            catch (JsonException)
            {
                lastValidationError = "AI returned malformed JSON.";
            }
            catch (Exception ex)
            {
                return new TriviaGenerateResult(null, null, ex.Message, 500);
            }
        }

        return new TriviaGenerateResult(null, null, lastValidationError ?? "AI failed to generate a valid question. Please try again.", 500);
    }
}
