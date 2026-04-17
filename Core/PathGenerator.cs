using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MediaDebrid_cli.Core;

public static class PathGenerator
{
    public static string GetDestinationPath(string mediaType, string title, string year, string filename, int? season = null)
    {
        string baseDir = Settings.MediaRoot;
        string safeTitle = Sanitize(title);
        string safeYear = Sanitize(year);
        string safeFilename = Sanitize(filename);

        string folderName = string.IsNullOrWhiteSpace(safeYear) ? safeTitle : $"{safeTitle} ({safeYear})";

        if (mediaType.Equals("movie", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(baseDir, "Movies", folderName, safeFilename);
        }
        else if (mediaType.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            string seasonStr = season.HasValue ? $"Season {season.Value:D2}" : "Season 01";
            return Path.Combine(baseDir, "TV Shows", folderName, seasonStr, safeFilename);
        }
        else
        {
            return Path.Combine(baseDir, "Other", safeFilename);
        }
    }

    private static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
        var regex = new Regex($"[{Regex.Escape(invalidChars)}]");
        return regex.Replace(input, "");
    }
}
