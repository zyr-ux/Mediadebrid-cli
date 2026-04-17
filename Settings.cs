using System;
using DotNetEnv;

namespace MediaDebrid_cli;

/// <summary>
/// Application-wide configuration loaded from environment variables / .env file.
/// </summary>
public static class Settings
{
    public static string RealDebridApiToken { get; private set; } = "";
    public static string MediaRoot { get; private set; } = "./media";
    public static bool ParallelDownloadEnabled { get; private set; } = true;
    public static int ConnectionsPerFile { get; private set; } = 8;
    public static string TmdbReadAccessToken { get; private set; } = "";
    public static int TmdbCacheTtlSeconds { get; private set; } = 86400;

    public static void Load()
    {
        // Silently ignore a missing .env file; system env vars may be used instead.
        Env.TraversePath().Load();

        RealDebridApiToken = Environment.GetEnvironmentVariable("REAL_DEBRID_API_TOKEN") ?? "";
        MediaRoot = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "./media";

        if (bool.TryParse(Environment.GetEnvironmentVariable("PARALLEL_DOWNLOAD_ENABLED"), out bool pd))
            ParallelDownloadEnabled = pd;

        if (int.TryParse(Environment.GetEnvironmentVariable("CONNECTIONS_PER_FILE"), out int cpf))
            ConnectionsPerFile = cpf;

        TmdbReadAccessToken = Environment.GetEnvironmentVariable("TMDB_READ_ACCESS_TOKEN") ?? "";

        if (int.TryParse(Environment.GetEnvironmentVariable("TMDB_CACHE_TTL_SECONDS"), out int ttl))
            TmdbCacheTtlSeconds = ttl;
    }
}
