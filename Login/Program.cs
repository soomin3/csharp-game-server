using Common.Auth;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);

var builder = WebApplication.CreateBuilder(args);
TokenService tokens = TokenService.FromEnvironment();
builder.Services.AddSingleton(tokens);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "MyServer demo login",
    authentication = "Demo only: a name issues a token. No account or password is checked."
}));

app.MapPost("/login", (LoginRequest? request, TokenService tokenService, HttpResponse response) =>
{
    response.Headers.CacheControl = "no-store";
    if (!TokenService.IsValidName(request?.Name))
    {
        return Results.BadRequest(new { error = "Name must contain 1 to 64 UTF-8 bytes, with no surrounding whitespace or control characters." });
    }

    IssuedToken issued = tokenService.Issue(request!.Name!);
    return Results.Ok(new { token = issued.Token, expiresAt = issued.ExpiresAt });
});

Console.WriteLine("[Login] DEMO ONLY: names issue tokens without account authentication.");
app.Run();

public sealed record LoginRequest(string? Name);
