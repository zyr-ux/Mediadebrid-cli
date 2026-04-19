using System.Text.RegularExpressions;
using MediaDebrid_cli.Models;
using TMDbLib.Client;

namespace MediaDebrid_cli.Services;

public class MetadataResolver
{
    private readonly TMDbClient? _tmdb;

    public MetadataResolver()
    {
        if (!string.IsNullOrWhiteSpace(Settings.TmdbReadAccessToken) && Settings.TmdbReadAccessToken != "your_tmdb_bearer_token_here")
        {
            _tmdb = new TMDbClient(Settings.TmdbReadAccessToken);
        }
    }

    public async Task<MediaMetadata> ResolveAsync(string name, string? mediaTypeHint = null, CancellationToken cancellationToken = default)
    {
        var parsed = ParseName(name);

        string mediaType = mediaTypeHint ?? parsed.Type;
        if (string.IsNullOrEmpty(mediaTypeHint) && mediaType == "movie")
        {
            // double check for show indicators if not explicitly set
            if (parsed.Season.HasValue || parsed.Episode.HasValue
                || name.Contains("S0", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Season", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = "show";
            }
        }

        var client = _tmdb;
        // Skip TMDB if explicitly "other" or if no token
        if (client == null || mediaType == "other")
        {
            parsed.Type = mediaType;
            return parsed;
        }

        try
        {
            if (mediaType == "movie")
            {
                var results = await client.SearchMovieAsync(parsed.Title, cancellationToken: cancellationToken);
                if (results?.Results != null && results.Results.Count > 0)
                {
                    var best = results.Results[0];
                    parsed.Title = best.Title ?? parsed.Title;
                    parsed.Year = best.ReleaseDate?.Year.ToString() ?? parsed.Year;
                }
            }
            else if (mediaType == "show")
            {
                var results = await client.SearchTvShowAsync(parsed.Title, cancellationToken: cancellationToken);
                if (results?.Results != null && results.Results.Count > 0)
                {
                    var best = results.Results[0];
                    parsed.Title = best.Name ?? parsed.Title;
                    parsed.Year = best.FirstAirDate?.Year.ToString() ?? parsed.Year;
                }
            }
        }
        catch
        {
            // fallback to parsed info
        }

        parsed.Type = mediaType;
        return parsed;
    }

    public MediaMetadata ParseName(string name)
    {
        var result = new MediaMetadata();
        string titlePart = name;

        // 1. Detect Non-Media (Software, ISO, etc.)
        if (Regex.IsMatch(name, @"(?i)\b(windows|office|adobe|autocad|crack|keygen|activator|patch|setup|installer|x64|x86|multilingual|portable)\b") ||
            Regex.IsMatch(name, @"(?i)\.(exe|msi|iso|zip|rar|7z|dmg|pkg)$"))
        {
            result.Type = "other";
        }

        // 2. Extract Season/Episode
        // Patterns: S01E01, S01.E01, Season 1, 1x01
        var seMatch = Regex.Match(name, @"(?i)S(?<season>\d{1,2})[E\.]?(?<episode>\d{1,2})|Season\s*(?<season2>\d{1,2})|(?<season3>\d{1,2})x(?<episode2>\d{1,2})");
        if (seMatch.Success)
        {
            result.Type = "show";
            var sStr = seMatch.Groups["season"].Value ?? seMatch.Groups["season2"].Value ?? seMatch.Groups["season3"].Value;
            var eStr = seMatch.Groups["episode"].Value ?? seMatch.Groups["episode2"].Value;

            if (int.TryParse(sStr, out int s)) result.Season = s;
            if (int.TryParse(eStr, out int e)) result.Episode = e;
            
            titlePart = name[..seMatch.Index];
        }
        else
        {
            // 3. Extract Year
            var yearMatch = Regex.Match(name, @"\b(?<year>19\d{2}|20\d{2})\b");
            if (yearMatch.Success)
            {
                result.Year = yearMatch.Groups["year"].Value;
                if (string.IsNullOrEmpty(result.Type)) result.Type = "movie";
                titlePart = name[..yearMatch.Index];
            }
        }

        // 4. Extract Resolution
        var resMatch = Regex.Match(name, @"(?i)\b(2160p|1080p|720p|480p|4k|uhd|hd)\b");
        if (resMatch.Success)
        {
            result.Resolution = resMatch.Groups[1].Value.ToLowerInvariant();
            if (titlePart.Length > resMatch.Index) titlePart = titlePart[..resMatch.Index];
        }

        // 5. Extract Quality/Codec
        var qualityMatch = Regex.Match(name, @"(?i)\b(bluray|brrip|bdrip|web-dl|webrip|hdtv|h264|x264|h265|x265|hevc|avc)\b");
        if (qualityMatch.Success)
        {
            result.Quality = qualityMatch.Groups[1].Value.ToLowerInvariant();
        }

        // 6. Clean Title
        // Remove periods, underscores, brackets, etc.
        string cleanedTitle = titlePart.Replace(".", " ").Replace("_", " ").Replace("(", "").Replace(")", "").Replace("[", "").Replace("]", "").Trim();
        cleanedTitle = Regex.Replace(cleanedTitle, @"\s+", " ").Trim('-', ' ');

        // Fallback if title is empty or too short
        if (string.IsNullOrWhiteSpace(cleanedTitle) || cleanedTitle.Length < 2)
        {
            cleanedTitle = name.Split(new[] { '.', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        result.Title = cleanedTitle;
        if (string.IsNullOrEmpty(result.Type)) result.Type = "movie"; // Default to movie if not software or show

        return result;
    }
}
