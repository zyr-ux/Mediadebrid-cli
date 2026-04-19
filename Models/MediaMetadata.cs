namespace MediaDebrid_cli.Models;

public class MediaMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // movie, show, other
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? Resolution { get; set; }
    public string? Codec { get; set; }
    public string? Quality { get; set; }
}
