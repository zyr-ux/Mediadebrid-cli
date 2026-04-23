using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

public static class Utils
{
    public static void ApplyMetadataOverrides(MediaMetadata meta, string? seasonOverride, string? episodeOverride)
    {
        if (!string.IsNullOrEmpty(seasonOverride)) meta.Season = seasonOverride;
        if (!string.IsNullOrEmpty(episodeOverride)) meta.Episode = episodeOverride;
        if (meta.Season == null && meta.Type == "show") meta.Season = "1";
    }

    public static HashSet<int> ParseRange(string? input)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var single) && single > 0)
            {
                result.Add(single);
            }
            else
            {
                var rangeParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0], out var start) &&
                    int.TryParse(rangeParts[1], out var end) &&
                    start > 0 && end > 0 && start <= end)
                {
                    for (int i = start; i <= end; i++)
                    {
                        result.Add(i);
                    }
                }
                else
                {
                    // Fail closed for malformed mixed input (e.g., "1,a" or "3-1")
                    return [];
                }
            }
        }
        return result;
    }

    public static string BuildEpisodeKey(int season, int episode) => $"{season}:{episode}";

    public static string[] GetSelectedFiles(List<TorrentFile> files, string? seasonOverride, string? episodeOverride, HashSet<string>? existingEpisodeKeys = null)
    {
        var sRange = ParseRange(seasonOverride);
        var eRange = ParseRange(episodeOverride);
        var resolver = new MetadataResolver();

        var fileIds = files
            .Where(f =>
            {
                if (f.Bytes < 50_000_000 && !eRange.Any()) return false;
                
                var meta = resolver.ParseName(f.Path);
                var fileSeasons = ParseRange(meta.Season);
                var fileEpisodes = ParseRange(meta.Episode);

                if (sRange.Any())
                {
                    if (!fileSeasons.Any(s => sRange.Contains(s))) return false;
                }

                if (eRange.Any())
                {
                    if (!fileEpisodes.Any(e => eRange.Contains(e))) return false;
                }
                
                if (existingEpisodeKeys != null && Settings.Instance.SkipExistingEpisodes)
                {
                    if (fileEpisodes.Any())
                    {
                        foreach (var ep in fileEpisodes)
                        {
                            var season = fileSeasons.FirstOrDefault();
                            if (season == 0 && sRange.Count == 1)
                            {
                                season = sRange.First();
                            }

                            if (season > 0 && existingEpisodeKeys.Contains(BuildEpisodeKey(season, ep))) return false;
                        }
                    }
                }

                return true;
            })
            .Select(f => f.Id.ToString())
            .ToArray();

        return fileIds;
    }

    public static (bool Success, string Message, string? Key) UpdateConfiguration(string key, string value)
    {
        var properties = typeof(AppSettings).GetProperties();
        foreach (var prop in properties)
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var propName = attr != null ? attr.Name : prop.Name;

            if (propName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    object convertedValue = Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(Settings.Instance, convertedValue);
                    Settings.Save();
                    return (true, $"Successfully updated '{propName}' to '{value}'", propName);
                }
                catch
                {
                    return (false, $"Failed to convert '{value}' to type {prop.PropertyType.Name} for key '{propName}'", propName);
                }
            }
        }

        return (false, $"Configuration key '{key}' not found.", null);
    }

    public static string GetSettingsJson()
    {
        return JsonSerializer.Serialize(Settings.Instance, Serialization.AppSettingsJsonContext.Default.AppSettings);
    }

    public static List<(string Key, string Type, string Description)> GetConfigurationMetadata()
    {
        var metadata = new List<(string Key, string Type, string Description)>();
        var properties = typeof(AppSettings).GetProperties();
        foreach (var prop in properties)
        {
            var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var descAttr = prop.GetCustomAttribute<DescriptionAttribute>();
            
            var propName = jsonAttr != null ? jsonAttr.Name : prop.Name;
            var description = descAttr != null ? descAttr.Description : "";
            
            metadata.Add((propName, prop.PropertyType.Name, description));
        }
        return metadata;
    }

    public static string GetRootHelpDescription()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Magnet → Media downloader");
        sb.AppendLine();
        sb.AppendLine("USAGE");
        sb.AppendLine("  mediadebrid-cli <command> [options]");
        sb.AppendLine();
        sb.AppendLine("COMMANDS");
        sb.AppendLine($"  {"resume <path>",-30} - Resume download from .mdebrid file");
        sb.AppendLine($"  {"set <key> <value>",-30} - Set a configuration value");
        sb.AppendLine($"  {"list",-30} - Show current configuration");
        sb.AppendLine();
        sb.AppendLine("OPTIONS");
        sb.AppendLine($"  {"-v, --version",-30} - Show version");
        sb.AppendLine($"  {"-h, --help",-30} - Show help");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("CONFIGURATION (used with `set`)");
        
        var metadata = GetConfigurationMetadata();
        foreach (var (key, type, desc) in metadata)
        {
            if (key.Contains("tmdb", StringComparison.OrdinalIgnoreCase) || key.Contains("rawg", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine($"  {key,-30} - {desc}");
        }

        return sb.ToString();
    }

    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".wmv" };

    public static HashSet<int> GetExistingEpisodes(string directory)
    {
        var existing = new HashSet<int>();
        if (!Directory.Exists(directory)) return existing;

        var resolver = new MetadataResolver();
        try
        {
            var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                if (VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    var meta = resolver.ParseName(Path.GetFileName(file));
                    var eps = ParseRange(meta.Episode);
                    foreach (var ep in eps) existing.Add(ep);
                }
            }
        }
        catch { /* Ignore IO errors during scan */ }

        return existing;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        if (bytes == 0) return "0 B";
        int unitIndex = (int)Math.Floor(Math.Log(bytes, 1024));
        if (unitIndex >= units.Length) unitIndex = units.Length - 1;
        double size = bytes / Math.Pow(1024, unitIndex);
        return $"{size:F2} {units[unitIndex]}";
    }
}
