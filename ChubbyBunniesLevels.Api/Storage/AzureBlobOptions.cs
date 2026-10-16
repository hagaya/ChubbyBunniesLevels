namespace ChubbyBunniesLevels.Api.Storage;

public sealed class AzureBlobOptions
{
    public const string SectionName = "AzureBlob";

    /// <summary>
    /// Full Azure Storage connection string.
    /// Set this (via Azure App Service config or appsettings.Development.json) to activate level storage.
    /// Leave empty and ILevelStorageService.IsAvailable stays false — storage endpoints return 503.
    /// </summary>
    public string ConnectionString { get; init; } = "";

    /// <summary>Blob container that holds all level folders.</summary>
    public string ContainerName { get; init; } = "chubbybunnies-levels";

    /// <summary>Virtual folder prefix for the Tests folder.</summary>
    public string TestsPrefix { get; init; } = "Tests";

    /// <summary>Virtual folder prefix for the Production folder.</summary>
    public string ProductionPrefix { get; init; } = "Production";
}
