using System.Text.Json;
using DotNetEnv;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli;

/// <summary>
/// Application-wide configuration loaded from config.json or environment variables.
/// </summary>
public static class Settings
{
    private static readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaDebrid");
    private static readonly string ConfigFilePath = Path.Combine(AppDataFolder, "config.json");

    public static AppSettings Instance { get; private set; } = new AppSettings();

    // Default path: {User}/Downloads/MediaDebrid
    public static string DefaultBaseRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
        "Downloads", 
        "MediaDebrid"
    );

    // Properties for backward compatibility with the rest of the application
    public static string RealDebridApiToken => Instance.RealDebridApiToken;
    public static string MediaRoot => IsDefault(Instance.MediaRoot) ? DefaultBaseRoot : Instance.MediaRoot;
    public static string GamesRoot => IsDefault(Instance.GamesRoot) ? DefaultBaseRoot : Instance.GamesRoot;
    public static string OthersRoot => IsDefault(Instance.OthersRoot) ? DefaultBaseRoot : Instance.OthersRoot;

    private static bool IsDefault(string path) => string.IsNullOrWhiteSpace(path) || path.Equals("default", StringComparison.OrdinalIgnoreCase);

    public static bool ParallelDownloadEnabled => Instance.ParallelDownloadEnabled;
    public static int ConnectionsPerFile => Instance.ConnectionsPerFile;

    public static void Load()
    {
        // Try to load from JSON first
        if (File.Exists(ConfigFilePath))
        {
            try
            {
                var json = File.ReadAllText(ConfigFilePath);
                Instance = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                return;
            }
            catch (Exception)
            {
                // Fall back to env or defaults if parsing fails
            }
        }

        // Legacy .env and Environment variables loading
        Env.TraversePath().Load();

        Instance.RealDebridApiToken = Environment.GetEnvironmentVariable("REAL_DEBRID_API_TOKEN") ?? "";
        Instance.MediaRoot = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "";
        Instance.GamesRoot = Environment.GetEnvironmentVariable("GAMES_ROOT") ?? "";
        Instance.OthersRoot = Environment.GetEnvironmentVariable("OTHERS_ROOT") ?? "";

        if (bool.TryParse(Environment.GetEnvironmentVariable("PARALLEL_DOWNLOAD_ENABLED"), out bool pd))
            Instance.ParallelDownloadEnabled = pd;

        if (int.TryParse(Environment.GetEnvironmentVariable("CONNECTIONS_PER_FILE"), out int cpf))
            Instance.ConnectionsPerFile = cpf;

        if (bool.TryParse(Environment.GetEnvironmentVariable("SKIP_EXISTING_EPISODES"), out bool skip))
            Instance.SkipExistingEpisodes = skip;

        // Save immediately if we loaded from legacy methods so next time it uses json
        Save();
    }

    public static void Save()
    {
        if (!Directory.Exists(AppDataFolder))
        {
            Directory.CreateDirectory(AppDataFolder);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(Instance, options);
        File.WriteAllText(ConfigFilePath, json);
    }

    public static string GetRootPathForType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return MediaRoot;
        return type.ToLower() switch
        {
            "game" => GamesRoot,
            "other" => OthersRoot,
            _ => MediaRoot
        };
    }

    public static bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(Instance.RealDebridApiToken);
    }
}
