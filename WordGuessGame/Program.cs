using WordGuessGame.Endpoints;
using WordGuessGame.Extensions;
using WordGuessGame.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGameServices(builder.Configuration);

var app = builder.Build();

// Only enforce HTTPS/HSTS in production; allow HTTP on LAN during development
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.UseHsts();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Enable CORS before hubs
app.UseCors();

app.MapHub<GuessHub>("/hub/guess");

app.MapGameEndpoints();
app.MapTriviaEndpoints();

app.Run();
