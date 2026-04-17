using System.Collections.Concurrent;
using MediaDebrid_cli.Core;
using MediaDebrid_cli.Models;
using MediaDebrid.Core;
using Spectre.Console;


namespace MediaDebrid_cli.Views;

public class TuiApp
{
    private readonly RealDebridClient _client;
    private readonly Downloader _downloader;
    private readonly MetadataResolver _metadataResolver;

    private ConcurrentDictionary<string, ProgressTask> _progressTasks;

    public TuiApp()
    {
        _client = new RealDebridClient();
        _downloader = new Downloader();
        _downloader.ProgressChanged += OnDownloadProgressChanged;
        _metadataResolver = new MetadataResolver();

        _progressTasks = new ConcurrentDictionary<string, ProgressTask>();
    }

    public static void ShowLogo()
    {
        AnsiConsole.Write(
            new FigletText("MediaDebrid")
                .Color(Color.Green));
    }

    public async Task RunAsync(string magnet, bool showLogo = true, CancellationToken cancellationToken = default)
    {
        if (showLogo)
        {
            ShowLogo();
        }

        string torrentId = string.Empty;
        TorrentInfo? info = null;
        ResolvedMetadata? resolved = null;

        await AnsiConsole.Status()
            .StartAsync("Initializing...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                ctx.SpinnerStyle(Style.Parse("green"));

                try
                {
                    // 1. Resolve metadata from the magnet display name first
                    string? magnetName = MagnetParser.ExtractName(magnet);
                    if (!string.IsNullOrEmpty(magnetName))
                    {
                        ctx.Status("[yellow]Resolving metadata from magnet...[/]");
                        resolved = await _metadataResolver.ResolveAsync(magnetName, cancellationToken: cancellationToken);
                        RenderMetadataPanel(resolved, $"Source (Magnet): {magnetName}");
                    }

                    // 2. Submit or reuse existing torrent on Real-Debrid
                    string? hash = MagnetParser.ExtractHash(magnet);
                    if (!string.IsNullOrEmpty(hash))
                    {
                        ctx.Status($"[yellow]Checking for existing torrent with hash {hash}...[/]");
                        var existingTorrents = await _client.GetTorrentsAsync(cancellationToken: cancellationToken);
                        var matched = existingTorrents.FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));

                        if (matched != null)
                        {
                            torrentId = matched.Id;
                            AnsiConsole.MarkupLine($"[green]✓[/] Found existing torrent. Reusing RD ID: [cyan]{torrentId}[/]");
                        }
                    }

                    if (string.IsNullOrEmpty(torrentId))
                    {
                        ctx.Status("[yellow]Submitting magnet to Real-Debrid...[/]");
                        var addRes = await _client.AddMagnetAsync(magnet, cancellationToken: cancellationToken);
                        torrentId = addRes.Id;
                        AnsiConsole.MarkupLine($"[green]✓[/] Magnet submitted. RD ID: [cyan]{torrentId}[/]");
                    }

                    // 3. Fetch torrent info and fall back to RD filename for metadata if needed
                    ctx.Status("[yellow]Fetching torrent info...[/]");
                    info = await _client.GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);

                    if (resolved == null)
                    {
                        ctx.Status("[yellow]Resolving metadata from Real-Debrid filename...[/]");
                        resolved = await _metadataResolver.ResolveAsync(info.Filename, cancellationToken: cancellationToken);
                        RenderMetadataPanel(resolved, $"Source (RD): {info.Filename}");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[dim]Source (RD): {info.Filename}[/]");
                    }

                    // 4. Wait for Real-Debrid to be ready for file selection
                    ctx.Status("[yellow]Waiting for Real-Debrid status...[/]");
                    while (true)
                    {
                        info = await _client.GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);
                        if (info.Status is "waiting_files_selection" or "downloaded" or "dead") break;
                        await Task.Delay(2000, cancellationToken);
                    }

                    if (info.Status == "dead")
                    {
                        AnsiConsole.MarkupLine("[red]✗[/] Torrent is dead.");
                        return;
                    }

                    // 5. Select relevant files and wait for caching
                    if (info.Status == "waiting_files_selection")
                    {
                        ctx.Status("[yellow]Selecting files...[/]");
                        var fileIds = info.Files
                            .Where(f => f.Bytes > 50_000_000)
                            .Select(f => f.Id.ToString())
                            .ToArray();
                        if (!fileIds.Any()) fileIds = new[] { info.Files.First().Id.ToString() };

                        await _client.SelectFilesAsync(torrentId, string.Join(",", fileIds), cancellationToken: cancellationToken);
                        AnsiConsole.MarkupLine("[green]✓[/] Selected relevant files.");

                        ctx.Status("[yellow]Waiting for Real-Debrid to cache files...[/]");
                        while (true)
                        {
                            info = await _client.GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);
                            if (info.Status == "downloaded") break;
                            await Task.Delay(5000, cancellationToken);
                        }
                    }

                    AnsiConsole.MarkupLine("[green]✓[/] Files are ready and cached!");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                    throw;
                }
            });

        if (info == null || resolved == null || info.Status == "dead") return;

        AnsiConsole.MarkupLine("\n[bold]Starting Downloads...[/]");

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var downloadTasks = info.Links.Select(async link =>
                {
                    try
                    {
                        var unrestricted = await _client.UnrestrictLinkAsync(link, cancellationToken: cancellationToken);
                        string filename = unrestricted.Filename;
                        string destPath = PathGenerator.GetDestinationPath(resolved.Type, resolved.Title, resolved.Year, filename, resolved.Season);

                        var progressTask = ctx.AddTask($"[cyan]{filename}[/]", new ProgressTaskSettings { AutoStart = false, MaxValue = 100 });
                        _progressTasks[filename] = progressTask;
                        progressTask.StartTask();

                        await _downloader.DownloadFileAsync(unrestricted.Download, destPath, cancellationToken);

                        progressTask.Value = progressTask.MaxValue;
                        progressTask.StopTask();
                    }
                    catch (OperationCanceledException)
                    {
                        // Propagate cancellation upwards
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Download failed:[/] {ex.Message}");
                    }
                });

                await Task.WhenAll(downloadTasks);
            });

        if (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("\n[bold green]All downloads completed![/]");
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static void RenderMetadataPanel(ResolvedMetadata meta, string sourceLabel)
    {
        var panel = new Panel(new Grid()
            .AddColumn()
            .AddColumn()
            .AddRow("[bold]Title:[/]", $"[cyan]{meta.Title}[/]")
            .AddRow("[bold]Year:[/]", $"[cyan]{meta.Year}[/]")
            .AddRow("[bold]Type:[/]", $"[cyan]{meta.Type}[/]")
            .AddRow("[bold]Source:[/]", $"[dim]{sourceLabel}[/]"))
        {
            Header = new PanelHeader("Resolved Metadata", Justify.Center),
            Border = BoxBorder.Rounded,
            Expand = true
        };
        AnsiConsole.Write(panel);
    }

    private void OnDownloadProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        if (_progressTasks.TryGetValue(e.Filename, out var task))
        {
            task.MaxValue = e.TotalBytes;
            task.Value = e.BytesDownloaded;
        }
    }
}
