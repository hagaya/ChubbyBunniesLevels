namespace ChubbyBunniesLevels.Api.Security;

/// <summary>
/// Resolves accepted API keys from configuration.
/// <list type="bullet">
/// <item><c>ApiKeys</c> — JSON array of strings (also supports Azure <c>ApiKeys__0</c>, <c>ApiKeys__1</c>, …).</item>
/// <item><c>ApiKey</c> — single secret, or comma-separated list for rotation (any value matches).</item>
/// </list>
/// If nothing is configured, the middleware allows all requests (local dev only).
/// </summary>
public static class ApiKeyAuth
{
    public static HashSet<string> GetAcceptedApiKeys(IConfiguration config)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        var fromArray = config.GetSection("ApiKeys").Get<string[]>();
        if (fromArray is { Length: > 0 })
        {
            foreach (var s in fromArray)
            {
                if (!string.IsNullOrWhiteSpace(s))
                    set.Add(s.Trim());
            }
        }

        var single = config["ApiKey"];
        if (string.IsNullOrWhiteSpace(single))
            return set;

        if (single.Contains(','))
        {
            foreach (var part in single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(part);
        }
        else
            set.Add(single.Trim());

        return set;
    }
}
