using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

public static class Utils
{
    public static void ApplyMetadataOverrides(TMDBModels meta, string? typeOverride, string? titleOverride, string? yearOverride, int? seasonOverride, int? episodeOverride)
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
}
