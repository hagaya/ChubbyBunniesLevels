namespace ChubbyBunniesLevels.Api.Security;

public static class AuthServiceExtensions
{
    public static IServiceCollection AddMochiPasswordAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<AllowlistService>();
        return services;
    }
}
