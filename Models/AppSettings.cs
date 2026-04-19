using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MediaDebrid_cli.Models;

public class AppSettings
{
    [JsonPropertyName("real_debrid_api_key")]
    [Description("Required. Your Real-Debrid API token")]
    public string RealDebridApiToken { get; set; } = "";

    [JsonPropertyName("media_root")]
    [Description("Download path for Movies & Shows (default: Downloads/MediaDebrid)")]
    public string MediaRoot { get; set; } = "";

    [JsonPropertyName("games_root")]
    [Description("Download path for Games (default: Downloads/MediaDebrid)")]
    public string GamesRoot { get; set; } = "";

    [JsonPropertyName("others_root")]
    [Description("Download path for miscellaneous files (default: Downloads/MediaDebrid)")]
    public string OthersRoot { get; set; } = "";

    [JsonPropertyName("parallel_download")]
    [Description("Enable chunked downloads (default: true)")]
    public bool ParallelDownloadEnabled { get; set; } = true;

    [JsonPropertyName("connections_per_file")]
    [Description("Parallel connections per file (default: 8)")]
    public int ConnectionsPerFile { get; set; } = 8;

    [JsonPropertyName("skip_existing_episodes")]
    [Description("Skip already downloaded episodes (default: true)")]
    public bool SkipExistingEpisodes { get; set; } = true;
}
