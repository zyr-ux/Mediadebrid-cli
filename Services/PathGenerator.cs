using System.Text.RegularExpressions;

namespace MediaDebrid_cli.Services;

public static class PathGenerator
{
    public static string GetDestinationPath(string? mediaType, string? title, string? year, string filename, string? seasonOverride = null)
    {
        var safeFilename = Sanitize(filename);

        int? actualSeason = null;
        if (!string.IsNullOrEmpty(seasonOverride) && int.TryParse(seasonOverride, out var s))
        {
            actualSeason = s;
        }
        else
        {
            // Try to extract from filename if no specific single season is provided
            actualSeason = Utils.ExtractSeasonNumber(filename);
        }

        var baseDir = GetSeasonDirectory(mediaType, title, year, actualSeason);
        return Path.Combine(baseDir, safeFilename);
    }

    public static string GetSeasonDirectory(string? mediaType, string? title, string? year, string? season)
    {
        int? sNum = null;
        if (int.TryParse(season, out var s)) sNum = s;
        return GetSeasonDirectory(mediaType, title, year, sNum);
    }

    public static string GetSeasonDirectory(string? mediaType, string? title, string? year, int? season = null)
    {
        var safeTitle = Sanitize(title ?? "Unknown");
        var safeYear = Sanitize(year ?? "");
        var safeType = mediaType ?? "other";

        var folderName = string.IsNullOrWhiteSpace(safeYear) ? safeTitle : $"{safeTitle} ({safeYear})";

        if (safeType.Equals("movie", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Settings.MediaRoot, "Movies", folderName);
        }

        if (safeType.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            string seasonStr = season.HasValue ? $"Season {season.Value:D2}" : "Season 01";
            return Path.Combine(Settings.MediaRoot, "TV Shows", folderName, seasonStr);
        }

        if (safeType.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Settings.GamesRoot, "Game Setups", folderName);
        }

        return Path.Combine(Settings.OthersRoot, "Other");
    }

    private static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        
        // Remove invalid characters but allow directory separators for subpaths
        var invalidChars = new string(Path.GetInvalidFileNameChars())
            .Replace("/", "").Replace("\\", ""); 
        invalidChars += new string(Path.GetInvalidPathChars());

        var regex = new Regex($"[{Regex.Escape(invalidChars)}]");
        return regex.Replace(input, "").Trim();
    }
}
