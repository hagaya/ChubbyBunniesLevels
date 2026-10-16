using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChubbyBunniesLevels.Api.Security;

/// <summary>
/// Loads users (email, password, role) from a JSON file. Edit the file to grant, revoke, or update access.
/// </summary>
public sealed class AllowlistService : IDisposable
{
    private sealed record StoredUser(string Password, string Role);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private Dictionary<string, StoredUser> _users = new(StringComparer.OrdinalIgnoreCase);

    public AllowlistService(IWebHostEnvironment env, IConfiguration config)
    {
        var relative = config["Auth:UsersPath"] ?? config["Auth:AllowlistPath"] ?? "auth-allowlist.json";
        _path = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(env.ContentRootPath, relative);

        Reload();
        TryWatch();
    }

    public bool IsConfigured
    {
        get
        {
            lock (_lock)
                return _users.Count > 0;
        }
    }

    public string? GetRole(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        lock (_lock)
        {
            return _users.TryGetValue(email.Trim(), out var user) ? user.Role : null;
        }
    }

    public bool TryAuthenticate(string? email, string? password, out string? role)
    {
        role = null;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            return false;

        lock (_lock)
        {
            if (!_users.TryGetValue(email.Trim(), out var user))
                return false;

            if (!PasswordMatches(user.Password, password))
                return false;

            role = user.Role;
            return true;
        }
    }

    private void Reload()
    {
        if (!File.Exists(_path))
        {
            lock (_lock)
                _users = new Dictionary<string, StoredUser>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<AllowlistFile>(json, JsonOptions) ?? new AllowlistFile();
            var map = new Dictionary<string, StoredUser>(StringComparer.OrdinalIgnoreCase);

            foreach (var user in file.Users)
            {
                if (string.IsNullOrWhiteSpace(user.Email) || string.IsNullOrEmpty(user.Password))
                    continue;

                var role = NormalizeRole(user.Role);
                if (role is null)
                    continue;

                map[user.Email.Trim()] = new StoredUser(user.Password, role);
            }

            lock (_lock)
                _users = map;
        }
        catch
        {
            // Keep previous users on parse errors.
        }
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return AuthRoles.User;

        return role.Trim() switch
        {
            AuthRoles.User => AuthRoles.User,
            AuthRoles.Admin => AuthRoles.Admin,
            _ => null,
        };
    }

    private static bool PasswordMatches(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        if (expectedBytes.Length != providedBytes.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private void TryWatch()
    {
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;

        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        _watcher.Changed += (_, _) => Reload();
        _watcher.Created += (_, _) => Reload();
        _watcher.Renamed += (_, _) => Reload();
        _watcher.Deleted += (_, _) => Reload();
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose() => _watcher?.Dispose();
}
