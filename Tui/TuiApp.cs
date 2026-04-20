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
        MediaMetadata? resolved = null;
        HashSet<int>? existingEpisodes = null;

        var hash = MagnetParser.ExtractHash(magnet);
        if (string.IsNullOrEmpty(hash))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] [white]Invalid magnet link: Missing BTIH hash (xt=urn:btih:).[/]");
            return;
        }

        TorrentItem? matched = null;
        bool isCached = false;
        bool newlyAdded = false;

        await AnsiConsole.Status()
            .StartAsync("Checking Real-Debrid cache...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                ctx.SpinnerStyle(Style.Parse("yellow"));

                matched = await GetClient().FindTorrentByHashAsync(hash, cancellationToken);
                
                if (matched == null)
                {
                    ctx.Status("[yellow]Adding magnet to check cache status...[/]");
                    var addRes = await GetClient().AddMagnetAsync(magnet, cancellationToken);
                    torrentId = addRes.Id;
                    newlyAdded = true;
                    
                    // Fetch fresh info to get status
                    var info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken);
                    isCached = info.Status == "downloaded" || info.Status == "waiting_files_selection";
                    
                    // Update matched with enough info for the prompt if needed
                    matched = new TorrentItem { Id = torrentId, Status = info.Status, Hash = hash };
                }
                else
                {
                    torrentId = matched.Id;
                    isCached = matched.Status == "downloaded" || matched.Status == "waiting_files_selection";
                }
            });

        if (!isCached)
        {
            string statusMsg = matched?.Status == "downloading" || matched?.Status == "queued" 
                ? $"is currently [bold red]{matched.Status}[/]" 
                : "is [bold red]not cached[/]";
            
            AnsiConsole.MarkupLine($"[red]✗[/] This magnet {statusMsg} on Real-Debrid servers.");
            
            if (!AnsiConsole.Confirm("Do you want Real-Debrid to cache it for you?"))
            {
                if (newlyAdded && !string.IsNullOrEmpty(torrentId))
                {
                    await AnsiConsole.Status().StartAsync("[red]Removing magnet...[/]", async ctx => 
                    {
                        await GetClient().DeleteTorrentAsync(torrentId, cancellationToken);
                    });
                }
                throw new TerminationException("[red]Caching declined by user. Magnet removed from Real-Debrid account.[/]");
            }
        }
        else if (matched != null && !newlyAdded)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Found existing torrent. (Status: [cyan]{matched.Status}[/])");
        }
        else if (newlyAdded)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Magnet added to Real-Debrid. (Status: [cyan]{matched?.Status}[/])");
        }

        await AnsiConsole.Status()
            .StartAsync("Initializing...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                ctx.SpinnerStyle(Style.Parse("green"));

                void ApplyOverrides(MediaMetadata meta) => Utils.ApplyMetadataOverrides(meta, typeOverride, titleOverride, yearOverride, seasonOverride, episodeOverride);

                try
                {
                    var magnetName = MagnetParser.ExtractName(magnet);
                    if (!string.IsNullOrEmpty(magnetName))
                    {
                        ctx.Status("[yellow]Resolving metadata from magnet...[/]");
                        resolved = await _metadataResolver.ResolveAsync(magnetName, typeOverride, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                        RenderMetadataPanel(resolved);
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
                        resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                        RenderMetadataPanel(resolved);
                    }

                    ctx.Status("[yellow]Waiting for Real-Debrid status...[/]");
                    info = await GetClient().WaitForStatusAsync(torrentId, new[] { "waiting_files_selection", "downloaded", "dead" }, cancellationToken);

                    if (info.Status == "dead")
                    {
                        throw new TerminationException("[red]✗[/] Torrent is dead.");
                    }

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
                        if (!fileIds.Any() && info.Files.Any()) fileIds = new[] { info.Files.First().Id.ToString() };
                        
                        if (!fileIds.Any())
                        {
                            throw new TerminationException("[red]✗[/] No files found to download.");
                        }

                        if (episodeOverride.HasValue && !info.Files.Any(f => Utils.IsEpisodeMatch(f.Path, episodeOverride.Value)))
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] No files found matching episode [cyan]{episodeOverride.Value}[/]. Falling back to largest files.");
                        }

                        await GetClient().SelectFilesAsync(torrentId, string.Join(",", fileIds), cancellationToken: cancellationToken);
                        AnsiConsole.MarkupLine("[green]✓[/] Selected relevant files.");
                    }
                }
                catch (TerminationException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error during initialization:[/] [white]{Markup.Escape(ex.Message)}[/]");
                }
            });

        if (info == null || resolved == null) return;

        // Definitive cache check: After selection, status MUST be 'downloaded' if it was cached.
        info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken);
        if (info.Status != "downloaded")
        {
            AnsiConsole.MarkupLine("[red]✗[/] Magnet is [bold red]not cached[/] on Real-Debrid servers.");
            if (!AnsiConsole.Confirm("Do you want to wait for Real-Debrid to cache it?"))
            {
                await AnsiConsole.Status().StartAsync("[red]Removing magnet...[/]", async ctx => 
                {
                    await GetClient().DeleteTorrentAsync(torrentId, cancellationToken);
                });
                throw new TerminationException("[red]Caching declined by user. Magnet removed from Real-Debrid account.[/]");
            }
        }

        if (info.Status != "downloaded")
        {
            await AnsiConsole.Status()
                .StartAsync("Waiting for Real-Debrid to cache files...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    ctx.SpinnerStyle(Style.Parse("yellow"));
                    info = await GetClient().WaitForStatusAsync(torrentId, ["downloaded"], cancellationToken, pollDelayMs: 5000);
                });
        }

        AnsiConsole.MarkupLine("[green]✓[/] Files are ready and cached!");

        AnsiConsole.MarkupLine("\n[bold]Starting Downloads...[/]");

        var activePaths = new ConcurrentBag<string>();
        Task? downloadLoopTask = null;
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
                    downloadLoopTask = Task.Run(async () =>
                    {
                        foreach (var link in info.Links)
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            ProgressTask? progressTask = null;
                            try
                            {
                                var unrestricted = await GetClient().UnrestrictLinkAsync(link, cancellationToken: cancellationToken);
                                var filename = unrestricted.Filename;
                                var destPath = PathGenerator.GetDestinationPath(resolved.Type, resolved.Title, resolved.Year, filename, resolved.Season);

                                // Skip if file already exists or episode already exists
                                if (File.Exists(destPath))
                                {
                                    var skipTask = ctx.AddTask($"[yellow]SKIPPED:[/] [cyan]{Markup.Escape(filename)}[/] (Already exists locally)", new ProgressTaskSettings { AutoStart = false });
                                    skipTask.Increment(100);
                                    skipTask.StopTask();
                                    continue;
                                }

                                if (resolved.Type == "show" && Settings.Instance.SkipExistingEpisodes)
                                {
                                    var ep = Utils.ExtractEpisodeNumber(filename);
                                    if (ep.HasValue && existingEpisodes != null && existingEpisodes.Contains(ep.Value))
                                    {
                                        var skipTask = ctx.AddTask($"[yellow]SKIPPED:[/] [cyan]{Markup.Escape(filename)}[/] (Episode {ep.Value} already exists)", new ProgressTaskSettings { AutoStart = false });
                                        skipTask.Increment(100);
                                        skipTask.StopTask();
                                        continue;
                                    }
                                }

                                var progressKey = destPath;
                                var tempPath = destPath + ".mdebrid";
                                activePaths.Add(tempPath);

                                var displayFilename = filename.Length > 40 ? filename[..37] + "..." : filename;

                                progressTask = ctx.AddTask($"[cyan]{Markup.Escape(displayFilename)}[/]", new ProgressTaskSettings { AutoStart = false });
                                _progressTasks[progressKey] = progressTask;
                                progressTask.StartTask();

                                var rootPath = Settings.GetRootPathForType(resolved.Type);
                                await _downloader.DownloadFileAsync(unrestricted.Download, destPath, rootPath, progressKey, cancellationToken);

                                progressTask.Value = progressTask.MaxValue;
                                progressTask.StopTask();
                            }
                            catch (OperationCanceledException)
                            {
                                progressTask?.StopTask();
                                break;
                            }
                            catch (Exception ex)
                            {
                                progressTask?.StopTask();
                                throw new TerminationException($"[red]Download failed:[/] {Markup.Escape(ex.Message)}");
                            }
                        }
                    }, cancellationToken);

                    while (!downloadLoopTask.IsCompleted)
                    {
                        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                        await Task.Delay(200, cancellationToken);
                    }

                    await downloadLoopTask;
                });

            if (!cancellationToken.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[bold green]All downloads completed![/]");
            }
        }
        catch (OperationCanceledException ex)
        {
            var tex = ex as TerminationException;
            if (tex == null)
            {
                tex = new TerminationException("\n[red]Termination requested. Cleaning up...[/]");
            }
            
            tex.Print();
            
            if (downloadLoopTask != null)
            {
                try { await downloadLoopTask; } catch { }
            }

            var cleanupRoot = resolved != null ? Settings.GetRootPathForType(resolved.Type) : null;
            Downloader.CleanupFiles(activePaths, cleanupRoot);
            throw tex;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]Critical error during download process:[/] {Markup.Escape(ex.Message)}");
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

            if (string.IsNullOrWhiteSpace(Settings.Instance.MediaRoot))
            {
                var defaultPath = Settings.DefaultBaseRoot;
                Settings.Instance.MediaRoot = await CancellablePromptAsync(
                    new TextPrompt<string>("Enter [green]Movies/Shows Root Path[/]:")
                        .DefaultValue(defaultPath)
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
                var metadata = Utils.GetConfigurationMetadata();
                foreach (var (propName, typeName, _) in metadata)
                {
                    AnsiConsole.MarkupLine($"- [cyan]{propName}[/] ({typeName})");
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

    private static void RenderMetadataPanel(MediaMetadata meta)
    {
        var grid = new Grid()
            .AddColumn()
            .AddColumn()
            .AddRow("[bold]Title:[/]", $"[cyan]{Markup.Escape(meta.Title)}[/]");

        if (meta.Type != "other" && !string.IsNullOrWhiteSpace(meta.Year))
        {
            grid.AddRow("[bold]Year:[/]", $"[cyan]{Markup.Escape(meta.Year)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Type))
        {
            grid.AddRow("[bold]Type:[/]", $"[cyan]{char.ToUpper(meta.Type[0]) + meta.Type[1..]}[/]");
        }

        if (meta.Season.HasValue)
        {
            grid.AddRow("[bold]Season:[/]", $"[cyan]{meta.Season.Value}[/]");
        }

        if (meta.Episode.HasValue)
        {
            grid.AddRow("[bold]Episode:[/]", $"[cyan]{meta.Episode.Value}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Version))
        {
            grid.AddRow("[bold]Version:[/]", $"[cyan]{Markup.Escape(meta.Version)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Edition))
        {
            grid.AddRow("[bold]Edition:[/]", $"[yellow]{Markup.Escape(meta.Edition)}[/]");
        }

        if (meta.HasDlc == true)
        {
            grid.AddRow("[bold]Has DLC:[/]", "[green]Yes[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.ReleaseGroup))
        {
            grid.AddRow("[bold]Group:[/]", $"[cyan]{Markup.Escape(meta.ReleaseGroup)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Resolution))
        {
            grid.AddRow("[bold]Res:[/]", $"[cyan]{Markup.Escape(meta.Resolution)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Quality))
        {
            grid.AddRow("[bold]Quality:[/]", $"[cyan]{Markup.Escape(meta.Quality)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Codec))
        {
            grid.AddRow("[bold]Codec:[/]", $"[cyan]{Markup.Escape(meta.Codec)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.InstallerType))
        {
            grid.AddRow("[bold]Installer:[/]", $"[cyan]{Markup.Escape(meta.InstallerType)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Source))
        {
            grid.AddRow("[bold]Source:[/]", $"[dim]{Markup.Escape(meta.Source)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Destination))
        {
            grid.AddRow("[bold]Location:[/]", $"[dim]{Markup.Escape(meta.Destination)}[/]");
        }

        var panel = new Panel(grid)
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