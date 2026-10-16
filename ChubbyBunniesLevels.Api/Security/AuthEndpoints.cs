namespace ChubbyBunniesLevels.Api.Security;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", (LoginRequest request, AllowlistService users) =>
        {
            if (!users.IsConfigured)
                return Results.Problem("User list is not configured.", statusCode: 503);

            if (!users.TryAuthenticate(request.Email, request.Password, out var role))
                return Results.Json(new { error = "Invalid email or password." }, statusCode: StatusCodes.Status401Unauthorized);

            var email = request.Email.Trim();
            return Results.Ok(new AuthLoginResponse(
                IsAuthenticated: true,
                Email: email,
                Role: role));
        })
        .WithName("AuthLogin")
        .WithSummary("Sign in with email and password (stateless — no cookie; native apps store the response locally)")
        .Produces<AuthLoginResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/verify", (LoginRequest request, AllowlistService users) =>
        {
            if (!users.IsConfigured)
                return Results.Problem("User list is not configured.", statusCode: 503);

            if (!users.TryAuthenticate(request.Email, request.Password, out var role))
            {
                return Results.Ok(new AuthVerifyResponse(
                    Allowed: false,
                    Email: null,
                    Role: null));
            }

            return Results.Ok(new AuthVerifyResponse(
                Allowed: true,
                Email: request.Email.Trim(),
                Role: role));
        })
        .WithName("AuthVerify")
        .WithSummary("Check email/password (same as login, alternate response shape; no session)")
        .Produces<AuthVerifyResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status503ServiceUnavailable);
    }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthLoginResponse(
    bool IsAuthenticated,
    string? Email,
    string? Role);

public sealed record AuthVerifyResponse(
    bool Allowed,
    string? Email,
    string? Role);
