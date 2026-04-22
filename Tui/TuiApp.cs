using System.Collections.Concurrent;
using MediaDebrid_cli.Models;
using Spectre.Console;
using MediaDebrid_cli.Services;
using Spectre.Console.Rendering;

namespace MediaDebrid_cli.Tui;

public class TuiApp
{
    private RealDebridClient? _client;
    private readonly Downloader _downloader;
    private readonly MetadataResolver _metadataResolver;

    private readonly ConcurrentDictionary<string, ProgressTask> _progressTasks;
    private readonly ConcurrentDictionary<int, double> _taskSpeeds;
    private readonly ConcurrentDictionary<int, TaskDisplayStatus> _taskDisplayStatuses;
    private readonly ConcurrentDictionary<int, int> _frozenFrames;
    private readonly ConcurrentDictionary<int, string> _taskEpisodeTexts;

    private static readonly Spinner AppSpinner = Spinner.Known.Arc;

    private enum TaskDisplayStatus { Finished, Saved, Cancelled }

    public TuiApp()
    {
        _downloader = new Downloader();
        _downloader.ProgressChanged += OnDownloadProgressChanged;
        _downloader.OnPauseChanged += OnPauseChanged;
        _metadataResolver = new MetadataResolver();

        _progressTasks = new ConcurrentDictionary<string, ProgressTask>();
        _taskSpeeds = new ConcurrentDictionary<int, double>();
        _taskDisplayStatuses = new ConcurrentDictionary<int, TaskDisplayStatus>();
        _frozenFrames = new ConcurrentDictionary<int, int>();
        _taskEpisodeTexts = new ConcurrentDictionary<int, string>();
    }

    private RealDebridClient GetClient() => _client ??= new RealDebridClient();

    public static void ShowLogo()
    {
        AnsiConsole.Write(new FigletText("MediaDebrid").Color(Color.Green));
    }

    public async Task RunAsync(string magnet, int? seasonOverride = null, int? episodeOverride = null, bool showLogo = true, CancellationToken cancellationToken = default, bool forceResume = false)
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
            throw new MagnetException("Invalid magnet link: Missing BTIH hash (xt=urn:btih:).");
        }

        TorrentItem? matched = null;
        bool isCached = false;
        bool newlyAdded = false;

        AnsiConsole.WriteLine();
        try
        {
            await AnsiConsole.Status()
                .StartAsync("Checking Real-Debrid cache...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Arc);
                    ctx.SpinnerStyle(Style.Parse("yellow"));

                    matched = await GetClient().FindTorrentByHashAsync(hash, cancellationToken);
                    
                    if (matched == null)
                    {
                        ctx.Status("[yellow]Adding magnet to check cache status...[/]");
                        var addRes = await GetClient().AddMagnetAsync(magnet, cancellationToken);
                        torrentId = addRes.Id;
                        newlyAdded = true;
                        
                        // Fetch fresh info to get status
                        var cacheInfo = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken);
                        isCached = cacheInfo.Status == "downloaded" || cacheInfo.Status == "waiting_files_selection";
                        
                        // Update matched with enough info for the prompt if needed
                        matched = new TorrentItem { Id = torrentId, Status = cacheInfo.Status, Hash = hash };
                    }
                    else
                    {
                        torrentId = matched.Id;
                        isCached = matched.Status == "downloaded" || matched.Status == "waiting_files_selection";
                    }
                });
        }
        catch (RealDebridApiException) { throw; }
        catch (HttpRequestException ex)
        {
            throw new TerminationException($"[bold red]X[/] Network error during cache check: [white]{Markup.Escape(ex.Message)}[/]");
        }

        if (!isCached)
        {
            string statusMsg = matched?.Status == "downloading" || matched?.Status == "queued" 
                ? $"is currently [bold red]{matched.Status}[/]" 
                : "is [bold red]Not Cached[/]";
            
            AnsiConsole.MarkupLine($"[bold red]X[/] This magnet {statusMsg} on Real-Debrid servers.");
            
            if (!AnsiConsole.Confirm("Do you want Real-Debrid to cache it for you?"))
            {
                if (newlyAdded && !string.IsNullOrEmpty(torrentId))
                {
                    await AnsiConsole.Status().StartAsync("[red]Removing magnet...[/]", async _ => 
                    {
                        await GetClient().DeleteTorrentAsync(torrentId, cancellationToken);
                    });
                }
                throw new TerminationException("[red]Caching declined by user. Magnet removed from Real-Debrid account.[/]");
            }
        }
        else if (matched != null && !newlyAdded)
        {
            AnsiConsole.MarkupLine($"[bold green]✓[/] Found existing torrent. (Status: [cyan]Cached[/])");
        }
        else if (newlyAdded)
        {
            AnsiConsole.MarkupLine($"[bold green]✓[/] Magnet added to Real-Debrid. (Status: [cyan]Cached[/])");
        }

        AnsiConsole.WriteLine();
        await AnsiConsole.Status()
            .StartAsync("Initializing...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                ctx.SpinnerStyle(Style.Parse("green"));

                void ApplyOverrides(MediaMetadata meta) => Utils.ApplyMetadataOverrides(meta, seasonOverride, episodeOverride);

                try
                {
                    var magnetName = MagnetParser.ExtractName(magnet);
                    if (!string.IsNullOrEmpty(magnetName))
                    {
                        ctx.Status("[yellow]Resolving metadata from magnet...[/]");
                        resolved = await _metadataResolver.ResolveAsync(magnetName, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                        RenderMetadataPanel(resolved);
                    }

                    if (string.IsNullOrEmpty(torrentId))
                    {
                        ctx.Status("[yellow]Submitting magnet to Real-Debrid...[/]");
                        var addRes = await GetClient().AddMagnetAsync(magnet, cancellationToken: cancellationToken);
                        torrentId = addRes.Id;
                        AnsiConsole.MarkupLine($"[bold green]✓[/] Magnet submitted. RD ID: [cyan]{torrentId}[/]");
                    }

                    ctx.Status("[yellow]Fetching torrent info...[/]");
                    info = await GetClient().GetTorrentInfoAsync(torrentId, cancellationToken: cancellationToken);

                    if (resolved == null)
                    {
                        ctx.Status("[yellow]Resolving metadata from Real-Debrid filename...[/]");
                        resolved = await _metadataResolver.ResolveAsync(info.Filename, cancellationToken: cancellationToken);
                        ApplyOverrides(resolved);
                        resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                        RenderMetadataPanel(resolved);
                    }

                    ctx.Status("[yellow]Waiting for Real-Debrid status...[/]");
                    info = await GetClient().WaitForStatusAsync(torrentId, new[] { "waiting_files_selection", "downloaded", "dead" }, cancellationToken);

                }
                catch (TerminationException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (RealDebridApiException) { throw; }
                catch (HttpRequestException ex)
                {
                    throw new TerminationException($"[red]X[/] Network error during initialization: [white]{Markup.Escape(ex.Message)}[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error during initialization:[/] [white]{Markup.Escape(ex.Message)}[/]");
                }
            });

        if (info == null || resolved == null) return;

        if (info.Status == "dead")
        {
            throw new TerminationException("[bold red]X[/] Torrent is dead.");
        }

        // Interactive season selection for multi-season shows
        if (resolved.Type == "show" && !seasonOverride.HasValue)
        {
            var seasonsInTorrent = info.Files
                .Select(f => Utils.ExtractSeasonNumber(f.Path))
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            if (seasonsInTorrent.Count > 1)
            {
                try
                {
                    string? input = null;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        input = await ReadLineWithEffectAsync($"[yellow]Multiple seasons detected ({string.Join(", ", seasonsInTorrent.Select(s => $"S{s:D2}"))}).[/]\nEnter [green]season number[/] to download (leave empty for all)", cancellationToken);
                        
                        if (cancellationToken.IsCancellationRequested) break;
                        if (string.IsNullOrWhiteSpace(input)) break;

                        if (!int.TryParse(input, out var sNum) || sNum <= 0)
                        {
                            AnsiConsole.MarkupLine("[red]Please enter a valid season number.[/]");
                            continue;
                        }
                        if (!seasonsInTorrent.Contains(sNum))
                        {
                            AnsiConsole.MarkupLine($"[red]Season {sNum} not found in this torrent.[/]");
                            continue;
                        }
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(input) && int.TryParse(input, out var chosenSeason))
                    {
                        seasonOverride = chosenSeason;
                        resolved.Season = chosenSeason;
                        resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                        AnsiConsole.MarkupLine($"[bold green]✓[/] Selected season [cyan]S{chosenSeason:D2}[/].");
                        RenderMetadataPanel(resolved);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw new TerminationException("[red]Application terminated. Exiting...[/]");
                }
            }
        }

        // Interactive episode selection for shows
        if (resolved.Type == "show" && !episodeOverride.HasValue)
        {
            try
            {
                string? input = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    input = await ReadLineWithEffectAsync("Enter [green]episode number[/] to download (leave empty for all)", cancellationToken);
                    
                    if (cancellationToken.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(input)) break;

                    if (!int.TryParse(input, out var epNum) || epNum <= 0)
                    {
                        AnsiConsole.MarkupLine("[red]Please enter a valid episode number.[/]");
                        continue;
                    }
                    if (!info.Files.Any(f => Utils.IsEpisodeMatch(f.Path, epNum, seasonOverride)))
                    {
                        var scope = seasonOverride.HasValue ? $"in season {seasonOverride}" : "in this torrent";
                        AnsiConsole.MarkupLine($"[red]Episode {epNum} not found {scope}.[/]");
                        continue;
                    }
                    break;
                }

                if (!string.IsNullOrWhiteSpace(input) && int.TryParse(input, out var chosenEp))
                {
                    episodeOverride = chosenEp;
                    resolved.Episode = chosenEp;
                    AnsiConsole.MarkupLine($"[bold green]✓[/] Selected episode [cyan]{chosenEp}[/].");
                }
            }
            catch (OperationCanceledException)
            {
                throw new TerminationException("[red]Application terminated. Exiting...[/]");
            }
        }

        await AnsiConsole.Status()
            .StartAsync("Preparing selection...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                ctx.SpinnerStyle(Style.Parse("green"));

                try
                {
                    if (resolved.Type == "show" && Settings.Instance.SkipExistingEpisodes)
                    {
                        var seasonDir = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, resolved.Season);
                        existingEpisodes = Utils.GetExistingEpisodes(seasonDir);

                        if (existingEpisodes.Any())
                        {
                            if (episodeOverride.HasValue && existingEpisodes.Contains(episodeOverride.Value))
                            {
                                throw new TerminationException($"[bold red]Episode {episodeOverride.Value} already exists in your local library.[/]");
                            }

                            AnsiConsole.MarkupLine($"[yellow]X[/] Found [cyan]{existingEpisodes.Count}[/] existing episodes in local library. They will be skipped.");
                            AnsiConsole.WriteLine();
                        }
                    }

                    if (info.Status == "waiting_files_selection")
                    {
                        ctx.Status("[yellow]Selecting files...[/]");
                        var fileIds = Utils.GetSelectedFiles(info.Files, seasonOverride, episodeOverride, existingEpisodes);
                        if (!fileIds.Any() && info.Files.Any()) fileIds = new[] { info.Files.First().Id.ToString() };
                        
                        if (!fileIds.Any())
                        {
                            throw new TerminationException("[bold red]X[/] No files found to download.");
                        }

                        if (episodeOverride.HasValue && !info.Files.Any(f => Utils.IsEpisodeMatch(f.Path, episodeOverride.Value, seasonOverride)))
                        {
                            AnsiConsole.MarkupLine($"[bold red]X[/] No files found matching episode [cyan]{episodeOverride.Value}[/] in selected season. Falling back to largest files.");
                        }

                        await GetClient().SelectFilesAsync(torrentId, string.Join(",", fileIds), cancellationToken: cancellationToken);
                        AnsiConsole.MarkupLine("[bold green]✓[/] Selected relevant files.");
                    }
                }
                catch (TerminationException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (RealDebridApiException) { throw; }
                catch (HttpRequestException ex)
                {
                    throw new TerminationException($"[red]X[/] Network error during initialization: [white]{Markup.Escape(ex.Message)}[/]");
                }
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
            AnsiConsole.MarkupLine("[bold red]X[/] Magnet is [bold red]not cached[/] on Real-Debrid servers.");
            if (!AnsiConsole.Confirm("Do you want to wait for Real-Debrid to cache it?"))
            {
                await AnsiConsole.Status().StartAsync("[red]Removing magnet...[/]", async _ => 
                {
                    await GetClient().DeleteTorrentAsync(torrentId, cancellationToken);
                });
                throw new TerminationException("[red]Caching declined by user. Magnet removed from Real-Debrid account.[/]");
            }
            AnsiConsole.WriteLine();
        }

        if (info.Status != "downloaded")
        {
            await AnsiConsole.Status()
                .StartAsync("Waiting for Real-Debrid to cache files...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Arc);
                    ctx.SpinnerStyle(Style.Parse("yellow"));
                    info = await GetClient().WaitForStatusAsync(torrentId, ["downloaded"], cancellationToken, pollDelayMs: 5000);
                });
        }

        AnsiConsole.MarkupLine("[bold green]✓[/] Files are ready and cached!");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Starting Downloads...[/]");
        AnsiConsole.MarkupLine("[dim]Controls: [yellow]P[/] Pause | [green]X[/] Save & Exit | [red]Ctrl+C[/] Cancel & Delete[/]");
        AnsiConsole.WriteLine();


        var activePaths = new ConcurrentBag<string>();
        Task? downloadLoopTask = null;
        bool shouldDeletePartial = true;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var queuedDownloads = new List<(UnrestrictResponse Unrestricted, ResumeMetadata? ResumeData, string DestPath)>();

        await AnsiConsole.Status().StartAsync("[yellow]Preparing downloads...[/]", async ctx =>
        {
            ctx.Spinner(Spinner.Known.Arc);
            foreach (var link in info.Links)
            {
                if (linkedCts.Token.IsCancellationRequested) break;
                
                var unrestricted = await GetClient().UnrestrictLinkAsync(link, cancellationToken: linkedCts.Token);
                var filename = unrestricted.Filename;
                var destPath = PathGenerator.GetDestinationPath(resolved.Type, resolved.Title, resolved.Year, filename, resolved.Season);

                // Skip if file already exists locally
                if (File.Exists(destPath)) continue;

                // Skip if it's an existing episode (for shows)
                if (resolved.Type == "show" && Settings.Instance.SkipExistingEpisodes)
                {
                    var ep = Utils.ExtractEpisodeNumber(filename);
                    if (ep.HasValue && existingEpisodes != null && existingEpisodes.Contains(ep.Value)) continue;
                }

                var tempPath = destPath + ".mdebrid";

                // Resume detection
                ResumeMetadata? resumeData = null;
                if (File.Exists(tempPath))
                {
                    resumeData = _downloader.ReadResumeMetadata(tempPath);
                    if (resumeData != null && resumeData.MagnetUri == magnet)
                    {
                        // We'll ask for confirmation outside the Status block to avoid UI conflicts
                    }
                    else
                    {
                        resumeData = null;
                    }
                }
                
                queuedDownloads.Add((unrestricted, resumeData, destPath));
            }
        });

        // Now ask for confirmations for any detected resumes
        bool showedResumes = false;
        for (int i = 0; i < queuedDownloads.Count; i++)
        {
            var item = queuedDownloads[i];
            if (item.ResumeData != null)
            {
                showedResumes = true;
                if (forceResume || AnsiConsole.Confirm($"[yellow]Partial download found for {Markup.Escape(item.Unrestricted.Filename)} ({Utils.FormatBytes(item.ResumeData.Segments.Sum(s => s.Current - s.Start))} / {Utils.FormatBytes(item.ResumeData.TotalSize)}). Resume?[/]"))
                {
                    // Keep it
                }
                else
                {
                    File.Delete(item.DestPath + ".mdebrid");
                    queuedDownloads[i] = (item.Unrestricted, null, item.DestPath);
                }
            }
        }
        if (showedResumes) AnsiConsole.WriteLine();

        try
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new SpinnerColumn(this, _downloader),
                    new EpisodeColumn(_taskEpisodeTexts),
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn { Width = 200 },
                    new PercentageColumn(),
                    new CustomDownloadedColumn(),
                    new CustomTransferSpeedColumn(_taskSpeeds),
                    new CustomEtaColumn(_taskSpeeds))
                .StartAsync(async ctx =>
                {

                    downloadLoopTask = Task.Run(async () =>
                    {
                        foreach (var item in queuedDownloads)
                        {
                            if (linkedCts.Token.IsCancellationRequested) break;

                            ProgressTask? progressTask = null;
                            try
                            {
                                var unrestricted = item.Unrestricted;
                                var filename = unrestricted.Filename;
                                var destPath = item.DestPath;
                                var tempPath = destPath + ".mdebrid";
                                var resumeData = item.ResumeData;

                                // Final safety check: Skip if file already exists
                                if (File.Exists(destPath))
                                {
                                    continue;
                                }

                                if (resolved.Type == "show" && Settings.Instance.SkipExistingEpisodes)
                                {
                                    var ep = Utils.ExtractEpisodeNumber(filename);
                                    if (ep.HasValue && existingEpisodes != null && existingEpisodes.Contains(ep.Value))
                                    {
                                        continue;
                                    }
                                }

                                var progressKey = destPath;
                                activePaths.Add(tempPath);

                                var displayFilename = filename.Length > 40 ? filename[..37] + "..." : filename;

                                progressTask = ctx.AddTask($"[cyan]{Markup.Escape(displayFilename)}[/]", new ProgressTaskSettings { AutoStart = false });
                                _progressTasks[progressKey] = progressTask;

                                if (resolved.Type == "show")
                                {
                                    var epNum = Utils.ExtractEpisodeNumber(filename);
                                    if (epNum.HasValue)
                                    {
                                        var season = resolved.Season ?? 1;
                                        _taskEpisodeTexts[progressTask.Id] = $"S{season:D2}E{epNum.Value:D2}";
                                    }
                                }

                                progressTask.StartTask();

                                var rootPath = Settings.GetRootPathForType(resolved.Type);
                                
                                if (resumeData == null)
                                {
                                    resumeData = new ResumeMetadata
                                    {
                                        MagnetUri = magnet,
                                        FileId = unrestricted.Id,
                                        TotalSize = 0, // Will be set by downloader
                                        SeasonOverride = seasonOverride,
                                        EpisodeOverride = episodeOverride
                                    };
                                }
                                else
                                {
                                    // Initialize task with existing progress
                                    if (resumeData.TotalSize > 0)
                                    {
                                        progressTask.MaxValue = resumeData.TotalSize;
                                        progressTask.Value = resumeData.Segments.Sum(s => s.Current - s.Start);
                                    }
                                }

                                await _downloader.DownloadFileAsync(unrestricted.Download, destPath, rootPath, progressKey, linkedCts.Token, resumeData);

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
                                shouldDeletePartial = false;
                                if (progressTask != null)
                                {
                                    _taskDisplayStatuses[progressTask.Id] = TaskDisplayStatus.Cancelled;
                                    progressTask.StopTask();
                                }
                                
                                // Mark all other active tasks as cancelled to ensure UI correctly reflects failure
                                foreach (var t in _progressTasks.Values)
                                {
                                    if (!t.IsFinished && !_taskDisplayStatuses.ContainsKey(t.Id))
                                    {
                                        _taskDisplayStatuses[t.Id] = TaskDisplayStatus.Cancelled;
                                    }
                                }

                                throw new DownloadException($"Download failed: {ex.Message}", ex);
                            }
                        }
                    }, cancellationToken);

                    while (!downloadLoopTask.IsCompleted)
                    {
                        ctx.Refresh();

                        if (linkedCts.Token.IsCancellationRequested) break;
                        
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(true);
                            if (key.Key == ConsoleKey.P)
                            {
                                _downloader.TogglePause();
                            }
                            else if (key.Key == ConsoleKey.X)
                            {
                                shouldDeletePartial = false;
                                var now = Environment.TickCount64;
                                var interval = (long)AppSpinner.Interval.TotalMilliseconds;
                                var count = AppSpinner.Frames.Count;
                                var frameIdx = (int)((now / interval) % count);

                                foreach (var t in _progressTasks.Values)
                                {
                                    if (t.IsFinished) continue;
                                    _taskDisplayStatuses[t.Id] = TaskDisplayStatus.Saved;
                                    _frozenFrames[t.Id] = frameIdx;
                                }
                                linkedCts.Cancel();
                                break;
                            }
                        }
                        
                        try
                        {
                            await Task.Delay(100, linkedCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            foreach (var t in _progressTasks.Values)
                            {
                                if (!t.IsFinished && !_taskDisplayStatuses.ContainsKey(t.Id))
                                {
                                    _taskDisplayStatuses[t.Id] = TaskDisplayStatus.Cancelled;
                                }
                            }
                            break;
                        }
                    }
                    
                    if (downloadLoopTask != null) await downloadLoopTask;

                });

            if (!linkedCts.IsCancellationRequested)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold green]All downloads completed![/]");
                shouldDeletePartial = false;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (MagnetException) { throw; }
        catch (ConfigurationException) { throw; }
        catch (DownloadException) { throw; }
        catch (RealDebridClientException) { throw; }
        catch (RealDebridApiException) { throw; }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
        finally
        {
            if (downloadLoopTask != null && !downloadLoopTask.IsCompleted)
            {
                try { await downloadLoopTask; }
                catch (Exception)
                {
                    // Suppress background shutdown errors to avoid masking the primary flow outcome.
                }
            }

            if (shouldDeletePartial)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[red]Download cancelled. Cleaning up partial files...[/]");
                var cleanupRoot = resolved != null ? Settings.GetRootPathForType(resolved.Type) : null;
                Downloader.CleanupFiles(activePaths, cleanupRoot, force: true);
            }
            else if (linkedCts.IsCancellationRequested)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Stopping... Partial progress preserved for resume.[/]");
                var cleanupRoot = resolved != null ? Settings.GetRootPathForType(resolved.Type) : null;
                Downloader.CleanupFiles(activePaths, cleanupRoot, force: false);
                throw new TerminationException("");
            }
            else
            {
                var cleanupRoot = resolved != null ? Settings.GetRootPathForType(resolved.Type) : null;
                Downloader.CleanupFiles(activePaths, cleanupRoot, force: false);
            }
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
            string? magnet = null;
            
            while (!cancellationToken.IsCancellationRequested)
            {
                magnet = await ReadLineWithEffectAsync("Enter [green]Magnet Link[/]: ", cancellationToken, ConsoleColor.Green);

                if (cancellationToken.IsCancellationRequested) break;

                // Validation logic (mirrors the previous TextPrompt validators)
                if (string.IsNullOrWhiteSpace(magnet))
                {
                    AnsiConsole.MarkupLine("[red]Magnet link cannot be empty.[/]");
                    continue;
                }

                if (!magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine("[red]Invalid magnet link format.[/]");
                    continue;
                }

                if (MagnetParser.ExtractHash(magnet) == null)
                {
                    AnsiConsole.MarkupLine("[red]Invalid magnet link: Missing BTIH hash (xt=urn:btih:).[/]");
                    continue;
                }

                break;
            }

            if (magnet is null || cancellationToken.IsCancellationRequested) break;

            await RunAsync(magnet, showLogo: false, cancellationToken: cancellationToken);
            break;
        }
    }

    public async Task RunResumeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File [cyan]{path}[/] not found.");
            return;
        }

        var metadata = _downloader.ReadResumeMetadata(path);
        if (metadata == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not read resume metadata from [cyan]{path}[/].");
            return;
        }

        await RunAsync(metadata.MagnetUri, metadata.SeasonOverride, metadata.EpisodeOverride, showLogo: true, cancellationToken, forceResume: true);
    }

    public async Task EnsureConfiguredAsync(CancellationToken cancellationToken)
    {
        if (Settings.IsConfigured()) return;

        cancellationToken.ThrowIfCancellationRequested();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Initial Setup Required[/]");
        AnsiConsole.MarkupLine("Please provide the following required configuration values:");
        AnsiConsole.WriteLine();

        try
        {
            if (string.IsNullOrWhiteSpace(Settings.Instance.RealDebridApiToken))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var token = await ReadLineWithEffectAsync("Enter [green]Real-Debrid API Key[/]", cancellationToken, secret: true);
                    if (cancellationToken.IsCancellationRequested) break;
                    
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        AnsiConsole.MarkupLine("[red]Key cannot be empty.[/]");
                        continue;
                    }
                    Settings.Instance.RealDebridApiToken = token;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(Settings.Instance.MediaRoot))
            {
                var defaultPath = Settings.DefaultBaseRoot;
                var root = await ReadLineWithEffectAsync("Enter [green]Movies/Shows Root Path[/]", cancellationToken, defaultValue: defaultPath);
                if (root != null) Settings.Instance.MediaRoot = root;
            }
        }
        catch (OperationCanceledException)
        {
            var ex = new TerminationException("[red]Setup cancelled. Exiting...[/]");
            ex.Print();
            throw ex;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Settings.Save();
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Configuration saved successfully![/]");
        AnsiConsole.WriteLine();
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

    private async Task<string?> ReadLineWithEffectAsync(string prompt, CancellationToken ct, ConsoleColor color = ConsoleColor.White, int batchSize = 5, bool secret = false, string? defaultValue = null)
    {
        var displayPrompt = prompt.Trim();
        if (!string.IsNullOrEmpty(defaultValue))
        {
            displayPrompt = $"{displayPrompt} [blue]({defaultValue})[/]";
        }
        
        if (!displayPrompt.EndsWith(":")) displayPrompt += ":";
        AnsiConsole.Markup(displayPrompt + " ");
        
        var sb = new System.Text.StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    AnsiConsole.WriteLine();
                    var result = sb.ToString().Trim();
                    return string.IsNullOrEmpty(result) ? defaultValue : result;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Remove(sb.Length - 1, 1);
                        
                        // Handle manual wrap-around for backspace at the left edge
                        if (Console.CursorLeft == 0)
                        {
                            if (Console.CursorTop > 0)
                            {
                                // Move to end of previous line
                                Console.SetCursorPosition(Console.WindowWidth - 1, Console.CursorTop - 1);
                                Console.Write(" ");
                                Console.SetCursorPosition(Console.WindowWidth - 1, Console.CursorTop);
                            }
                        }
                        else
                        {
                            // Standard backspace within the same line
                            Console.Write("\b \b");
                        }
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    
                    Console.ForegroundColor = color;
                    Console.Write(secret ? "*" : key.KeyChar);
                    Console.ResetColor();

                    if (Console.KeyAvailable && sb.Length % batchSize == 0)
                    {
                        // Batch delay to keep the speed high but the "filling in" effect visible
                        await Task.Delay(1, ct);
                    }
                }
            }
            else
            {
                // Yield to keep UI responsive and avoid CPU pinning
                await Task.Delay(5, ct);
            }
        }

        return null;
    }

    private static void RenderMetadataPanel(MediaMetadata meta)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().PadLeft(1).PadRight(2))
            .AddColumn(new GridColumn());

        void AddGridRow(string label, string value) => grid.AddRow($"[bold]{label}[/]", ":", value);

        AddGridRow("Title", $"[cyan]{Markup.Escape(meta.Title)}[/]");

        if (meta.Type != "other" && !string.IsNullOrWhiteSpace(meta.Year))
        {
            AddGridRow("Year", $"[cyan]{Markup.Escape(meta.Year)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Type))
        {
            AddGridRow("Type", $"[cyan]{char.ToUpper(meta.Type[0]) + meta.Type[1..]}[/]");
        }

        if (meta.Season.HasValue)
        {
            AddGridRow("Season", $"[orange1]{meta.Season.Value}[/]");
        }

        if (meta.Episode.HasValue)
        {
            AddGridRow("Episode", $"[orange1]{meta.Episode.Value}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Version))
        {
            AddGridRow("Version", $"[cyan]{Markup.Escape(meta.Version)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Edition))
        {
            AddGridRow("Edition", $"[yellow]{Markup.Escape(meta.Edition)}[/]");
        }

        if (meta.HasDlc == true)
        {
            AddGridRow("Has DLC", "[green]Yes[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.ReleaseGroup))
        {
            AddGridRow("Group", $"[cyan]{Markup.Escape(meta.ReleaseGroup)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Resolution))
        {
            AddGridRow("Res", $"[cyan]{Markup.Escape(meta.Resolution)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Quality))
        {
            AddGridRow("Quality", $"[cyan]{Markup.Escape(meta.Quality)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Codec))
        {
            AddGridRow("Codec", $"[cyan]{Markup.Escape(meta.Codec)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.InstallerType))
        {
            AddGridRow("Installer", $"[cyan]{Markup.Escape(meta.InstallerType)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Source))
        {
            AddGridRow("Source", $"[dim]{Markup.Escape(meta.Source)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(meta.Destination))
        {
            AddGridRow("Location", $"[dim]{Markup.Escape(meta.Destination)}[/]");
        }

        var panel = new Panel(grid)
        {
            Header = new PanelHeader("Resolved Metadata", Justify.Center),
            Border = BoxBorder.Rounded,
            Expand = true
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private void OnDownloadProgressChanged(object? sender, DownloadProgressModel e)
    {
        if (_progressTasks.TryGetValue(e.ProgressKey, out var task))
        {
            if (e.TotalBytes > 0)
            {
                task.MaxValue = e.TotalBytes;
            }

            task.Value = e.BytesDownloaded;
            _taskSpeeds[task.Id] = e.SpeedBytesPerSecond;

            if (e.BytesDownloaded >= e.TotalBytes && e.TotalBytes > 0)
            {
                _taskDisplayStatuses[task.Id] = TaskDisplayStatus.Finished;
            }
        }
    }

    private void OnPauseChanged(bool isPaused)
    {
        if (isPaused)
        {
            var now = Environment.TickCount64;
            var interval = (long)AppSpinner.Interval.TotalMilliseconds;
            var count = AppSpinner.Frames.Count;
            var frameIdx = (int)((now / interval) % count);

            foreach (var task in _progressTasks.Values)
            {
                _frozenFrames[task.Id] = frameIdx;
            }
        }
        else
        {
            _frozenFrames.Clear();
        }

        foreach (var task in _progressTasks.Values)
        {
            if (task.IsFinished) continue;

            var current = task.Description;
            if (isPaused && !current.StartsWith("[yellow]PAUSED[/]"))
            {
                task.Description = $"[yellow]PAUSED[/] {current}";
            }
            else if (!isPaused && current.StartsWith("[yellow]PAUSED[/]"))
            {
                task.Description = current.Replace("[yellow]PAUSED[/] ", "");
            }
        }
    }

    private sealed class CustomTransferSpeedColumn : ProgressColumn
    {
        private readonly ConcurrentDictionary<int, double> _speeds;
        public CustomTransferSpeedColumn(ConcurrentDictionary<int, double> speeds) => _speeds = speeds;

        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            _speeds.TryGetValue(task.Id, out var speed);
            return new Text($"{Utils.FormatBytes((long)speed)}/s", new Style(Color.Silver));
        }
    }

    private sealed class CustomDownloadedColumn : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            var downloaded = (long)task.Value;
            var total = (long)task.MaxValue;

            if (total <= 0) return new Text("- / -");

            var downloadedStr = Utils.FormatBytes(downloaded);
            var totalStr = Utils.FormatBytes(total);

            return new Markup($"[blue]{downloadedStr}[/] / [green]{totalStr}[/]");
        }
    }

    private sealed class CustomEtaColumn : ProgressColumn
    {
        private readonly ConcurrentDictionary<int, double> _speeds;
        public CustomEtaColumn(ConcurrentDictionary<int, double> speeds) => _speeds = speeds;

        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            _speeds.TryGetValue(task.Id, out var speed);
            if (speed <= 0) return new Text("--");

            var remainingBytes = task.MaxValue - task.Value;
            var remainingSeconds = remainingBytes / speed;
            var eta = TimeSpan.FromSeconds(remainingSeconds);

            if (eta.TotalHours >= 1)
            {
                return new Text($"{(int)eta.TotalHours}h:{eta.Minutes:D2}m");
            }


            return new Text($"{(int)eta.TotalMinutes}m:{eta.Seconds:D2}s");
        }
    }

    private sealed class SpinnerColumn : ProgressColumn
    {
        private readonly Downloader _downloader;
        private readonly TuiApp _app;

        public SpinnerColumn(TuiApp app, Downloader downloader)
        {
            _app = app;
            _downloader = downloader;
        }

        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            if (_app._taskDisplayStatuses.TryGetValue(task.Id, out var status))
            {
                switch (status)
                {
                    case TaskDisplayStatus.Finished: return new Markup("[bold green]✓[/] ");
                    case TaskDisplayStatus.Saved:
                        _app._frozenFrames.TryGetValue(task.Id, out var sIdx);
                        sIdx %= AppSpinner.Frames.Count;
                        var sFrame = AppSpinner.Frames[sIdx];
                        return new Markup($"[bold blue]{Markup.Escape(sFrame)}[/] ");
                    case TaskDisplayStatus.Cancelled: return new Markup("[bold red]X[/] ");
                }
            }

            if (task.IsFinished)
            {
                return new Markup("[bold green]✓[/] ");
            }

            if (_downloader.IsPaused)
            {
                _app._frozenFrames.TryGetValue(task.Id, out var pIdx);
                pIdx %= AppSpinner.Frames.Count;
                var pFrame = AppSpinner.Frames[pIdx];
                return new Markup($"[bold yellow]{Markup.Escape(pFrame)}[/] ");
            }

            var frameIndex = (int)((Environment.TickCount64 / (long)AppSpinner.Interval.TotalMilliseconds) % AppSpinner.Frames.Count);
            var activeFrame = AppSpinner.Frames[frameIndex];
            return new Markup($"[bold yellow]{Markup.Escape(activeFrame)}[/] ");
        }
    }

    private sealed class EpisodeColumn : ProgressColumn
    {
        private readonly ConcurrentDictionary<int, string> _episodeTexts;
        public EpisodeColumn(ConcurrentDictionary<int, string> episodeTexts) => _episodeTexts = episodeTexts;

        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            if (_episodeTexts.TryGetValue(task.Id, out var epText))
            {
                return new Markup($"[orange1]{Markup.Escape(epText)}[/] ");
            }
            return Text.Empty;
        }
    }
}