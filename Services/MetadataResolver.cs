using System.Reflection;
using System.Text.RegularExpressions;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

public partial class MetadataResolver
{
    private readonly record struct Token(string Text, int Start, int Length, string NormalizedText);
    private readonly record struct Signal(string Id, double Weight, string Detail);

    private static readonly PropertyInfo? ConfidenceProperty =
        typeof(MediaMetadata).GetProperty("Confidence", BindingFlags.Public | BindingFlags.Instance);
        
    private static readonly PropertyInfo? DebugSignalsProperty =
        typeof(MediaMetadata).GetProperty("DebugSignals", BindingFlags.Public | BindingFlags.Instance);

    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "avi", "mov", "wmv", "flv", "m4v", "iso", "dmg", "pkg",
        "exe", "msi", "appx", "apk", "bat", "cmd", "scr", "zip", "rar", "7z",
        "tar", "gz"
    };

    private static readonly HashSet<string> LeadingReleaseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "yts", "rarbg", "fgt", "flux", "kogi", "qxr", "tigole", "sigla", "silence",
        "vxt", "galaxyrg", "evo", "mazemaze", "d3g", "ion10", "pahe", "vyndros",
        "utp", "hazel", "megusta", "mkvvc", "tasty", "ashtray", "shm", "scenefiles",
        "shisui", "don", "ntb", "ctrlhd", "hds", "sbr", "ajp", "crimson", "geckos",
        "amiable", "sparks", "rovers", "drones", "stratos", "psychd", "cocoa", "cadaver",
        "krave", "phoenix", "peekay", "cbgb", "knife", "drg", "spirit", "deflate",
        "reward", "lpd", "veto", "tdo", "ghouls", "jigsaw", "omp", "cmrg", "tgx",
        "amzn", "nf", "dsnp", "hmax", "atvp", "psa", "rbg",
        "subsplease", "erai", "horriblesubs", "judas", "commie", "asw", "doki",
        "ember", "kametsu", "motive", "nano", "bss",
        "fitgirl", "dodi", "elamigos", "xatab", "rg", "chovka", "kaos", "darck", "blackbox"
    };

    private static readonly HashSet<string> AnimeContextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "subsplease", "erai", "horriblesubs", "judas", "commie", "asw", "doki",
        "ember", "kametsu", "motive", "nano", "bss", "anime", "ova", "ona",
        "ncop", "nced", "special", "batch", "dual", "sub", "dub"
    };

    private static readonly HashSet<string> KnownReleaseGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "fitgirl", "dodi", "razor1911", "reloaded", "skidrow", "codex", "plaza", "cpy", 
        "empress", "flt", "rune", "goldberg", "p2p", "tenoke", "simplex", "darksiders", 
        "tinyiso", "darck", "blackbox", "xatab", "chovka", "rg mechanics", "elamigos", "kaos"
    };

    private static readonly HashSet<string> SoftSingleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "proper", "repack", "unrated", "remastered", "remux", "internal",
        "multi", "dual", "audio", "dubbed", "subbed", "hardsub",
        "english", "hindi", "tamil", "telugu", "malayalam", "kannada",
        "french", "spanish", "german", "russian", "japanese", "korean",
        "chinese", "web", "dl", "rip", "bluray", "bdrip", "brrip", "hdtv",
        "hdrip", "dvd", "dvdrip", "cam", "telesync", "tc", "r5", "workprint",
        "x264", "x265", "h264", "h265", "hevc", "av1", "vp9", "vc1", "avc",
        "mpeg2", "divx", "xvid", "10bit", "hdr", "hdr10", "hdr10plus", "dv",
        "dovi", "truehd", "dts", "ma", "atmos", "ac3", "aac", "mp3", "flac",
        "mono", "stereo",
        "razor1911", "reloaded", "skidrow", "codex", "plaza", "cpy", "empress",
        "flt", "rune", "goldberg", "p2p", "tenoke", "simplex", "darksiders", "tinyiso",
        "steam", "gog", "cracked", "crack", "update", "patch", "dlc", "bonus", "supporter"
    };

    private static readonly HashSet<string> SoftPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "directors cut",
        "director cut",
        "special edition",
        "standard edition",
        "deluxe edition",
        "ultimate edition",
        "collectors edition",
        "anniversary edition",
        "complete edition",
        "dual audio",
        "multi audio",
        "dolby vision",
        "true hd",
        "dts hd",
        "dts ma",
        "hdr10 plus",
        "early access",
        "bonus content",
        "day one edition",
        "game of the year edition",
        "goty edition",
        "premium edition",
        "gold edition",
        "supporter edition"
    };

    [GeneratedRegex(@"^\[REQ\]\s*|^\(REQ\)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ReqPrefixRegex();

    [GeneratedRegex(@"(?i)\b(?:fitgirl|dodi|repack|razor1911|reloaded|skidrow|codex|plaza|cpy|steam|gog|cracked|multi\d+|setup[- ._]?fitgirl|elamigos|kaos|empress|flt|rune|goldberg|p2p|tenoke|simplex|darksiders|tinyiso|darck|blackbox|xatab|chovka|rg\s?mechanics)\b", RegexOptions.Compiled)]
    private static partial Regex GameRegex();

    [GeneratedRegex(@"(?i)\b(?:windows|office|adobe|autocad|keygen|activator|patch|installer|portable|multilingual|crack|serial)\b|\.(?:exe|msi|iso|dmg|pkg|appx|apk|bat|cmd|scr)$", RegexOptions.Compiled)]
    private static partial Regex SoftwareRegex();

    [GeneratedRegex(@"(?i)(?:^|[._\-\s])(v|version)[ ._-]*(?<ver>\d+(?:[._-]\d+)+(?:[a-z])?)\b", RegexOptions.Compiled)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    public Task<MediaMetadata> ResolveAsync(string name, string? mediaTypeHint = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var parsed = ParseName(name);
        parsed.Source = name;

        if (!string.IsNullOrWhiteSpace(mediaTypeHint))
        {
            parsed.Type = mediaTypeHint.Trim();
        }

        return Task.FromResult(parsed);
    }

    public MediaMetadata ParseName(string name)
    {
        var result = new MediaMetadata();

        if (string.IsNullOrWhiteSpace(name))
        {
            result.Title = string.Empty;
            result.Type = "movie";
            SetConfidenceIfSupported(result, 0.0);
            return result;
        }

        var workingString = ReqPrefixRegex().Replace(name.Trim(), string.Empty).Trim();

        if (workingString.Length == 0)
        {
            result.Title = string.Empty;
            result.Type = "movie";
            SetConfidenceIfSupported(result, 0.0);
            return result;
        }

        var tokens = Tokenize(workingString);

        var mediaCandidate = ParseAsMedia(workingString, tokens);
        var gameCandidate = ParseAsGame(workingString, tokens);
        var softwareCandidate = ParseAsSoftware(workingString, tokens);

        ApplyCrossDomainSignals(tokens, mediaCandidate.Signals, gameCandidate.Signals, softwareCandidate.Signals);

        // Recalculate confidence after global signals
        mediaCandidate.Confidence = Math.Clamp(mediaCandidate.Signals.Sum(s => s.Weight), 0.0, 1.0);
        gameCandidate.Confidence = Math.Clamp(gameCandidate.Signals.Sum(s => s.Weight), 0.0, 1.0);
        softwareCandidate.Confidence = Math.Clamp(softwareCandidate.Signals.Sum(s => s.Weight), 0.0, 1.0);

        var best = new[] { mediaCandidate, gameCandidate, softwareCandidate }
            .OrderByDescending(c => c.Confidence)
            .First();

        var finalResult = best.Metadata;
        SetConfidenceIfSupported(finalResult, best.Confidence);
        SetDebugSignalsIfSupported(finalResult, best.Signals);

        return finalResult;
    }

    private static void ApplyCrossDomainSignals(
        List<Token> tokens,
        List<Signal> mediaSignals,
        List<Signal> gameSignals,
        List<Signal> softwareSignals)
    {
        bool hasResolution = false;
        bool hasCodec = false;
        bool hasInstaller = false;
        bool hasDlc = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            var txt = tokens[i].NormalizedText;
            var next = i + 1 < tokens.Count ? tokens[i + 1].NormalizedText : string.Empty;

            if (IsResolutionToken(txt)) hasResolution = true;
            if (IsCodecToken(txt, next)) hasCodec = true;
            if (txt is "portable" or "setup" or "crack" or "installer" or "keygen" or "activator") hasInstaller = true;
            if (txt is "dlc" or "ost" or "soundtrack" or "expansion") hasDlc = true;
        }

        if (hasResolution)
        {
            mediaSignals.Add(new Signal("GLOBAL_RESOLUTION", 0.15, string.Empty));
            gameSignals.Add(new Signal("GLOBAL_RESOLUTION_PENALTY", -0.15, string.Empty));
            softwareSignals.Add(new Signal("GLOBAL_RESOLUTION_PENALTY", -0.20, string.Empty));
        }

        if (hasCodec)
        {
            mediaSignals.Add(new Signal("GLOBAL_CODEC", 0.15, string.Empty));
            gameSignals.Add(new Signal("GLOBAL_CODEC_PENALTY", -0.15, string.Empty));
            softwareSignals.Add(new Signal("GLOBAL_CODEC_PENALTY", -0.20, string.Empty));
        }

        if (hasInstaller)
        {
            softwareSignals.Add(new Signal("GLOBAL_INSTALLER", 0.20, string.Empty));
            mediaSignals.Add(new Signal("GLOBAL_INSTALLER_PENALTY", -0.30, string.Empty));
        }

        if (hasDlc)
        {
            gameSignals.Add(new Signal("GLOBAL_DLC", 0.15, string.Empty));
            mediaSignals.Add(new Signal("GLOBAL_DLC_PENALTY", -0.20, string.Empty));
            softwareSignals.Add(new Signal("GLOBAL_DLC_PENALTY", -0.20, string.Empty));
        }
    }

    private static (MediaMetadata Metadata, double Confidence, List<Signal> Signals) ParseAsMedia(string workingString, List<Token> tokens)
    {
        var result = new MediaMetadata { Type = "movie" };
        var signals = new List<Signal> { new Signal("BASE_MEDIA", 0.12, string.Empty) };
        int titleBoundary = tokens.Count;

        bool tvDetected = false, absoluteEpisodeDetected = false, yearDetected = false, resolutionDetected = false;

        if (TryDetectTv(tokens, workingString, out var season, out var episode, out var tvBoundary, out absoluteEpisodeDetected))
        {
            tvDetected = true;
            result.Type = "show";
            result.Season = season;
            if (episode > 0) result.Episode = episode;
            titleBoundary = Math.Min(titleBoundary, tvBoundary);
            signals.Add(absoluteEpisodeDetected 
                ? new Signal("TV_ANIME_ABSOLUTE_EP", 0.35, $"Ep: {episode} [Boundary: {tvBoundary}]") 
                : new Signal("TV_STANDARD", 0.45, $"S: {season}, Ep: {episode} [Boundary: {tvBoundary}]"));
        }

        if (TryDetectYear(tokens, out var year, out var yearBoundary))
        {
            yearDetected = true;
            result.Year = year;
            titleBoundary = Math.Min(titleBoundary, yearBoundary);
            signals.Add(new Signal("YEAR_DETECTED", 0.18, $"{year} [Boundary: {yearBoundary}]"));
        }

        if (TryDetectResolution(tokens, out var resolution, out var resBoundary))
        {
            resolutionDetected = true;
            result.Resolution = resolution;
            titleBoundary = Math.Min(titleBoundary, resBoundary);
            signals.Add(new Signal("RESOLUTION_DETECTED", 0.10, $"{resolution} [Boundary: {resBoundary}]"));
        }

        if (TryDetectQuality(tokens, out var quality, out var qualityBoundary))
        {
            result.Quality = quality;
            titleBoundary = Math.Min(titleBoundary, qualityBoundary);
            signals.Add(new Signal("QUALITY_DETECTED", 0.10, $"{quality} [Boundary: {qualityBoundary}]"));
        }

        if (TryDetectCodec(tokens, out var codecBoundary))
        {
            titleBoundary = Math.Min(titleBoundary, codecBoundary);
            signals.Add(new Signal("CODEC_DETECTED", 0.05, $"[Boundary: {codecBoundary}]"));
        }

        var titleTokens = tokens.Take(Math.Clamp(titleBoundary, 0, tokens.Count)).ToList();
        TrimLeadingReleaseTags(titleTokens);
        TrimTrailingSoftTags(titleTokens);

        result.Title = BuildTitle(titleTokens);
        if (string.IsNullOrWhiteSpace(result.Title))
        {
            result.Title = BuildFallbackTitle(tokens);
            signals.Add(new Signal("TITLE_FALLBACK", -0.1, string.Empty));
        }
        else signals.Add(new Signal("TITLE_EXISTS", 0.05, string.Empty));

        if (tvDetected && yearDetected) signals.Add(new Signal("PENALTY_TV_YEAR", -0.10, string.Empty));
        if (LooksLikeSoftware(workingString) || LooksLikeGame(workingString)) signals.Add(new Signal("PENALTY_MEDIA_MISMATCH", -0.30, string.Empty));
        if (resolutionDetected && !tvDetected && !yearDetected) signals.Add(new Signal("PENALTY_ORPHAN_RES", -0.05, string.Empty));

        double confidence = Math.Clamp(signals.Sum(s => s.Weight), 0.0, 1.0);
        return (result, confidence, signals);
    }

    private static (MediaMetadata Metadata, double Confidence, List<Signal> Signals) ParseAsGame(string workingString, List<Token> tokens)
    {
        var result = new MediaMetadata { Type = "game" };
        var signals = new List<Signal> { new Signal("BASE_GAME", 0.10, string.Empty) };
        
        var boundaries = new List<int> { tokens.Count };

        if (LooksLikeGame(workingString)) signals.Add(new Signal("TYPE_GAME_REGEX", 0.30, string.Empty));

        bool hasVersion = TryDetectVersion(workingString, tokens, out var version, out var versionBoundary);
        if (hasVersion)
        {
            result.Version = version;
            boundaries.Add(versionBoundary);
            signals.Add(new Signal("VERSION_DETECTED", 0.15, $"{version} [Boundary: {versionBoundary}]"));
        }

        bool hasEdition = TryDetectGameEdition(tokens, out var edition, out var editionBoundary);
        if (hasEdition)
        {
            result.Edition = edition;
            boundaries.Add(editionBoundary);
            signals.Add(new Signal("EDITION_DETECTED", 0.15, $"{edition} [Boundary: {editionBoundary}]"));
        }

        bool hasGroup = TryDetectReleaseGroup(tokens, out var releaseGroup, out var groupBoundary);
        if (hasGroup)
        {
            result.ReleaseGroup = releaseGroup;
            boundaries.Add(groupBoundary);
            signals.Add(new Signal("RELEASE_GROUP", 0.15, $"{releaseGroup} [Boundary: {groupBoundary}]"));
        }

        bool hasDlc = TryDetectDlc(tokens, out var dlcBoundary);
        if (hasDlc)
        {
            result.HasDlc = true;
            boundaries.Add(dlcBoundary);
            signals.Add(new Signal("DLC_DETECTED", 0.10, $"[Boundary: {dlcBoundary}]"));
        }

        if (TryDetectGameBoundary(tokens, out var gameBoundary))
        {
            boundaries.Add(gameBoundary);
            signals.Add(new Signal("GAME_BOUNDARY_DETECTED", 0.20, $"[Boundary: {gameBoundary}]"));
        }

        bool hasYear = TryDetectYear(tokens, out var year, out var yearBoundary);
        if (hasYear)
        {
            result.Year = year;
            boundaries.Add(yearBoundary);
            signals.Add(new Signal("YEAR_DETECTED", 0.10, $"{year} [Boundary: {yearBoundary}]"));
        }
        
        int comboCount = (hasVersion ? 1 : 0) + (hasEdition ? 1 : 0) + (hasGroup ? 1 : 0) + (hasDlc ? 1 : 0);
        if (comboCount >= 2)
        {
            signals.Add(new Signal("STRONG_GAME_COMBO", 0.25, string.Empty));
        }

        // Strong positive signals for games
        if (tokens.Count > 1 && tokens[^1].NormalizedText.Length <= 4 && int.TryParse(tokens[^1].NormalizedText, out _))
        {
             signals.Add(new Signal("NUMERIC_TITLE_SUFFIX", 0.10, string.Empty));
        }

        int titleBoundary = SelectBestBoundary(boundaries, tokens);
        var titleTokens = tokens.Take(Math.Clamp(titleBoundary, 0, tokens.Count)).ToList();
        
        result.Title = BuildTitle(titleTokens);
        if (string.IsNullOrWhiteSpace(result.Title))
        {
            result.Title = BuildFallbackTitle(tokens);
            signals.Add(new Signal("TITLE_FALLBACK", -0.1, string.Empty));
        }
        else signals.Add(new Signal("TITLE_EXISTS", 0.05, string.Empty));

        if (TryDetectTv(tokens, workingString, out _, out _, out _, out _)) signals.Add(new Signal("PENALTY_GAME_TV", -0.40, string.Empty));
        if (TryDetectResolution(tokens, out _, out _)) signals.Add(new Signal("PENALTY_GAME_RES", -0.20, string.Empty));

        double confidence = Math.Clamp(signals.Sum(s => s.Weight), 0.0, 1.0);
        return (result, confidence, signals);
    }

    private static (MediaMetadata Metadata, double Confidence, List<Signal> Signals) ParseAsSoftware(string workingString, List<Token> tokens)
    {
        var result = new MediaMetadata { Type = "other" };
        var signals = new List<Signal> { new Signal("BASE_SOFTWARE", 0.10, string.Empty) };
        
        var boundaries = new List<int> { tokens.Count };

        if (LooksLikeSoftware(workingString)) signals.Add(new Signal("TYPE_SOFTWARE_REGEX", 0.35, string.Empty));

        bool hasVersion = TryDetectVersion(workingString, tokens, out var version, out var versionBoundary);
        if (hasVersion)
        {
            result.Version = version;
            boundaries.Add(versionBoundary);
            signals.Add(new Signal("VERSION_DETECTED", 0.20, $"{version} [Boundary: {versionBoundary}]"));
        }

        var osBoundary = tokens.FindIndex(t => t.NormalizedText is "mac" or "macos" or "linux" or "windows" or "win" or "x64" or "x86" or "amd64" or "arm64");
        bool hasOs = osBoundary >= 0;
        if (hasOs)
        {
            boundaries.Add(osBoundary);
            signals.Add(new Signal("OS_ARCH_DETECTED", 0.15, $"[Boundary: {osBoundary}]"));
        }
        
        bool hasInstaller = TryDetectInstallerType(tokens, out var installer, out var installBoundary);
        if (hasInstaller)
        {
            result.InstallerType = installer;
            boundaries.Add(installBoundary);
            signals.Add(new Signal("INSTALLER_DETECTED", 0.15, $"{installer} [Boundary: {installBoundary}]"));
        }

        if (TryDetectYear(tokens, out var year, out var yearBoundary))
        {
            result.Year = year;
            boundaries.Add(yearBoundary);
            signals.Add(new Signal("YEAR_DETECTED", 0.10, $"{year} [Boundary: {yearBoundary}]"));
        }
        
        int comboCount = (hasVersion ? 1 : 0) + (hasOs ? 1 : 0) + (hasInstaller ? 1 : 0);
        if (comboCount >= 2)
        {
            signals.Add(new Signal("STRONG_SOFTWARE_COMBO", 0.25, string.Empty));
        }

        if (workingString.Contains("adobe", StringComparison.OrdinalIgnoreCase) || 
            workingString.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
            workingString.Contains("autodesk", StringComparison.OrdinalIgnoreCase))
        {
            // Verify it's an actual token to avoid substring false positives
            if (tokens.Any(t => t.NormalizedText is "adobe" or "microsoft" or "autodesk"))
            {
                signals.Add(new Signal("KNOWN_VENDOR", 0.20, string.Empty));
            }
        }

        int titleBoundary = SelectBestBoundary(boundaries, tokens);
        var titleTokens = tokens.Take(Math.Clamp(titleBoundary, 0, tokens.Count)).ToList();

        result.Title = BuildTitle(titleTokens);
        if (string.IsNullOrWhiteSpace(result.Title))
        {
            result.Title = BuildFallbackTitle(tokens);
            signals.Add(new Signal("TITLE_FALLBACK", -0.1, string.Empty));
        }
        else signals.Add(new Signal("TITLE_EXISTS", 0.05, string.Empty));

        if (TryDetectTv(tokens, workingString, out _, out _, out _, out _)) signals.Add(new Signal("PENALTY_SOFT_TV", -0.40, string.Empty));

        double confidence = Math.Clamp(signals.Sum(s => s.Weight), 0.0, 1.0);
        return (result, confidence, signals);
    }

    private static bool LooksLikeSoftware(string input) => SoftwareRegex().IsMatch(input);
    private static bool LooksLikeGame(string input) => GameRegex().IsMatch(input);

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < input.Length)
        {
            while (i < input.Length && IsSeparator(input[i])) i++;
            if (i >= input.Length) break;

            int start = i;
            while (i < input.Length && !IsSeparator(input[i])) i++;

            int length = i - start;
            var text = input.Substring(start, length);
            tokens.Add(new Token(text, start, length, NormalizeForMatch(text)));
        }

        return tokens;
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c is '.' or '_' or '-' or '/' or '\\';

    private static string CleanToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        return token.Trim('(', ')', '[', ']', '{', '}', '<', '>', '"', '\'', ',', ';', ':', '!', '?');
    }

    private static string NormalizeForMatch(string token)
    {
        ReadOnlySpan<char> span = token;
        Span<char> buffer = stackalloc char[span.Length];
        int length = 0;

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c is '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or '"' or '\'' or '’' or ',' or ';' or ':' or '!' or '?') continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }

    private static int FindTokenIndexAtOrAfter(IReadOnlyList<Token> tokens, int charIndex)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Start >= charIndex || (token.Start <= charIndex && charIndex < token.Start + token.Length)) return i;
        }
        return tokens.Count;
    }

    private static bool TryDetectTv(
        IReadOnlyList<Token> tokens, string original, out int season, out int episode,
        out int boundaryIndex, out bool absoluteEpisodeDetected)
    {
        season = 0;
        episode = 0;
        boundaryIndex = -1;
        absoluteEpisodeDetected = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;
            var next = i + 1 < tokens.Count ? tokens[i + 1].NormalizedText : string.Empty;
            var afterNext = i + 2 < tokens.Count ? tokens[i + 2].NormalizedText : string.Empty;

            if (TryParseSeasonEpisodeToken(current, out season, out episode))
            {
                boundaryIndex = i;
                return season > 0;
            }

            if (current.StartsWith('s') && current.Length > 2 && int.TryParse(current[1..], out season) && season > 0)
            {
                boundaryIndex = i;
                if (TryParseEpisodeToken(next, out episode)) return true;
                if (next == "e" && TryParseEpisodeToken(afterNext, out episode)) return true;
                if (TryParseCompoundEpisodeToken(next, out episode)) return true;
                return true; // Return true even if no episode is found, as SXX is a strong signal for a show
            }

            if (TryParseNumericXPattern(current, out season, out episode))
            {
                boundaryIndex = i;
                return true;
            }

            if (current is "season" and not "")
            {
                if (TryParseNumberToken(next, out season))
                {
                    boundaryIndex = i;
                    TryParseEpisodeWordPattern(tokens, i + 2, out episode);
                    return true;
                }
            }

            if (current == "s" && TryParseNumberToken(next, out season))
            {
                boundaryIndex = i;
                TryParseEpisodeWordPattern(tokens, i + 2, out episode);
                return true;
            }

            if (TryDetectAbsoluteEpisode(tokens, original, i, out episode))
            {
                absoluteEpisodeDetected = true;
                season = 0;
                boundaryIndex = i;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSeasonEpisodeToken(string token, out int season, out int episode)
    {
        season = 0;
        episode = 0;

        if (token.Length < 4 || token[0] != 's') return false;
        int eIndex = token.IndexOf('e', 1);
        if (eIndex <= 1) return false;
        if (!int.TryParse(token[1..eIndex], out season) || season <= 0) return false;

        var episodePart = token[(eIndex + 1)..];
        if (episodePart.Length == 0) return false;

        int secondE = episodePart.IndexOf('e');
        if (secondE >= 0) episodePart = episodePart[..secondE];

        return int.TryParse(episodePart, out episode) && episode > 0;
    }

    private static bool TryParseNumericXPattern(string token, out int season, out int episode)
    {
        season = 0;
        episode = 0;
        int xIndex = token.IndexOf('x');
        if (xIndex <= 0 || xIndex >= token.Length - 1) return false;
        
        var left = token[..xIndex];
        var right = token[(xIndex + 1)..];
        return int.TryParse(left, out season) && season > 0 && int.TryParse(right, out episode) && episode > 0;
    }

    private static bool TryParseEpisodeToken(string token, out int episode)
    {
        episode = 0;
        if (token.Length < 2 || token[0] != 'e') return false;
        return int.TryParse(token[1..], out episode) && episode > 0;
    }

    private static bool TryParseCompoundEpisodeToken(string token, out int episode)
    {
        episode = 0;
        if (token.Length < 3 || token[0] != 'e') return false;
        
        var raw = token[1..];
        int secondE = raw.IndexOf('e');
        if (secondE >= 0) raw = raw[..secondE];
        
        return int.TryParse(raw, out episode) && episode > 0;
    }

    private static bool TryParseNumberToken(string token, out int value)
        => int.TryParse(token, out value) && value > 0;

    private static bool TryParseEpisodeWordPattern(IReadOnlyList<Token> tokens, int index, out int episode)
    {
        episode = 0;
        if (index >= tokens.Count) return false;
        var word = tokens[index].NormalizedText;
        if (word is not ("episode" or "ep")) return false;
        if (index + 1 >= tokens.Count) return false;
        return TryParseNumberToken(tokens[index + 1].NormalizedText, out episode);
    }

    private static bool TryDetectAbsoluteEpisode(IReadOnlyList<Token> tokens, string original, int i, out int episode)
    {
        episode = 0;
        var token = tokens[i];
        var current = token.NormalizedText;

        if (!TryParseNumberToken(current, out episode)) return false;
        if (episode <= 0 || episode > 500) return false;
        if (IsYearLike(current)) return false;

        bool isPositionallySafe = i >= tokens.Count / 2;

        if (i > 0)
        {
            var prev = tokens[i - 1].NormalizedText;
            if (prev is "ep" or "episode" or "e") return HasStrongMetadataAhead(tokens, i + 1);

            if (HasHyphenLikeSeparatorBetween(original, tokens[i - 1], token) &&
                HasStrongMetadataAhead(tokens, i + 1) && IsLikelyAnimeContext(tokens))
            {
                return true;
            }
        }

        if (IsLikelyAnimeContext(tokens) && HasStrongMetadataAhead(tokens, i + 1) && isPositionallySafe)
        {
            return true;
        }

        if (tokens.Count <= 4 && i == tokens.Count - 1 && episode <= 500)
        {
            return true;
        }

        return false;
    }

    private static bool HasHyphenLikeSeparatorBetween(string original, Token left, Token right)
    {
        int start = left.Start + left.Length;
        int length = right.Start - start;

        if (length <= 0 || start < 0 || start >= original.Length) return false;
        if (start + length > original.Length) length = original.Length - start;

        var gap = original.AsSpan(start, length);
        for (int i = 0; i < gap.Length; i++)
        {
            if (gap[i] is '-' or '–' or '—' or ':' or '|' or '~') return true;
        }
        return false;
    }

    private static bool IsLikelyAnimeContext(IReadOnlyList<Token> tokens)
    {
        if (tokens.Count == 0) return false;
        if (AnimeContextTags.Contains(tokens[0].NormalizedText)) return true;

        for (int i = 0; i < Math.Min(tokens.Count, 4); i++)
        {
            if (AnimeContextTags.Contains(tokens[i].NormalizedText)) return true;
        }
        return false;
    }

    private static bool IsYearLike(string token)
        => token.Length == 4 && int.TryParse(token, out var y) && y is >= 1900 and <= 2101;

    private static bool TryDetectYear(IReadOnlyList<Token> tokens, out string year, out int boundaryIndex)
    {
        year = string.Empty;
        boundaryIndex = -1;

        var currentYear = DateTime.UtcNow.Year + 1;
        int bestScore = 0;
        int bestIndex = -1;
        string? bestYear = null;

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;

            if (current.Length != 4 || !int.TryParse(current, out var parsedYear)) continue;
            if (parsedYear < 1900 || parsedYear > currentYear) continue;
            if (i + 1 < tokens.Count && IsYearLike(tokens[i + 1].NormalizedText)) continue;

            int score = 0;
            if (IsBracketed(tokens[i].Text)) score += 2;
            if (HasStrongMetadataAhead(tokens, i + 1)) score += 1;
            if (i == tokens.Count - 1) score += 1;

            if (score == 0) continue;

            if (score > bestScore || (score == bestScore && i > bestIndex))
            {
                bestScore = score;
                bestIndex = i;
                bestYear = CleanToken(tokens[i].Text);
            }
        }

        if (bestIndex >= 0 && bestYear is not null)
        {
            year = bestYear;
            boundaryIndex = bestIndex;
            return true;
        }

        return false;
    }

    private static bool IsBracketed(string token)
        => (token.StartsWith('(') && token.EndsWith(')')) || (token.StartsWith('[') && token.EndsWith(']'));

    private static bool HasStrongMetadataAhead(IReadOnlyList<Token> tokens, int startIndex)
    {
        for (int i = startIndex; i < tokens.Count && i <= startIndex + 3; i++)
        {
            var current = tokens[i].NormalizedText;
            var next = i + 1 < tokens.Count ? tokens[i + 1].NormalizedText : string.Empty;

            if (IsResolutionToken(current) || IsQualityToken(current, next) ||
                IsCodecToken(current, next) || IsVersionPrefix(current) ||
                IsTvPrefix(current) || current is "episode" or "ep")
            {
                return true;
            }

            if (current == "web" && (next == "dl" || next == "rip")) return true;
        }
        return false;
    }

    private static bool TryDetectResolution(IReadOnlyList<Token> tokens, out string resolution, out int boundaryIndex)
    {
        resolution = string.Empty;
        boundaryIndex = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;

            if (current is "2160p" or "4k" or "uhd") { resolution = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "1080p" or "720p" or "480p") { resolution = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
        }
        return false;
    }

    private static bool TryDetectQuality(IReadOnlyList<Token> tokens, out string quality, out int boundaryIndex)
    {
        quality = string.Empty;
        boundaryIndex = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;
            var next = i + 1 < tokens.Count ? tokens[i + 1].NormalizedText : string.Empty;

            if (current == "web" && next == "dl") { quality = CleanToken(tokens[i].Text) + "-" + CleanToken(tokens[i+1].Text); boundaryIndex = i; return true; }
            if (current == "web" && next == "rip") { quality = CleanToken(tokens[i].Text) + CleanToken(tokens[i+1].Text); boundaryIndex = i; return true; }
            if (current is "bluray" or "brrip" or "bdrip" or "bdr") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "webrip") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "webdl") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "hdtv") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "hdrip") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "dvd" or "dvdr" or "dvdrip") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "cam") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "telesync" or "ts") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "tc") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "vhsrip") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "r5") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "workprint") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
            if (current is "remux") { quality = CleanToken(tokens[i].Text); boundaryIndex = i; return true; }
        }
        return false;
    }

    private static bool TryDetectCodec(IReadOnlyList<Token> tokens, out int boundaryIndex)
    {
        boundaryIndex = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;
            var next = i + 1 < tokens.Count ? tokens[i + 1].NormalizedText : string.Empty;

            if (current == "dolby" && next == "vision") { boundaryIndex = i; return true; }
            if (current == "dts" && next is "hd" or "ma") { boundaryIndex = i; return true; }

            if (current is "x264" or "x265" or "h264" or "h265" or "hevc" or "av1" or "vp9" or "vc1" or "avc" or "mpeg2"
                or "divx" or "xvid" or "10bit" or "hdr" or "hdr10" or "hdr10plus" or "dv" or "dovi" or "truehd"
                or "atmos" or "ac3" or "aac" or "mp3" or "flac" or "mono" or "stereo")
            {
                boundaryIndex = i; return true;
            }
        }
        return false;
    }

    private static bool TryDetectVersion(string original, IReadOnlyList<Token> tokens, out string version, out int boundaryIndex)
    {
        version = string.Empty;
        boundaryIndex = -1;

        var match = VersionRegex().Match(original);
        if (match.Success && match.Groups["ver"].Value.Length > 0)
        {
            var raw = match.Groups["ver"].Value.Trim().Replace(' ', '.').Replace('_', '.').Replace('-', '.');
            version = "v" + raw;
            boundaryIndex = FindTokenIndexAtOrAfter(tokens, match.Index);
            return true;
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;
            var next = i + 1 < tokens.Count ? tokens[i + 1].NormalizedText : string.Empty;

            if ((current is "build" or "update" or "patch" or "rev") && next.Length > 0 && char.IsDigit(next[0]))
            {
                version = CleanToken(tokens[i].Text) + " " + CleanToken(tokens[i+1].Text);
                boundaryIndex = i;
                return true;
            }

            if (current.Length > 2 && current.Contains('.') && char.IsDigit(current[0]) && char.IsDigit(current[^1]))
            {
                 if (current.Split('.').Length >= 2)
                 {
                     version = "v" + current;
                     boundaryIndex = i;
                     return true;
                 }
            }
        }

        return false;
    }

    private static bool TryDetectGameEdition(IReadOnlyList<Token> tokens, out string edition, out int boundaryIndex)
    {
        edition = string.Empty;
        boundaryIndex = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;

            // Standalone edition tokens and compounds (e.g., "Digital Deluxe", "Ultimate")
            if (current is "digital" or "ultimate" or "premium" or "standard" or "complete" or "supporter")
            {
                // Safety: Avoid catching these as the first word of a title unless followed by strong edition signals
                bool isFollowedByStrong = (i + 1 < tokens.Count && tokens[i + 1].NormalizedText is "edition" or "deluxe" or "ultimate" or "bundle" or "collection");
                if (i > 0 || isFollowedByStrong)
                {
                    edition = CleanToken(tokens[i].Text);
                    boundaryIndex = i;

                    int lookAhead = i + 1;
                    if (lookAhead < tokens.Count && tokens[lookAhead].NormalizedText is "deluxe" or "ultimate")
                    {
                        edition += " " + CleanToken(tokens[lookAhead].Text);
                        lookAhead++;
                    }

                    if (lookAhead < tokens.Count && tokens[lookAhead].NormalizedText is "edition" or "bundle" or "collection")
                    {
                        edition += " " + CleanToken(tokens[lookAhead].Text);
                    }

                    return true;
                }
            }
            
            if (current is "collection" or "bundle")
            {
                 edition = CleanToken(tokens[i].Text);
                 boundaryIndex = i;
                 
                 if (i > 0)
                 {
                     var prev = tokens[i - 1].NormalizedText;
                     if (prev is "complete" or "ultimate" or "masterpiece" or "hd" or "remastered")
                     {
                         edition = CleanToken(tokens[i - 1].Text) + " " + CleanToken(tokens[i].Text);
                         boundaryIndex = i - 1;
                     }
                 }
                 return true;
            }
            
            if (i > 0 && current == "edition")
            {
                 var prev = tokens[i - 1].NormalizedText;
                 if (prev is "deluxe" or "ultimate" or "standard" or "special" or "collectors" or "anniversary" or "complete" or "goty" or "premium" or "gold" or "definitive" or "legendary" or "enhanced" or "directors" or "masterpiece" or "supporter")
                 {
                     edition = CleanToken(tokens[i - 1].Text) + " " + CleanToken(tokens[i].Text);
                     boundaryIndex = i - 1;
                     
                     if (i > 1 && prev == "directors" && tokens[i-2].NormalizedText == "cut")
                     {
                         edition = CleanToken(tokens[i - 2].Text) + " " + CleanToken(tokens[i - 1].Text) + " " + CleanToken(tokens[i].Text);
                         boundaryIndex = i - 2;
                     }
                     
                     return true;
                 }
            }
            if (current == "goty")
            {
                 edition = CleanToken(tokens[i].Text);
                 boundaryIndex = i;
                 if (i + 1 < tokens.Count && tokens[i+1].NormalizedText == "edition") edition = CleanToken(tokens[i].Text) + " " + CleanToken(tokens[i+1].Text);
                 return true;
            }
        }
        return false;
    }

    private static bool TryDetectReleaseGroup(IReadOnlyList<Token> tokens, out string group, out int boundaryIndex)
    {
        group = string.Empty;
        boundaryIndex = -1;

        for(int i = 0; i < tokens.Count; i++)
        {
            var txt = tokens[i].NormalizedText;
            if (KnownReleaseGroups.Contains(txt))
            {
                group = CleanToken(tokens[i].Text);
                boundaryIndex = i;
                return true;
            }
            
            if (i < tokens.Count - 1)
            {
                var normalizedTwo = txt + " " + tokens[i+1].NormalizedText;
                if (KnownReleaseGroups.Contains(normalizedTwo))
                {
                    group = CleanToken(tokens[i].Text) + " " + CleanToken(tokens[i+1].Text);
                    boundaryIndex = i;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryDetectDlc(IReadOnlyList<Token> tokens, out int boundaryIndex)
    {
        boundaryIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
             if (tokens[i].NormalizedText is "dlc" or "dlcs" or "bonus" or "bonuses" or "ost" or "soundtrack" or "expansion")
             {
                 boundaryIndex = i; return true;
             }
        }
        return false;
    }
    
    private static bool TryDetectInstallerType(IReadOnlyList<Token> tokens, out string installer, out int boundaryIndex)
    {
        installer = string.Empty;
        boundaryIndex = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;
            if (current is "portable" or "setup" or "crack" or "installer" or "keygen" or "activator" or "retail")
            {
                installer = CleanToken(tokens[i].Text);
                boundaryIndex = i;
                return true;
            }
        }
        return false;
    }

    private static int SelectBestBoundary(List<int> boundaries, List<Token> tokens)
    {
        if (boundaries.Count == 0) return tokens.Count;
        
        boundaries.Sort();
        
        // If the earliest boundary is right at the start, try to find a better one
        if (boundaries[0] == 0 && boundaries.Count > 1)
        {
            return boundaries[1];
        }
        
        // If year is the earliest boundary, but there's a strong game/software boundary right after, use that instead
        if (boundaries.Count > 1)
        {
            int first = boundaries[0];
            int second = boundaries[1];
            if (first >= 0 && first < tokens.Count && IsYearLike(tokens[first].NormalizedText))
            {
                if (second > first && second - first <= 2)
                {
                    return second; // Prefer the structural boundary over just the year if it immediately follows
                }
            }
        }

        return boundaries[0];
    }

    private static bool TryDetectGameBoundary(IReadOnlyList<Token> tokens, out int boundaryIndex)
    {
        boundaryIndex = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i].NormalizedText;
            var next = i + 1 < tokens.Count ? tokens[i + 1].NormalizedText : string.Empty;

            // Removed overlapping tokens already handled by specific extractors
            if (current is "update" or "patch" or "crack" or "cracked" or "hotfix" or "build" or "repack" or "multi" or "languages" or "lang" or "mod")
            {
                boundaryIndex = i; return true;
            }

            if (current.StartsWith("multi") && current.Length > 5 && char.IsDigit(current[5]))
            {
                boundaryIndex = i; return true;
            }

            if (current is "english" or "french" or "spanish" or "german" or "russian" or "japanese" or "korean" or "chinese")
            {
                boundaryIndex = i; return true;
            }

            if (GameRegex().IsMatch(current))
            {
                boundaryIndex = i; return true;
            }
        }

        return false;
    }

    private static bool IsResolutionToken(string current) => current is "2160p" or "4k" or "uhd" or "1080p" or "720p" or "480p";

    private static bool IsQualityToken(string current, string next)
    {
        if (current == "web" && (next == "dl" || next == "rip")) return true;
        return current is "bluray" or "brrip" or "bdrip" or "bdr" or "webrip" or "webdl" or "hdtv" or "hdrip"
            or "dvd" or "dvdr" or "dvdrip" or "cam" or "telesync" or "ts" or "tc" or "vhsrip" or "r5" or "workprint" or "remux";
    }

    private static bool IsCodecToken(string current, string next)
    {
        if (current == "dolby" && next == "vision") return true;
        if (current == "dts" && next is "hd" or "ma") return true;
        return current is "x264" or "x265" or "h264" or "h265" or "hevc" or "av1" or "vp9" or "vc1" or "avc" or "mpeg2"
            or "divx" or "xvid" or "10bit" or "hdr" or "hdr10" or "hdr10plus" or "dv" or "dovi" or "truehd"
            or "atmos" or "ac3" or "aac" or "mp3" or "flac" or "mono" or "stereo";
    }

    private static bool IsVersionPrefix(string current) => current.StartsWith('v') && current.Length > 1 && char.IsDigit(current[1]);
    private static bool IsTvPrefix(string current) => current.StartsWith('s') && current.Length > 2 && char.IsDigit(current[1]);

    private static void TrimLeadingReleaseTags(List<Token> tokens)
    {
        while (tokens.Count > 1)
        {
            var current = tokens[0].NormalizedText;

            if (string.IsNullOrWhiteSpace(current)) { tokens.RemoveAt(0); continue; }
            if (LeadingReleaseTags.Contains(current) || current.StartsWith("multi", StringComparison.OrdinalIgnoreCase)) { tokens.RemoveAt(0); continue; }
            break;
        }
    }

    private static void TrimTrailingSoftTags(List<Token> tokens)
    {
        while (tokens.Count > 0)
        {
            if (tokens.Count >= 3)
            {
                var three = $"{tokens[^3].NormalizedText} {tokens[^2].NormalizedText} {tokens[^1].NormalizedText}";
                if (SoftPhrases.Contains(three)) { tokens.RemoveRange(tokens.Count - 3, 3); continue; }
            }

            if (tokens.Count >= 2)
            {
                var two = $"{tokens[^2].NormalizedText} {tokens[^1].NormalizedText}";
                if (SoftPhrases.Contains(two)) { tokens.RemoveRange(tokens.Count - 2, 2); continue; }
            }

            var last = tokens[^1].NormalizedText;

            if (string.IsNullOrWhiteSpace(last)) { tokens.RemoveAt(tokens.Count - 1); continue; }
            if (SoftSingleTokens.Contains(last) || last.StartsWith("multi", StringComparison.OrdinalIgnoreCase)) { tokens.RemoveAt(tokens.Count - 1); continue; }
            break;
        }
    }

    private static string BuildTitle(IEnumerable<Token> tokens)
    {
        var parts = new List<string>();
        foreach (var token in tokens)
        {
            var cleaned = CleanToken(token.Text);
            if (!string.IsNullOrWhiteSpace(cleaned)) parts.Add(cleaned);
        }
        return WhitespaceRegex().Replace(string.Join(" ", parts), " ").Trim();
    }

    private static string BuildFallbackTitle(IReadOnlyList<Token> tokens)
    {
        var parts = new List<string>();
        foreach (var token in tokens)
        {
            var cleaned = CleanToken(token.Text);
            if (string.IsNullOrWhiteSpace(cleaned) || IsKnownExtension(cleaned)) continue;
            if (parts.Count == 0 && (LeadingReleaseTags.Contains(cleaned) || SoftSingleTokens.Contains(cleaned))) continue;

            parts.Add(cleaned);
            if (parts.Count == 3) break;
        }
        return WhitespaceRegex().Replace(string.Join(" ", parts), " ").Trim();
    }

    private static bool IsKnownExtension(string token) => KnownExtensions.Contains(token);

    private static void SetConfidenceIfSupported(MediaMetadata result, double confidence)
    {
        if (ConfidenceProperty is null || !ConfidenceProperty.CanWrite) return;
        
        object? value = null;
        if (ConfidenceProperty.PropertyType == typeof(double)) value = confidence;
        else if (ConfidenceProperty.PropertyType == typeof(float)) value = (float)confidence;
        else if (ConfidenceProperty.PropertyType == typeof(decimal)) value = (decimal)confidence;
        else if (ConfidenceProperty.PropertyType == typeof(int)) value = (int)Math.Round(confidence * 100);

        if (value is not null) ConfidenceProperty.SetValue(result, value);
    }
    
    private static void SetDebugSignalsIfSupported(MediaMetadata result, List<Signal> signals)
    {
        if (DebugSignalsProperty is null || !DebugSignalsProperty.CanWrite) return;

        var formatted = signals.Select(s => string.IsNullOrWhiteSpace(s.Detail) 
            ? $"{s.Id} ({s.Weight:+#.##;-#.##;0})" 
            : $"{s.Id} ({s.Weight:+#.##;-#.##;0}) [{s.Detail}]").ToList();

        if (DebugSignalsProperty.PropertyType.IsAssignableFrom(typeof(List<string>)))
        {
            DebugSignalsProperty.SetValue(result, formatted);
        }
        else if (DebugSignalsProperty.PropertyType == typeof(string[]))
        {
            DebugSignalsProperty.SetValue(result, formatted.ToArray());
        }
    }
}