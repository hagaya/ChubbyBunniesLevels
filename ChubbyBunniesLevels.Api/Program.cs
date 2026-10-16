using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChubbyBunniesLevels.Api.Security;
using ChubbyBunniesLevels.Api.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ── JSON serialisation ────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// ── Level storage backend (Azure Blob only — no Google Drive fallback here) ───
builder.Services.AddSingleton<ILevelStorageService, AzureBlobLevelService>();

// ── Level content cache ───────────────────────────────────────────────────────
// Caches downloaded level JSON in memory so repeated reads (many users on the same level)
// hit memory instead of Azure Blob Storage. TTL is short so Level Editor pushes appear quickly.
builder.Services.AddMemoryCache();

// ── Email/password sign-in (UI only; API stays on X-Api-Key below) ────────────
builder.Services.AddMochiPasswordAuth(builder.Configuration);

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

// ── Swagger / OpenAPI (Swashbuckle) ───────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Chubby Bunnies Levels API",
        Version = "v1",
        Description =
            "Backend for the Chubby Bunnies Level Editor: sign-in plus level file storage " +
            "under /api/storage/{tests|production}/... (Azure Blob, container/prefixes configured via AzureBlob:*). " +
            "GET /api/storage/{tests|production}/levels lists level files; " +
            "GET .../levels/{fileName} downloads one raw; " +
            "PUT .../levels/{fileName} uploads/overwrites one (Production auto-bumps Level.Ver); " +
            "DELETE .../levels/{fileName} removes one; " +
            "GET .../levels/zip downloads all (or a Level_N.json fromLevel/toLevel range) as one archive; " +
            "GET .../DownloadLevel and POST .../DownloadLevels return level JSON only when server Level.Ver " +
            "is newer than the client-supplied version. " +
            "POST /api/auth/login and /api/auth/verify are stateless email/password sign-in for UI clients " +
            "(no cookie/session; separate from the X-Api-Key mechanism below), backed by auth-allowlist.json.",
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description =
            "Optional when no keys are configured. Use appsettings or Azure: ApiKey (one secret or comma-separated for rotation), and/or ApiKeys array (ApiKeys__0, ApiKeys__1 in Azure).",
    });

    options.AddSecurityRequirement(document =>
    {
        var requirement = new OpenApiSecurityRequirement();
        requirement.Add(new OpenApiSecuritySchemeReference("ApiKey", document, externalResource: null), []);
        return requirement;
    });
});

var app = builder.Build();

// Swagger UI + JSON must run before the API key middleware (see bypass below).
var enableSwagger = app.Configuration.GetValue("Swagger:Enabled", defaultValue: true);

if (enableSwagger)
{
    // Some hosts (including Azure) do not resolve /swagger to index.html; redirect explicitly.
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value;
        if (path is "/swagger" or "/swagger/")
        {
            context.Response.Redirect("/swagger/index.html");
            return;
        }

        await next(context);
    });

    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Chubby Bunnies Levels API v1");
        options.RoutePrefix = "swagger";
    });
}

app.MapHealthChecks("/health");
app.MapAuthEndpoints();

// ── API key middleware ─────────────────────────────────────────────────────────
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
    {
        await next(context);
        return;
    }

    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    var acceptedKeys = ApiKeyAuth.GetAcceptedApiKeys(configuration);

    if (acceptedKeys.Count == 0)
    {
        await next(context);
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-Api-Key", out var provided)
        || string.IsNullOrEmpty(provided)
        || !acceptedKeys.Contains(provided.ToString()))
    {
        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
        return;
    }

    await next(context);
});

// ── Level file storage HTTP API ───────────────────────────────────────────────
// Paths: /api/storage/{folder}/... where {folder} is tests or production.

static bool TryParseFolder(string raw, out LevelFolder folder)
{
    folder = raw.ToLowerInvariant() switch
    {
        "tests"      => LevelFolder.Tests,
        "production" => LevelFolder.Production,
        _            => (LevelFolder)(-1)
    };
    return (int)folder >= 0;
}

// Level JSON is cached in memory for 5 minutes per folder.
// This means a Level Editor push takes up to 5 minutes to propagate to clients,
// but at scale it eliminates repeated blob reads for the same level.
static async Task<(string json, int version)?> GetLevelCachedAsync(
    LevelFolder folder,
    string fileName,
    ILevelStorageService storage,
    IMemoryCache cache)
{
    var folderKey = folder == LevelFolder.Tests ? "tests" : "production";
    var key = $"level:{folderKey}:{fileName}";
    if (cache.TryGetValue(key, out (string json, int version) hit))
        return hit;

    string levelJson;
    try   { levelJson = await storage.DownloadLevelAsync(folder, fileName); }
    catch { return null; }

    if (!TryReadLevelVerFromJson(levelJson, out var ver, out _))
        return null;

    cache.Set(key, (levelJson, ver), TimeSpan.FromMinutes(5));
    return (levelJson, ver);
}

static bool TryApplyProductionLevelVersion(
    string incomingJson,
    string? existingProductionJson,
    out string updatedJson,
    out string error)
{
    updatedJson = incomingJson;
    error = "";

    JsonNode? incomingRoot;
    try
    {
        incomingRoot = JsonNode.Parse(incomingJson);
    }
    catch (JsonException ex)
    {
        error = $"Invalid level JSON body: {ex.Message}";
        return false;
    }

    if (incomingRoot is not JsonObject incomingObj ||
        incomingObj["Level"] is not JsonObject incomingLevelObj)
    {
        error = "Invalid level JSON body: missing object path 'Level'.";
        return false;
    }

    var versionToSet = 1;
    if (!string.IsNullOrWhiteSpace(existingProductionJson))
    {
        try
        {
            var existingRoot = JsonNode.Parse(existingProductionJson);
            if (existingRoot is JsonObject existingObj &&
                existingObj["Level"] is JsonObject existingLevelObj &&
                existingLevelObj["Ver"] is JsonValue existingVerValue &&
                existingVerValue.TryGetValue<int>(out var existingVer))
            {
                versionToSet = existingVer + 1;
            }
        }
        catch
        {
            // Keep fallback version=1 if existing file cannot be parsed cleanly.
        }
    }

    incomingLevelObj["Ver"] = versionToSet;

    updatedJson = incomingObj.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true
    });
    return true;
}

static string NormalizeLevelFileName(string levelName)
{
    if (levelName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        return levelName;
    return levelName + ".json";
}

/// <summary>Parses <c>Level_{n}.json</c> file names (e.g. <c>Level_42.json</c> → 42).</summary>
static bool TryParseLevelNumberFromFileName(string fileName, out int levelNum)
{
    levelNum = 0;
    if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        return false;

    var stem = fileName[..^5];
    if (!stem.StartsWith("Level_", StringComparison.OrdinalIgnoreCase))
        return false;

    return int.TryParse(stem.AsSpan(6), System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out levelNum) && levelNum > 0;
}

static List<LevelFileInfo> FilterLevelFilesByNumberRange(
    IReadOnlyList<LevelFileInfo> files, int fromLevel, int toLevel)
{
    var list = new List<LevelFileInfo>();
    foreach (var info in files)
    {
        if (TryParseLevelNumberFromFileName(info.Name, out var n) && n >= fromLevel && n <= toLevel)
            list.Add(info);
    }

    return list;
}

static bool TryReadLevelVerFromJson(string json, out int ver, out string? error)
{
    ver = 0;
    error = null;

    JsonNode? root;
    try
    {
        root = JsonNode.Parse(json);
    }
    catch (JsonException ex)
    {
        error = $"Invalid level JSON: {ex.Message}";
        return false;
    }

    if (root is not JsonObject rootObj ||
        rootObj["Level"] is not JsonObject levelObj)
    {
        error = "Invalid level JSON: missing object path 'Level'.";
        return false;
    }

    if (levelObj["Ver"] is JsonValue verValue && verValue.TryGetValue<int>(out ver))
        return true;

    error = "Invalid level JSON: Level.Ver is missing or not an integer.";
    return false;
}

// GET /api/storage/{folder}/DownloadLevel
// Returns level JSON only when cloud Level.Ver is newer than the client's levelVersion.
app.MapGet("/api/storage/{folder}/DownloadLevel", async (
    string folder,
    string levelName,
    int levelVersion,
    ILevelStorageService storage) =>
{
    if (!storage.IsAvailable)
        return Results.Problem("Level storage is not configured.", statusCode: 503);

    if (!TryParseFolder(folder, out var levelFolder))
        return Results.BadRequest(new { error = $"Unknown folder '{folder}'. Use 'tests' or 'production'." });

    if (string.IsNullOrWhiteSpace(levelName))
        return Results.BadRequest(new { error = "levelName is required." });

    if (levelVersion < 0)
        return Results.BadRequest(new { error = "levelVersion must be non-negative." });

    var fileName = NormalizeLevelFileName(levelName.Trim());

    string json;
    try
    {
        json = await storage.DownloadLevelAsync(levelFolder, fileName);
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound(new { error = $"Level '{fileName}' not found in {folder}." });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }

    if (!TryReadLevelVerFromJson(json, out var serverVersion, out var parseError))
        return Results.BadRequest(new { error = parseError });

    var hasUpdate = serverVersion > levelVersion;
    return Results.Ok(new DownloadLevelResponse(
        hasUpdate ? json : null,
        serverVersion,
        hasUpdate));
})
.WithName("DownloadLevel")
.WithSummary(
    "Download a level by name if Level.Ver is newer than levelVersion; otherwise omit level JSON. " +
    "Folder is tests or production. levelName may omit the .json suffix.")
.Produces<DownloadLevelResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status502BadGateway)
.Produces(StatusCodes.Status503ServiceUnavailable);

// POST /api/storage/{folder}/DownloadLevels
// Batch variant: accepts up to 50 levels with their client versions and returns all results in one call.
app.MapPost("/api/storage/{folder}/DownloadLevels", async (
    string folder,
    List<BatchDownloadLevelRequest> requests,
    ILevelStorageService storage,
    IMemoryCache cache) =>
{
    if (!storage.IsAvailable)
        return Results.Problem("Level storage is not configured.", statusCode: 503);

    if (!TryParseFolder(folder, out var levelFolder))
        return Results.BadRequest(new { error = $"Unknown folder '{folder}'. Use 'tests' or 'production'." });

    if (requests is null || requests.Count == 0)
        return Results.BadRequest(new { error = "Request body must contain at least one level." });

    if (requests.Count > 50)
        return Results.BadRequest(new { error = "Maximum 50 levels per batch request." });

    var tasks = requests.Select(async req =>
    {
        var fileName = NormalizeLevelFileName(req.LevelName.Trim());
        var cached   = await GetLevelCachedAsync(levelFolder, fileName, storage, cache);

        if (cached is null)
            return new BatchDownloadLevelResponse(fileName, null, 0, false);

        var hasUpdate = cached.Value.version > req.LevelVersion;
        return new BatchDownloadLevelResponse(fileName, hasUpdate ? cached.Value.json : null, cached.Value.version, hasUpdate);
    });

    var results = await Task.WhenAll(tasks);
    return Results.Ok(results);
})
.WithName("BatchDownloadLevels")
.WithSummary(
    "Batch download up to 50 levels in one call (tests or production). " +
    "For each item, levelJson is non-null only when server Level.Ver > client levelVersion. " +
    "Missing or unparseable levels return hasUpdate=false with serverVersion=0.")
.Produces<BatchDownloadLevelResponse[]>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status503ServiceUnavailable);

// GET /api/storage/{folder}/levels
// Returns a JSON array of level file metadata (id, name, modifiedAt, sizeBytes).
app.MapGet("/api/storage/{folder}/levels", async (string folder, ILevelStorageService storage) =>
{
    if (!storage.IsAvailable)
        return Results.Problem("Level storage is not configured.", statusCode: 503);

    if (!TryParseFolder(folder, out var levelFolder))
        return Results.BadRequest(new { error = $"Unknown folder '{folder}'. Use 'tests' or 'production'." });

    try
    {
        var levels = await storage.ListLevelsAsync(levelFolder);
        return Results.Ok(levels);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
})
.WithName("ListStorageLevels")
.WithSummary("List all level JSON files in storage (tests or production virtual folder)")
.Produces<IReadOnlyList<LevelFileInfo>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status503ServiceUnavailable);

// GET /api/storage/{folder}/levels/zip — must be registered before /levels/{fileName} so "zip" is not treated as a file name.
app.MapGet(
    "/api/storage/{folder}/levels/zip",
    async (string folder, int? fromLevel, int? toLevel, ILevelStorageService storage, CancellationToken ct) =>
    {
        if (!storage.IsAvailable)
            return Results.Problem("Level storage is not configured.", statusCode: 503);

        if (!TryParseFolder(folder, out var levelFolder))
            return Results.BadRequest(new { error = $"Unknown folder '{folder}'. Use 'tests' or 'production'." });

        var rangeRequested = fromLevel.HasValue || toLevel.HasValue;
        if (rangeRequested && (!fromLevel.HasValue || !toLevel.HasValue))
            return Results.BadRequest(new { error = "fromLevel and toLevel must both be provided, or both omitted for a full-folder zip." });

        if (fromLevel.HasValue)
        {
            if (fromLevel.Value < 1 || toLevel!.Value < 1)
                return Results.BadRequest(new { error = "fromLevel and toLevel must be at least 1." });
            if (fromLevel.Value > toLevel.Value)
                return Results.BadRequest(new { error = "fromLevel must be less than or equal to toLevel." });
        }

        IReadOnlyList<LevelFileInfo> levelFiles;
        try
        {
            levelFiles = await storage.ListLevelsAsync(levelFolder, ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 502);
        }

        if (fromLevel.HasValue)
            levelFiles = FilterLevelFilesByNumberRange(levelFiles, fromLevel.Value, toLevel!.Value);

        if (levelFiles.Count == 0)
        {
            var emptyMsg = fromLevel.HasValue
                ? $"No Level_*.json files with level number from {fromLevel} to {toLevel} in '{folder}'."
                : "No level JSON files in this folder.";
            return Results.NotFound(new { error = emptyMsg });
        }

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var info in levelFiles)
            {
                ct.ThrowIfCancellationRequested();
                string json;
                try
                {
                    json = await storage.DownloadLevelAsync(levelFolder, info.Name, ct);
                }
                catch (FileNotFoundException ex)
                {
                    return Results.Problem($"While building zip (file '{info.Name}'): {ex.Message}", statusCode: 502);
                }

                var entry = zip.CreateEntry(info.Name, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(json);
                await entryStream.WriteAsync(bytes, ct);
            }
        }

        ms.Position = 0;
        var downloadName = fromLevel.HasValue
            ? $"{folder}-levels-{fromLevel}-{toLevel}.zip"
            : $"{folder}-levels.zip";
        return Results.File(ms, "application/zip", fileDownloadName: downloadName);
    })
.WithName("DownloadStorageLevelsZip")
.WithSummary(
    "Download level *.json files in tests or production as a single .zip. " +
    "Omit fromLevel/toLevel for every file in the folder; provide both for an inclusive Level_N.json range (e.g. fromLevel=10&toLevel=50).")
.Produces(StatusCodes.Status200OK, contentType: "application/zip")
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status502BadGateway)
.Produces(StatusCodes.Status503ServiceUnavailable);

// GET /api/storage/{folder}/levels/{fileName}
// Downloads and returns the raw JSON content of a specific level file.
app.MapGet("/api/storage/{folder}/levels/{fileName}", async (string folder, string fileName, ILevelStorageService storage) =>
{
    if (!storage.IsAvailable)
        return Results.Problem("Level storage is not configured.", statusCode: 503);

    if (!TryParseFolder(folder, out var levelFolder))
        return Results.BadRequest(new { error = $"Unknown folder '{folder}'. Use 'tests' or 'production'." });

    try
    {
        var json = await storage.DownloadLevelAsync(levelFolder, fileName);
        return Results.Content(json, "application/json");
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound(new { error = $"Level '{fileName}' not found in {folder}." });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
})
.WithName("DownloadStorageLevel")
.WithSummary("Download a level JSON file from storage (tests or production)")
.Produces(StatusCodes.Status200OK, contentType: "application/json")
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status503ServiceUnavailable);

// PUT /api/storage/{folder}/levels/{fileName}
// Uploads (or overwrites) a level JSON file. The request body must be the raw JSON content.
app.MapPut("/api/storage/{folder}/levels/{fileName}", async (string folder, string fileName, HttpRequest req, ILevelStorageService storage) =>
{
    if (!storage.IsAvailable)
        return Results.Problem("Level storage is not configured.", statusCode: 503);

    if (!TryParseFolder(folder, out var levelFolder))
        return Results.BadRequest(new { error = $"Unknown folder '{folder}'. Use 'tests' or 'production'." });

    using var reader = new StreamReader(req.Body);
    var jsonContent = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(jsonContent))
        return Results.BadRequest(new { error = "Request body must contain the level JSON." });

    try
    {
        if (levelFolder == LevelFolder.Production)
        {
            string? existingProductionJson = null;
            try
            {
                existingProductionJson = await storage.DownloadLevelAsync(LevelFolder.Production, fileName);
            }
            catch (FileNotFoundException)
            {
                // No existing level with this file name in production -> version starts at 1.
            }

            if (!TryApplyProductionLevelVersion(
                    jsonContent,
                    existingProductionJson,
                    out var versionedJson,
                    out var versionError))
            {
                return Results.BadRequest(new { error = versionError });
            }

            jsonContent = versionedJson;
        }

        await storage.UploadLevelAsync(levelFolder, fileName, jsonContent);
        return Results.Ok(new { message = $"Level '{fileName}' saved to {folder}." });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
})
.WithName("UploadStorageLevel")
.WithSummary("Upload or overwrite a level JSON file in storage (tests or production)")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status503ServiceUnavailable);

// DELETE /api/storage/{folder}/levels/{fileName}
app.MapDelete("/api/storage/{folder}/levels/{fileName}", async (string folder, string fileName, ILevelStorageService storage) =>
{
    if (!storage.IsAvailable)
        return Results.Problem("Level storage is not configured.", statusCode: 503);

    if (!TryParseFolder(folder, out var levelFolder))
        return Results.BadRequest(new { error = $"Unknown folder '{folder}'. Use 'tests' or 'production'." });

    try
    {
        await storage.DeleteLevelAsync(levelFolder, fileName);
        return Results.Ok(new { message = $"Level '{fileName}' removed from {folder}." });
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound(new { error = $"Level '{fileName}' not found in {folder}." });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
})
.WithName("DeleteStorageLevel")
.WithSummary("Delete a level JSON file from storage (tests or production)")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status502BadGateway)
.Produces(StatusCodes.Status503ServiceUnavailable);

app.Run();
