using System.Collections.Concurrent;
using MediaDebrid_cli.Models;
using Spectre.Console;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaDebrid_cli.Services;
using Spectre.Console.Rendering;

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
        AnsiConsole.Write(new FigletText("MediaDebrid").Color(Color.Green));
    }

    public async Task RunAsync(string magnet, string? typeOverride = null, string? titleOverride = null, string? yearOverride = null, int? seasonOverride = null, int? episodeOverride = null, bool showLogo = true, CancellationToken cancellationToken = default)
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

        var torrentId = string.Empty;
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

                void ApplyOverrides(TMDBModels meta) => Utils.ApplyMetadataOverrides(meta, typeOverride, titleOverride, yearOverride, seasonOverride, episodeOverride);

                try
                {
                    var magnetName = MagnetParser.ExtractName(magnet);
                    if (!string.IsNullOrEmpty(magnetName))
                    {
                        ctx.Status("[yellow]Resolving metadata from magnet...[/]");
                        resolved = await _metadataResolver.ResolveAsync(magnetName, typeOverride, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        RenderMetadataPanel(resolved, magnetName);
                    }

                    var hash = MagnetParser.ExtractHash(magnet);
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

                    ctx.Status("[yellow]Fetching torrent info...[/]");
                    info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);

                    if (resolved == null)
                    {
                        ctx.Status("[yellow]Resolving metadata from Real-Debrid filename...[/]");
                        resolved = await _metadataResolver.ResolveAsync(info.Filename, typeOverride, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        RenderMetadataPanel(resolved, info.Filename);
                    }

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

                    HashSet<int>? existingEpisodes = null;
                    if (resolved.Type == "show" && Settings.Instance.SkipExistingEpisodes)
                    {
                        var seasonDir = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                        existingEpisodes = Utils.GetExistingEpisodes(seasonDir);

                        if (existingEpisodes.Any())
                        {
                            if (episodeOverride.HasValue && existingEpisodes.Contains(episodeOverride.Value))
                            {
                                throw new TerminationException($"[red]Episode {episodeOverride.Value} already exists in your local library.[/]");
                            }

                            AnsiConsole.MarkupLine($"[yellow]⚠[/] Found [cyan]{existingEpisodes.Count}[/] existing episodes in local library. They will be skipped.");
                        }
                    }

                    if (info.Status == "waiting_files_selection")
                    {
                        ctx.Status("[yellow]Selecting files...[/]");
                        var fileIds = Utils.GetSelectedFiles(info.Files, episodeOverride, existingEpisodes);
                        if (!fileIds.Any()) fileIds = new[] { info.Files.First().Id.ToString() };

                        if (episodeOverride.HasValue && !info.Files.Any(f => Utils.IsEpisodeMatch(f.Path, episodeOverride.Value)))
                        {
                            AnsiConsole.MarkupLine($"[yellow]⚠[/] No files found matching episode [cyan]{episodeOverride.Value}[/]. Falling back to largest files.");
                        }

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
                    new ProgressBarColumn { Width = 200 },
                    new PercentageColumn(),
                    new DownloadedColumn(),
                    new TransferSpeedColumn(),
                    new EtaTimeColumn())
                .StartAsync(async ctx =>
                {
                    var tasks = info.Links.Select(async link =>
                    {
                        ProgressTask? progressTask = null;
                        try
                        {
                            var unrestricted = await GetClient().UnrestrictLinkAsync(link, cancellationToken: cancellationToken);
                            var filename = unrestricted.Filename;
                            var destPath = PathGenerator.GetDestinationPath(resolved.Type, resolved.Title, resolved.Year, filename, resolved.Season);
                            
                            // Skip if file already exists or episode already exists
                            if (File.Exists(destPath))
                            {
                                AnsiConsole.MarkupLine($"[yellow]⚠[/] Skipping [cyan]{filename}[/] (Already exists locally)");
                                return;
                            }

                            if (resolved.Type == "show" && Settings.Instance.SkipExistingEpisodes)
                            {
                                var ep = Utils.ExtractEpisodeNumber(filename);
                                if (ep.HasValue && existingEpisodes != null && existingEpisodes.Contains(ep.Value))
                                {
                                    AnsiConsole.MarkupLine($"[yellow]⚠[/] Skipping [cyan]{filename}[/] (Episode {ep.Value} already exists)");
                                    return;
                                }
                            }

                            var progressKey = destPath;
                            var tempPath = destPath + ".mdebrid";
                            activePaths.Add(tempPath);

                            var displayFilename = filename.Length > 40 ? filename[..37] + "..." : filename;

                            progressTask = ctx.AddTask($"[cyan]{displayFilename}[/]", new ProgressTaskSettings { AutoStart = false });
                            _progressTasks[progressKey] = progressTask;
                            progressTask.StartTask();

                            await _downloader.DownloadFileAsync(unrestricted.Download, destPath, progressKey, cancellationToken);

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
                        await Task.Delay(200, cancellationToken);
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
            var ex = new TerminationException("[red]Termination requested. Cleaning up...[/]");
            ex.Print();
            try { await Task.WhenAll(allDownloadTasks); } catch { }
            Downloader.CleanupFiles(activePaths);
            throw ex;
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
                        .PromptStyle("green")
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
                var ex = new TerminationException("\n[red]Application terminated. Exiting...[/]");
                ex.Print();
                throw ex;
            }

            if (magnet is null || cancellationToken.IsCancellationRequested) break;

            await RunAsync(magnet, showLogo: false, cancellationToken: cancellationToken);
            break; 
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
            var ex = new TerminationException("\n[red]Setup cancelled. Exiting...[/]");
            ex.Print();
            throw ex;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Settings.Save();
        AnsiConsole.MarkupLine("\n[green]Configuration saved successfully![/]\n");
    }

    public void SetConfigurationValue(string key, string value)
    {
        var (success, message, _) = Utils.UpdateConfiguration(key, value);
        if (success)
        {
            AnsiConsole.MarkupLine($"[green]{message}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]{message}[/]");
            if (message.Contains("not found"))
            {
                AnsiConsole.MarkupLine("Available keys:");
                foreach (var prop in typeof(AppSettings).GetProperties())
                {
                    var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                    var propName = attr != null ? attr.Name : prop.Name;
                    AnsiConsole.MarkupLine($"- [cyan]{propName}[/] ({prop.PropertyType.Name})");
                }
            }
        }
    }

    public void ListConfiguration()
    {
        var json = Utils.GetSettingsJson();
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
        if (_progressTasks.TryGetValue(e.ProgressKey, out var task))
        {
            if (task.MaxValue != e.TotalBytes && e.TotalBytes > 0)
            {
                task.MaxValue = e.TotalBytes;
            }

            task.Value = e.BytesDownloaded;
        }
    }

    private sealed class EtaTimeColumn : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            var remaining = task.RemainingTime;
            if (remaining == null)
            {
                return new Text("--");
            }

            var eta = remaining.Value;
            if (eta.TotalHours >= 1)
            {
                var hours = (int)eta.TotalHours;
                return new Text($"{hours}h:{eta.Minutes:D2}m");
            }

            return new Text($"{(int)eta.TotalMinutes}m:{eta.Seconds:D2}s");
        }
    }
}