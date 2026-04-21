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
    public static void ApplyMetadataOverrides(MediaMetadata meta, string? typeOverride, string? titleOverride, string? yearOverride, int? seasonOverride, int? episodeOverride)
    {
        if (!string.IsNullOrWhiteSpace(titleOverride)) meta.Title = titleOverride.Trim();
        if (!string.IsNullOrWhiteSpace(typeOverride)) meta.Type = typeOverride.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(yearOverride)) meta.Year = yearOverride.Trim();
        if (seasonOverride.HasValue) meta.Season = seasonOverride;
        if (episodeOverride.HasValue) meta.Episode = episodeOverride;
        if (meta.Season == null && meta.Type == "show") meta.Season = 1;
    }

    public static string[] GetSelectedFiles(List<TorrentFile> files, int? episodeOverride, HashSet<int>? existingEpisodes = null)
    {
        var fileIds = files
            .Where(f =>
            {
                if (f.Bytes < 50_000_000 && !episodeOverride.HasValue) return false;
                if (episodeOverride.HasValue)
                {
                    return IsEpisodeMatch(f.Path, episodeOverride.Value);
                }
                
                if (existingEpisodes != null && Settings.Instance.SkipExistingEpisodes)
                {
                    var ep = ExtractEpisodeNumber(f.Path);
                    if (ep.HasValue && existingEpisodes.Contains(ep.Value)) return false;
                }

                return true;
            })
            .Select(f => f.Id.ToString())
            .ToArray();

        if (!fileIds.Any())
        {
            fileIds = files
                .Where(f => f.Bytes > 50_000_000)
                .Select(f => f.Id.ToString())
                .ToArray();
        }

        if (!fileIds.Any() && files.Any())
        {
            fileIds = new[] { files.First().Id.ToString() };
        }

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
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(Settings.Instance, options);
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
        sb.AppendLine($"  {"add <magnet>",-30} - Add a magnet and start downloading");
        sb.AppendLine($"  {"resume <path>",-30} - Resume download from .mdebrid file");
        sb.AppendLine($"  {"set <key> <value>",-30} - Set a configuration value");
        sb.AppendLine($"  {"list",-30} - Show current configuration");
        sb.AppendLine();
        sb.AppendLine("OPTIONS");
        sb.AppendLine($"  {"-v, --version",-30} - Show version");
        sb.AppendLine($"  {"-h, --help",-30} - Show help");
        sb.AppendLine();
        sb.AppendLine("METADATA OVERRIDES (used with `add`)");
        sb.AppendLine($"  {"--type <movie|show|game|other>",-30} - Force media type");
        sb.AppendLine($"  {"--title <name>",-30} - Override title");
        sb.AppendLine($"  {"--year <yyyy>",-30} - Override release year");
        sb.AppendLine($"  {"--season <num>",-30} - Season number");
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

    public static bool IsEpisodeMatch(string path, int episodeNumber)
    {
        // Try to match episode number in path (e.g., E05, Episode 5, x05)
        var epPattern = $@"(?i)(?:E|Episode\s*|x)(0*{episodeNumber})\b";
        if (Regex.IsMatch(path, epPattern)) return true;

        // Fallback for cases like "Show Title 05.mkv"
        var fallbackPattern = $@"(?i)(?:\s|^)(0*{episodeNumber})\b";
        return Regex.IsMatch(path, fallbackPattern);
    }

    public static int? ExtractEpisodeNumber(string input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        
        // Try to match episode number (e.g., E05, Episode 5, x05)
        var epPattern = @"(?i)(?:E|Episode\s*|x)0*(\d+)\b";
        var match = Regex.Match(input, epPattern);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int ep)) return ep;

        // Fallback for cases like "Show Title 05.mkv"
        var fallbackPattern = @"(?i)(?:\s|^)0*(\d+)\b";
        match = Regex.Match(input, fallbackPattern);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int fallbackEp)) return fallbackEp;

        return null;
    }

    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".wmv" };

    public static HashSet<int> GetExistingEpisodes(string directory)
    {
        var existing = new HashSet<int>();
        if (!Directory.Exists(directory)) return existing;

        try
        {
            var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                if (VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    var ep = ExtractEpisodeNumber(Path.GetFileName(file));
                    if (ep.HasValue) existing.Add(ep.Value);
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
