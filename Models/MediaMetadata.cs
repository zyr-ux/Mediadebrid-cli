namespace MediaDebrid_cli.Models;

public class MediaMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Year { get; set; }
    public string? Type { get; set; } // movie, show, other, game
    public string? Season { get; set; }
    public string? Episode { get; set; }
    public string? Version { get; set; }
    public string? Resolution { get; set; }
    public string? Codec { get; set; }
    public string? Quality { get; set; }
    public string? Destination { get; set; }
    public string? Edition { get; set; }
    public string? ReleaseGroup { get; set; }
    public string? InstallerType { get; set; }
    public bool? HasDlc { get; set; }
}
