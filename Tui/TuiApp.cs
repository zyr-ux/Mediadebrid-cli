using System.Collections.Concurrent;
using MediaDebrid_cli.Models;
using Spectre.Console;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaDebrid_cli.Services;


namespace MediaDebrid_cli.Tui;

public class TuiApp
{
    private RealDebridClient? _client;
    private readonly Downloader _downloader;
    private readonly MetadataResolver _metadataResolver;

    private readonly ConcurrentDictionary<string, ProgressTask> _progressTasks;

    public TuiApp()
    {
        _downloader = new Downloader();
        _downloader.ProgressChanged += OnDownloadProgressChanged;
        _metadataResolver = new MetadataResolver();

        _progressTasks = new ConcurrentDictionary<string, ProgressTask>();
    }

    private RealDebridClient GetClient() => _client ??= new RealDebridClient();

    public static void ShowLogo()
    {
        AnsiConsole.Write(
            new FigletText("MediaDebrid")
                .Color(Color.Green));
    }

    public async Task RunAsync(string magnet, string? typeOverride = null, string? titleOverride = null, string? yearOverride = null, int? seasonOverride = null, bool showLogo = true, CancellationToken cancellationToken = default)
    {
        if (showLogo)
        {
            ShowLogo();
        }

        _progressTasks.Clear();

        try
        {
            await EnsureConfiguredAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        string torrentId = string.Empty;
        TorrentInfo? info = null;
        TMDBModels? resolved = null;

        if (MagnetParser.ExtractHash(magnet) == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] [white]Invalid magnet link: Missing BTIH hash (xt=urn:btih:).[/]");
            return;
        }

        await AnsiConsole.Status()
            .StartAsync("Initializing...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                ctx.SpinnerStyle(Style.Parse("green"));

                void ApplyOverrides(TMDBModels meta)
                {
                    if (!string.IsNullOrWhiteSpace(titleOverride)) meta.Title = titleOverride.Trim();
                    if (!string.IsNullOrWhiteSpace(typeOverride)) meta.Type = typeOverride.Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(yearOverride)) meta.Year = yearOverride.Trim();
                    if (seasonOverride.HasValue) meta.Season = seasonOverride;
                    if (meta.Season == null && meta.Type == "show") meta.Season = 1;
                }

                try
                {
                    // 1. Resolve metadata from the magnet display name first
                    string? magnetName = MagnetParser.ExtractName(magnet);
                    if (!string.IsNullOrEmpty(magnetName))
                    {
                        ctx.Status("[yellow]Resolving metadata from magnet...[/]");
                        resolved = await _metadataResolver.ResolveAsync(magnetName, typeOverride, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        RenderMetadataPanel(resolved, $"Source (Magnet): {magnetName}");
                    }

                    // 2. Submit or reuse existing torrent on Real-Debrid
                    string? hash = MagnetParser.ExtractHash(magnet);
                    if (!string.IsNullOrEmpty(hash))
                    {
                        ctx.Status($"[yellow]Checking for existing torrent with hash {hash}...[/]");
                        var existingTorrents = await GetClient().GetTorrentsAsync(cancellationToken: cancellationToken);
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
                        var addRes = await GetClient().AddMagnetAsync(magnet, cancellationToken: cancellationToken);
                        torrentId = addRes.Id;
                        AnsiConsole.MarkupLine($"[green]✓[/] Magnet submitted. RD ID: [cyan]{torrentId}[/]");
                    }

                    // 3. Fetch torrent info and fall back to RD filename for metadata if needed
                    ctx.Status("[yellow]Fetching torrent info...[/]");
                    info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);

                    if (resolved == null)
                    {
                        ctx.Status("[yellow]Resolving metadata from Real-Debrid filename...[/]");
                        resolved = await _metadataResolver.ResolveAsync(info.Filename, typeOverride, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        RenderMetadataPanel(resolved, $"Source (RD): {info.Filename}");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[dim]Source (RD): {info.Filename}[/]");
                    }

                    // 4. Wait for Real-Debrid to be ready for file selection
                    ctx.Status("[yellow]Waiting for Real-Debrid status...[/]");
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);
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

                        await GetClient().SelectFilesAsync(torrentId, string.Join(",", fileIds), cancellationToken: cancellationToken);
                        AnsiConsole.MarkupLine("[green]✓[/] Selected relevant files.");

                        ctx.Status("[yellow]Waiting for Real-Debrid to cache files...[/]");
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);
                            if (info.Status == "downloaded") break;
                            await Task.Delay(5000, cancellationToken);
                        }
                    }

                    AnsiConsole.MarkupLine("[green]✓[/] Files are ready and cached!");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error during initialization:[/] [white]{ex.Message}[/]");
                }
            });

        if (info == null || resolved == null || info.Status == "dead") return;

        AnsiConsole.MarkupLine("\n[bold]Starting Downloads...[/]");

        var activePaths = new ConcurrentBag<string>();
        var allDownloadTasks = new List<Task>();
        try
        {
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
                    var tasks = info.Links.Select(async link =>
                    {
                        ProgressTask? progressTask = null;
                        try
                        {
                            var unrestricted = await GetClient().UnrestrictLinkAsync(link, cancellationToken: cancellationToken);
                            string filename = unrestricted.Filename;
                            string destPath = PathGenerator.GetDestinationPath(resolved.Type, resolved.Title, resolved.Year, filename, resolved.Season);
                            string tempPath = destPath + ".mdebrid";
                            activePaths.Add(tempPath);

                            progressTask = ctx.AddTask($"[cyan]{filename}[/]", new ProgressTaskSettings { AutoStart = false, MaxValue = 100 });
                            _progressTasks[filename] = progressTask;
                            progressTask.StartTask();

                            await _downloader.DownloadFileAsync(unrestricted.Download, destPath, cancellationToken);

                            progressTask.Value = progressTask.MaxValue;
                            progressTask.StopTask();
                        }
                        catch (OperationCanceledException)
                        {
                            progressTask?.StopTask();
                            throw;
                        }
                        catch (Exception ex)
                        {
                            progressTask?.StopTask();
                            AnsiConsole.MarkupLine($"[red]Download failed:[/] {ex.Message}");
                        }
                    }).ToList();

                    allDownloadTasks.AddRange(tasks);

                    var whenAllTask = Task.WhenAll(tasks);
                    while (!whenAllTask.IsCompleted)
                    {
                        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                        await Task.Delay(200);
                    }
                    await whenAllTask;
                });

            if (!cancellationToken.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[bold green]All downloads completed![/]");
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[red]Termination requested. Cleaning up...[/]");
            // Wait for background tasks to finish their cancellation and release file handles
            try { await Task.WhenAll(allDownloadTasks); } catch { /* Ignore cancellation errors */ }
            Downloader.CleanupFiles(activePaths);
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]Critical error during download process:[/] {ex.Message}");
        }
    }

    public async Task RunInteractiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConfiguredAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ShowLogo();

        while (!cancellationToken.IsCancellationRequested)
        {
            string? magnet;
            try
            {
                magnet = await CancellablePromptAsync(
                    new TextPrompt<string>("Enter [green]Magnet Link[/]:")
                        .PromptStyle("white")
                        .Validate(k =>
                        {
                            if (string.IsNullOrWhiteSpace(k)) return ValidationResult.Error("[red]Magnet link cannot be empty.[/]");
                            if (!k.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return ValidationResult.Error("[red]Invalid magnet link format.[/]");
                            if (MagnetParser.ExtractHash(k) == null) return ValidationResult.Error("[red]Invalid magnet link: Missing BTIH hash (xt=urn:btih:).[/]");
                            return ValidationResult.Success();
                        }),
                    cancellationToken
                );
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[red]Application terminated. Exiting...[/]");
                break;
            }

            if (magnet is null || cancellationToken.IsCancellationRequested) break;

            await RunAsync(magnet, showLogo: false, cancellationToken: cancellationToken);
            break; // Exit after one run in this version, or remove break to loop
        }
    }

    public async Task EnsureConfiguredAsync(CancellationToken cancellationToken)
    {
        if (Settings.IsConfigured()) return;

        cancellationToken.ThrowIfCancellationRequested();

        ShowLogo();
        AnsiConsole.MarkupLine("\n[yellow]Initial Setup Required[/]");
        AnsiConsole.MarkupLine("Please provide the following required configuration values:\n");

        try
        {
            if (string.IsNullOrWhiteSpace(Settings.Instance.RealDebridApiToken))
            {
                Settings.Instance.RealDebridApiToken = await CancellablePromptAsync(
                    new TextPrompt<string>("Enter [green]Real-Debrid API Key[/]:")
                        .PromptStyle("white")
                        .Secret()
                        .Validate(k => string.IsNullOrWhiteSpace(k) ? ValidationResult.Error("[red]Key cannot be empty.[/]") : ValidationResult.Success()),
                    cancellationToken
                );
            }

            if (string.IsNullOrWhiteSpace(Settings.Instance.TmdbReadAccessToken))
            {
                Settings.Instance.TmdbReadAccessToken = await CancellablePromptAsync(
                    new TextPrompt<string>("Enter [green]TMDB Read Access Token[/]:")
                        .PromptStyle("white")
                        .Secret()
                        .Validate(k => string.IsNullOrWhiteSpace(k) ? ValidationResult.Error("[red]Token cannot be empty.[/]") : ValidationResult.Success()),
                    cancellationToken
                );
            }

            if (Settings.Instance.MediaRoot == "./media" || string.IsNullOrWhiteSpace(Settings.Instance.MediaRoot))
            {
                Settings.Instance.MediaRoot = await CancellablePromptAsync(
                    new TextPrompt<string>("Enter [green]Media Root Path[/]:")
                        .DefaultValue("./media")
                        .PromptStyle("white"),
                    cancellationToken
                );
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[red]Setup cancelled. Exiting...[/]");
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Settings.Save();
        AnsiConsole.MarkupLine("\n[green]Configuration saved successfully![/]\n");
    }

    public void SetConfigurationValue(string key, string value)
    {
        var properties = typeof(AppSettings).GetProperties();
        foreach (var prop in properties)
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var propName = attr != null ? attr.Name : prop.Name;

            if (propName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    object convertedValue = Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(Settings.Instance, convertedValue);
                    Settings.Save();
                    AnsiConsole.MarkupLine($"[green]Successfully updated '{key}' to '{value}'[/]");
                    return;
                }
                catch (Exception)
                {
                    AnsiConsole.MarkupLine($"[red]Failed to convert '{value}' to type {prop.PropertyType.Name} for key '{key}'[/]");
                    return;
                }
            }
        }

        AnsiConsole.MarkupLine($"[red]Configuration key '{key}' not found.[/]");
        AnsiConsole.MarkupLine("Available keys:");
        foreach (var prop in properties)
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var propName = attr != null ? attr.Name : prop.Name;
            AnsiConsole.MarkupLine($"- [cyan]{propName}[/] ({prop.PropertyType.Name})");
        }
    }

    public void ListConfiguration()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(Settings.Instance, options);
        AnsiConsole.MarkupLine("[cyan]Current Configuration:[/]");
        Console.WriteLine(json);
    }

    private async Task<T> CancellablePromptAsync<T>(IPrompt<T> prompt, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>();
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        _ = Task.Run(() =>
        {
            try
            {
                var result = AnsiConsole.Prompt(prompt);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, cancellationToken);

        return await tcs.Task;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static void RenderMetadataPanel(TMDBModels meta, string sourceLabel)
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

    private void OnDownloadProgressChanged(object? sender, DownloadProgressModel e)
    {
        if (_progressTasks.TryGetValue(e.Filename, out var task))
        {
            task.MaxValue = e.TotalBytes;
            task.Value = e.BytesDownloaded;
        }
    }
}
