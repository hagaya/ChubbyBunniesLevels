using System.Text.Json.Serialization;

namespace ChubbyBunniesLevels.Api.Security;

public sealed class AllowlistFile
{
    [JsonPropertyName("users")]
    public List<AllowlistUserEntry> Users { get; set; } = [];
}

public sealed class AllowlistUserEntry
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = AuthRoles.User;
}
