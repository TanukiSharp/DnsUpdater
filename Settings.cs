using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DnsUpdater;

public sealed class StorageContainer<T>
{
    private readonly string settingsFilename;
    private readonly ILogger<StorageContainer<T>> logger;

    public StorageContainer(params string[] categories)
    {
        logger = new LoggerFactory().CreateLogger<StorageContainer<T>>();

        settingsFilename = SetupStorage(categories);
    }

    private static string SetupStorage(params string[] pathAndFilename)
    {
        if (pathAndFilename.Length == 0)
            throw new ArgumentException($"Argument '{nameof(pathAndFilename)}' cannot be empty.", nameof(pathAndFilename));

        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appDirectory = Path.Join(homeDirectory, ".dnsupdater");

        foreach (string path in pathAndFilename.SkipLast(1))
            appDirectory = Path.Join(appDirectory, path);

        Directory.CreateDirectory(Path.Join(appDirectory, pathAndFilename.Last()));

        string fullFilename = Path.Join(appDirectory, "settings.json");

        return fullFilename;
    }

    public async ValueTask<T?> Get()
    {
        if (File.Exists(settingsFilename) == false)
            return default;

        try
        {
            using Stream fs = File.OpenRead(settingsFilename);
            return await JsonSerializer.DeserializeAsync<T>(fs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get value for settings container of type '{type}' with settings filename '{settingsFilename}'.", typeof(T), settingsFilename);

            return default;
        }
    }

    public async ValueTask Set(T settings)
    {
        try
        {
            using Stream fs = File.OpenWrite(settingsFilename);
            await JsonSerializer.SerializeAsync(fs, settings, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set value for settings container of type '{type}' with settings filename '{settingsFilename}'.", typeof(T), settingsFilename);
        }
    }
}
