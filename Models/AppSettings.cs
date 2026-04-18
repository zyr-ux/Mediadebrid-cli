using System.Text.Json.Serialization;

namespace MediaDebrid_cli.Models;

public class AppSettings
{
    [JsonPropertyName("real_debrid_api_key")]
    public string RealDebridApiToken { get; set; } = "";

    [JsonPropertyName("media_root")]
    public string MediaRoot { get; set; } = "./media";

    [JsonPropertyName("parallel_download")]
    public bool ParallelDownloadEnabled { get; set; } = true;

    [JsonPropertyName("connections_per_file")]
    public int ConnectionsPerFile { get; set; } = 8;

    [JsonPropertyName("tmdb_access_token")]
    public string TmdbReadAccessToken { get; set; } = "";

    [JsonPropertyName("tmdb_cache_ttl")]
    public int TmdbCacheTtlSeconds { get; set; } = 86400;

    [JsonPropertyName("skip_existing_episodes")]
    public bool SkipExistingEpisodes { get; set; } = true;
}
