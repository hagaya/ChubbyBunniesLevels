namespace ChubbyBunniesLevels.Api.Storage;

/// <summary>
/// Abstraction over level file storage. Currently backed only by <see cref="AzureBlobLevelService"/>
/// (Tests/Production virtual folders in a shared Azure Blob container) — mirrors MochiSortLevels.Api's
/// ILevelStorageService, minus the Google Drive fallback (not needed here).
/// </summary>
public interface ILevelStorageService
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<LevelFileInfo>> ListLevelsAsync(LevelFolder folder, CancellationToken ct = default);

    Task<string> DownloadLevelAsync(LevelFolder folder, string fileName, CancellationToken ct = default);

    Task UploadLevelAsync(LevelFolder folder, string fileName, string jsonContent, CancellationToken ct = default);

    Task DeleteLevelAsync(LevelFolder folder, string fileName, CancellationToken ct = default);
}
