namespace ChubbyBunniesLevels.Api.Security;

public static class AuthRoles
{
    public const string User = "User";
    public const string Admin = "Admin";

    /// <summary>API key clients (Unity, automation).</summary>
    public const string Service = "Service";
}
