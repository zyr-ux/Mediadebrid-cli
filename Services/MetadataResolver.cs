using System.Text.RegularExpressions;
using MediaDebrid_cli.Models;
using TMDbLib.Client;

namespace MediaDebrid_cli.Services;

public class MetadataResolver
{
    private readonly TMDbClient? _tmdb;

    public MetadataResolver()
    {
        if (!string.IsNullOrWhiteSpace(Settings.TmdbReadAccessToken))
        {
            _tmdb = new TMDbClient(Settings.TmdbReadAccessToken);
        }
    }

    public async Task<TMDBModels> ResolveAsync(string name, string? mediaTypeHint = null, CancellationToken cancellationToken = default)
    {
        var parsed = ParseName(name);

        string mediaType = mediaTypeHint ?? "movie";
        if (string.IsNullOrEmpty(mediaTypeHint))
        {
            if (parsed.Season.HasValue || parsed.Episode.HasValue
                || name.Contains("S0", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Season", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = "show";
            }
        }

        var client = _tmdb;
        if (client == null || Settings.TmdbReadAccessToken == "your_tmdb_bearer_token_here")
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
            else
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

    private TMDBModels ParseName(string name)
    {
        var result = new TMDBModels();

        var seMatch = Regex.Match(name, @"S(?<season>\d{1,2})E(?<episode>\d{1,2})", RegexOptions.IgnoreCase);
        if (seMatch.Success)
        {
            result.Season = int.Parse(seMatch.Groups["season"].Value);
            result.Episode = int.Parse(seMatch.Groups["episode"].Value);
            result.Title = name[..seMatch.Index].Replace(".", " ").Replace("_", " ").Trim();
        }
        else
        {
            var yearMatch = Regex.Match(name, @"\b(?<year>19\d{2}|20\d{2})\b");
            if (yearMatch.Success)
            {
                result.Year = yearMatch.Groups["year"].Value;
                result.Title = name[..yearMatch.Index].Replace(".", " ").Replace("_", " ").Replace("(", "").Trim();
            }
            else
            {
                var resMatch = Regex.Match(name, @"(?i)(1080p|720p|2160p|4k|blu-ray)");
                result.Title = resMatch.Success
                    ? name[..resMatch.Index].Replace(".", " ").Replace("_", " ").Trim()
                    : name.Replace(".", " ").Trim();
            }
        }

        result.Title = Regex.Replace(result.Title, @"\s+", " ").Trim('-');
        return result;
    }
}
