namespace MediaDebrid_cli.Models;

public class ResumeMetadata
{
    public string MagnetUri { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public List<SegmentProgress> Segments { get; set; } = new();
    public string? SeasonOverride { get; set; }
    public string? EpisodeOverride { get; set; }
}

public class SegmentProgress
{
    [System.Text.Json.Serialization.JsonInclude]
    public long Start;
    [System.Text.Json.Serialization.JsonInclude]
    public long End;
    [System.Text.Json.Serialization.JsonInclude]
    public long Current;
}
