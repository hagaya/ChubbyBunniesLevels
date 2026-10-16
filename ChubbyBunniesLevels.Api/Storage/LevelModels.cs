namespace ChubbyBunniesLevels.Api.Storage;

public enum LevelFolder { Tests, Production }

/// <summary>Metadata returned when listing levels in a folder.</summary>
public sealed record LevelFileInfo(
    string Id,
    string Name,
    DateTimeOffset? ModifiedAt,
    long? SizeBytes);

/// <summary>
/// Response for <c>DownloadLevel</c>: <see cref="LevelJson"/> is set only when cloud <c>Level.Ver</c>
/// is greater than the client-supplied version (production uploads bump <c>Ver</c> on each save; tests files use their stored <c>Ver</c>).
/// </summary>
public sealed record DownloadLevelResponse(
    string? LevelJson,
    int ServerVersion,
    bool HasUpdate);

/// <summary>One item in a batch download request.</summary>
public sealed record BatchDownloadLevelRequest(string LevelName, int LevelVersion);

/// <summary>One item in a batch download response.</summary>
public sealed record BatchDownloadLevelResponse(
    string LevelName,
    string? LevelJson,
    int ServerVersion,
    bool HasUpdate);
