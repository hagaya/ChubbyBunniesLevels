using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace ChubbyBunniesLevels.Api.Storage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="ILevelStorageService"/>.
/// Levels are stored as blobs under {ContainerName}/{Tests|Production}/Level_N.json.
/// Mirrors MochiSortLevels.Api's AzureBlobLevelService.
/// </summary>
public sealed class AzureBlobLevelService : ILevelStorageService
{
    private readonly BlobContainerClient? _container;
    private readonly AzureBlobOptions _options;
    private readonly ILogger<AzureBlobLevelService> _logger;

    public bool IsAvailable => _container is not null;

    public AzureBlobLevelService(IConfiguration configuration, ILogger<AzureBlobLevelService> logger)
    {
        _logger = logger;
        _options = configuration.GetSection(AzureBlobOptions.SectionName).Get<AzureBlobOptions>()
                   ?? new AzureBlobOptions();

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            _logger.LogWarning("AzureBlob:ConnectionString is not set. AzureBlobLevelService is unavailable.");
            return;
        }

        try
        {
            var serviceClient = new BlobServiceClient(_options.ConnectionString);
            _container = serviceClient.GetBlobContainerClient(_options.ContainerName);
            _logger.LogInformation(
                "AzureBlobLevelService initialised. Container: '{Container}', Tests: '{Tests}/', Production: '{Production}/'.",
                _options.ContainerName, _options.TestsPrefix, _options.ProductionPrefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise AzureBlobLevelService.");
        }
    }

    public async Task<IReadOnlyList<LevelFileInfo>> ListLevelsAsync(
        LevelFolder folder, CancellationToken ct = default)
    {
        EnsureAvailable();
        var prefix = FolderPrefix(folder) + "/";
        var result = new List<LevelFileInfo>();

        await foreach (var blob in _container!.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            var name = blob.Name[prefix.Length..];
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains('/')) continue; // skip any unexpected sub-folders

            result.Add(new LevelFileInfo(
                blob.Name,
                name,
                blob.Properties.LastModified,
                blob.Properties.ContentLength));
        }

        return result.OrderBy(f => f.Name).ToList();
    }

    public async Task<string> DownloadLevelAsync(
        LevelFolder folder, string fileName, CancellationToken ct = default)
    {
        EnsureAvailable();
        var blobClient = _container!.GetBlobClient(BlobPath(folder, fileName));

        if (!await blobClient.ExistsAsync(ct))
            throw new FileNotFoundException($"Level '{fileName}' not found in {folder}.");

        var response = await blobClient.DownloadContentAsync(ct);
        return Encoding.UTF8.GetString(response.Value.Content.ToArray());
    }

    public async Task UploadLevelAsync(
        LevelFolder folder, string fileName, string jsonContent, CancellationToken ct = default)
    {
        EnsureAvailable();
        var blobClient = _container!.GetBlobClient(BlobPath(folder, fileName));
        var bytes = Encoding.UTF8.GetBytes(jsonContent);

        using var stream = new MemoryStream(bytes);
        await blobClient.UploadAsync(stream,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } },
            cancellationToken: ct);

        _logger.LogInformation("Uploaded '{FileName}' to {Folder}/ in blob container '{Container}'.",
            fileName, folder, _options.ContainerName);
    }

    public async Task DeleteLevelAsync(
        LevelFolder folder, string fileName, CancellationToken ct = default)
    {
        EnsureAvailable();
        var blobClient = _container!.GetBlobClient(BlobPath(folder, fileName));

        var deleted = await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
        if (!deleted)
            throw new FileNotFoundException($"Level '{fileName}' not found in {folder}.");

        _logger.LogInformation("Deleted '{FileName}' from {Folder}/ in blob container '{Container}'.",
            fileName, folder, _options.ContainerName);
    }

    private string FolderPrefix(LevelFolder folder) => folder switch
    {
        LevelFolder.Tests      => _options.TestsPrefix,
        LevelFolder.Production => _options.ProductionPrefix,
        _                      => throw new ArgumentOutOfRangeException(nameof(folder))
    };

    private string BlobPath(LevelFolder folder, string fileName)
        => $"{FolderPrefix(folder)}/{fileName}";

    private void EnsureAvailable()
    {
        if (_container is null)
            throw new InvalidOperationException("AzureBlobLevelService is not available. Check AzureBlob configuration.");
    }
}
