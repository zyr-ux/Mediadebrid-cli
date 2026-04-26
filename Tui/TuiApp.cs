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

    public async Task RunAsync(string magnet, string? seasonOverride = null, string? episodeOverride = null, bool showLogo = true, CancellationToken cancellationToken = default, bool forceResume = false, bool generateUnresLinks = false)
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
        HashSet<string>? existingEpisodeKeys = null;

        var hash = MagnetParser.ExtractHash(magnet);
        if (string.IsNullOrEmpty(hash))
        {
            throw new MagnetException("Invalid magnet link: Missing BTIH hash (xt=urn:btih:).");
        }

        TorrentItem? matched = null;
        bool isCached = false;
        bool newlyAdded = false;

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

        AnsiConsole.WriteLine();
        if (!isCached)
        {
            string statusMsg = matched?.Status == "downloading" || matched?.Status == "queued" 
                ? $"is currently [bold red]{matched.Status}[/]" 
                : "is [bold red]Not Cached[/]";
            
            AnsiConsole.MarkupLine($"[bold red]X[/] This magnet {statusMsg} on Real-Debrid servers.");
            
            if (!await ConfirmAsync("Do you want Real-Debrid to cache it for you?", cancellationToken))
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
                    info = await GetClient().WaitForStatusAsync(torrentId, ["waiting_files_selection", "downloaded", "dead"
                    ], cancellationToken);

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

        bool needsNewline = true;

        if (info.Status == "dead")
        {
            throw new TerminationException("[bold red]X[/] Torrent is dead.");
        }

        // Interactive season selection for multi-season shows
        if (resolved.Type == "show" && string.IsNullOrEmpty(seasonOverride))
        {
            if (needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
            var seasonsInTorrent = info.Files
                .Select(f =>
                {
                    var meta = _metadataResolver.ParseName(f.Path);
                    var seasons = Utils.ParseRange(meta.Season);
                    return seasons.Any() ? (int?)seasons.First() : null;
                })
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            if (seasonsInTorrent.Count > 1)
            {
                // Display accurate intent for all-seasons mode before prompting for overrides.
                resolved.Season = "Multiple";
                var defaultSeasonDir = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, 1);
                resolved.Destination = Directory.GetParent(defaultSeasonDir)?.FullName ?? defaultSeasonDir;
                RenderMetadataPanel(resolved);

                try
                {
                    string? input = null;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        input = await ReadLineWithEffectAsync($"[yellow]Multiple seasons detected ({string.Join(", ", seasonsInTorrent.Select(s => $"S{s:D2}"))}).[/]\nEnter [green]season number or range[/] (e.g. 1-3) to download (leave empty for all)", cancellationToken);
                        
                        if (cancellationToken.IsCancellationRequested) break;
                        if (string.IsNullOrWhiteSpace(input)) break;

                        var parsed = Utils.ParseRange(input);
                        if (!parsed.Any())
                        {
                            AnsiConsole.MarkupLine("[red]Please enter a valid season number or range (e.g., 1-2, 4).[/]");
                            continue;
                        }
                        
                        if (!parsed.Any(s => seasonsInTorrent.Contains(s)))
                        {
                            AnsiConsole.MarkupLine($"[red]None of the specified seasons ({input}) were found in this torrent.[/]");
                            continue;
                        }
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        seasonOverride = input;
                        resolved.Season = input;
                        resolved.Destination = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year); // Generic dir for ranges
                        AnsiConsole.MarkupLine($"[bold green]✓[/] Selected seasons [cyan]{input}[/].");
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
        if (resolved.Type == "show" && string.IsNullOrEmpty(episodeOverride))
        {
            var sRange = Utils.ParseRange(seasonOverride);
            var episodesInTorrent = info.Files
                .Where(f =>
                {
                    if (f.Bytes < 50_000_000) return false;
                    var meta = _metadataResolver.ParseName(f.Path);
                    var fileSeasons = Utils.ParseRange(meta.Season);
                    return sRange.Count == 0 || fileSeasons.Any(s => sRange.Contains(s));
                })
                .SelectMany(f =>
                {
                    var meta = _metadataResolver.ParseName(f.Path);
                    return Utils.ParseRange(meta.Episode);
                })
                .Distinct()
                .OrderBy(e => e)
                .ToList();

            if (episodesInTorrent.Count == 1)
            {
                if (needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
                episodeOverride = episodesInTorrent[0].ToString();
                resolved.Episode = episodeOverride;
                AnsiConsole.MarkupLine($"[bold green]✓[/] Only one episode detected ([cyan]E{episodesInTorrent[0]:D2}[/]). Auto-selecting.");
            }
            else
            {
                if (needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
                try
                {
                    string? input = null;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        input = await ReadLineWithEffectAsync("Enter [green]episode number or range[/] (e.g. 1-12) to download (leave empty for all)", cancellationToken);
                        
                        if (cancellationToken.IsCancellationRequested) break;
                        if (string.IsNullOrWhiteSpace(input)) break;

                        var parsed = Utils.ParseRange(input);
                        if (!parsed.Any())
                        {
                            AnsiConsole.MarkupLine("[red]Please enter a valid episode number or range (e.g., 1-5, 8).[/]");
                            continue;
                        }

                        if (!info.Files.Any(f =>
                        {
                            var meta = _metadataResolver.ParseName(f.Path);
                            var fileSeasons = Utils.ParseRange(meta.Season);
                            var fileEpisodes = Utils.ParseRange(meta.Episode);
                            
                            if (sRange.Any() && !fileSeasons.Any(s => sRange.Contains(s))) return false;
                            return fileEpisodes.Any(e => parsed.Contains(e));
                        }))
                        {
                            var scope = sRange.Any() ? "in selected seasons" : "in this torrent";
                            AnsiConsole.MarkupLine($"[red]No episodes from range {input} found {scope}.[/]");
                            continue;
                        }
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        episodeOverride = input;
                        resolved.Episode = input;
                        AnsiConsole.MarkupLine($"[bold green]✓[/] Selected episodes [cyan]{input}[/].");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw new TerminationException("[red]Application terminated. Exiting...[/]");
                }
            }
        }

        var selectedSeasons = Utils.ParseRange(seasonOverride);
        if (resolved.Type == "show" && selectedSeasons.Count == 0)
        {
            selectedSeasons = info.Files
                .Select(f =>
                {
                    var meta = _metadataResolver.ParseName(f.Path);
                    var seasons = Utils.ParseRange(meta.Season);
                    return seasons.Any() ? (int?)seasons.First() : null;
                })
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToHashSet();

            if (!selectedSeasons.Any() && !string.IsNullOrEmpty(resolved.Season))
            {
                selectedSeasons = Utils.ParseRange(resolved.Season);
            }

            if (!selectedSeasons.Any()) selectedSeasons.Add(1);
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
                        var seasonsToCheck = selectedSeasons;

                        existingEpisodeKeys = new HashSet<string>();
                        foreach (var s in seasonsToCheck)
                        {
                            var seasonDir = PathGenerator.GetSeasonDirectory(resolved.Type, resolved.Title, resolved.Year, s);
                            var existingInSeason = Utils.GetExistingEpisodes(seasonDir);
                            foreach (var ep in existingInSeason)
                            {
                                existingEpisodeKeys.Add(Utils.BuildEpisodeKey(s, ep));
                            }
                        }

                        if (existingEpisodeKeys.Any())
                        {
                            var epRange = Utils.ParseRange(episodeOverride);
                            var allSelectedExist = epRange.Any() && seasonsToCheck.All(s => epRange.All(e => existingEpisodeKeys.Contains(Utils.BuildEpisodeKey(s, e))));
                            if (allSelectedExist)
                            {
                                throw new TerminationException($"[bold red]All selected episodes ({episodeOverride}) already exist in your local library.[/]");
                            }
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine($"[yellow]X[/] Found [cyan]{existingEpisodeKeys.Count}[/] existing episodes in local library. They will be skipped.");
                        }
                    }

                    if (info.Status == "waiting_files_selection")
                    {
                        ctx.Status("[yellow]Selecting files...[/]");
                        var fileIds = Utils.GetSelectedFiles(info.Files, seasonOverride, episodeOverride, existingEpisodeKeys);

                        if (!fileIds.Any())
                        {
                            throw new TerminationException("[bold red]X[/] No files found to download.");
                        }

                        await GetClient().SelectFilesAsync(torrentId, string.Join(",", fileIds), cancellationToken: cancellationToken);
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
            if (needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
            AnsiConsole.MarkupLine("[bold red]X[/] Magnet is [bold red]not cached[/] on Real-Debrid servers.");
            if (!await ConfirmAsync("Do you want to wait for Real-Debrid to cache it?", cancellationToken))
            {
                await AnsiConsole.Status().StartAsync("[red]Removing magnet...[/]", async _ => 
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
                    ctx.Spinner(Spinner.Known.Arc);
                    ctx.SpinnerStyle(Style.Parse("yellow"));
                    info = await GetClient().WaitForStatusAsync(torrentId, ["downloaded"], cancellationToken, pollDelayMs: 5000);
                });
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]✓[/] Files are ready and cached!");

        if (generateUnresLinks)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Generating Unrestricted Links...[/]");
            AnsiConsole.WriteLine();
            
            await AnsiConsole.Status().StartAsync("[yellow]Unrestricting links...[/]", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Arc);
                foreach (var link in info.Links)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    
                    var unrestricted = await GetClient().UnrestrictLinkAsync(link, cancellationToken: cancellationToken);
                    
                    AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(unrestricted.Filename)}[/]");
                    AnsiConsole.MarkupLine($"[white]{unrestricted.Download}[/]");
                    AnsiConsole.WriteLine();
                }
            });
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Starting Downloads...[/]");
        AnsiConsole.MarkupLine("[dim]Controls: [yellow]P[/] Pause | [green]X[/] Save & Exit | [red]Ctrl+C[/] Cancel & Delete[/]");

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
                var destPath = PathGenerator.GetDestinationPath(resolved.Type, resolved.Title, resolved.Year, filename, seasonOverride);

                // Skip if file already exists locally
                if (File.Exists(destPath)) continue;

                // Skip if it's an existing episode (for shows)
                if (resolved.Type == "show" && Settings.Instance.SkipExistingEpisodes)
                {
                    var meta = _metadataResolver.ParseName(filename);
                    var episodes = Utils.ParseRange(meta.Episode);
                    if (episodes.Any() && existingEpisodeKeys != null)
                    {
                        var seasons = Utils.ParseRange(meta.Season);
                        var sNum = seasons.Any() ? (int?)seasons.First() : null;
                        
                        if (!sNum.HasValue && selectedSeasons.Count == 1)
                        {
                            sNum = selectedSeasons.First();
                        }

                        if (sNum.HasValue && episodes.Any(e => existingEpisodeKeys.Contains(Utils.BuildEpisodeKey(sNum.Value, e)))) continue;
                    }
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
        needsNewline = true;
        for (int i = 0; i < queuedDownloads.Count; i++)
        {
            var item = queuedDownloads[i];
            if (item.ResumeData != null)
            {
                if (!forceResume && needsNewline) { AnsiConsole.WriteLine(); needsNewline = false; }
                if (forceResume || await ConfirmAsync($"[yellow]Partial download found for {Markup.Escape(item.Unrestricted.Filename)} ({Utils.FormatBytes(item.ResumeData.Segments.Sum(s => s.Current - s.Start))} / {Utils.FormatBytes(item.ResumeData.TotalSize)}). Resume?[/]", cancellationToken))
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
        
        AnsiConsole.WriteLine();

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
                                    var meta = _metadataResolver.ParseName(filename);
                                    var episodes = Utils.ParseRange(meta.Episode);
                                    if (episodes.Any() && existingEpisodeKeys != null)
                                    {
                                        var seasons = Utils.ParseRange(meta.Season);
                                        var sNum = seasons.Any() ? (int?)seasons.First() : null;

                                        if (!sNum.HasValue && selectedSeasons.Count == 1)
                                        {
                                            sNum = selectedSeasons.First();
                                        }

                                        if (sNum.HasValue && episodes.Any(e => existingEpisodeKeys.Contains(Utils.BuildEpisodeKey(sNum.Value, e))))
                                        {
                                            continue;
                                        }
                                    }
                                }

                                var progressKey = destPath;
                                activePaths.Add(tempPath);

                                var displayFilename = filename.Length > 40 ? filename[..37] + "..." : filename;

                                progressTask = ctx.AddTask($"[cyan]{Markup.Escape(displayFilename)}[/]", new ProgressTaskSettings { AutoStart = false });
                                _progressTasks[progressKey] = progressTask;

                                if (resolved.Type == "show")
                                {
                                    var meta = _metadataResolver.ParseName(filename);
                                    var episodes = Utils.ParseRange(meta.Episode);
                                    if (episodes.Any())
                                    {
                                        var seasons = Utils.ParseRange(meta.Season);
                                        var sNum = seasons.Any() ? seasons.First() : 1;
                                        _taskEpisodeTexts[progressTask.Id] = $"S{sNum:D2}E{episodes.First():D2}";
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
                                    if (!t.IsFinished)
                                    {
                                        _taskDisplayStatuses.TryAdd(t.Id, TaskDisplayStatus.Cancelled);
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
                                if (!t.IsFinished)
                                {
                                    _taskDisplayStatuses.TryAdd(t.Id, TaskDisplayStatus.Cancelled);
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
            AnsiConsole.MarkupLine($"[red]Unexpected error ({Markup.Escape(ex.GetType().Name)}):[/] [white]{Markup.Escape(ex.Message)}[/]");
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

    public async Task RunInteractiveAsync(CancellationToken cancellationToken, bool generateUnresLinks = false)
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

        if (magnet is null || cancellationToken.IsCancellationRequested) return;

        await RunAsync(magnet, showLogo: false, cancellationToken: cancellationToken, generateUnresLinks: generateUnresLinks);
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
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.White);

        table.AddColumn("[bold]Key[/]");
        table.AddColumn("[bold]Value[/]");

        var metadata = Utils.GetConfigurationMetadata();
        foreach (var (key, type, description) in metadata)
        {
            string value = key switch
            {
                "real_debrid_api_key" => Settings.Instance.RealDebridApiToken,
                "media_root" => Settings.IsDefault(Settings.Instance.MediaRoot) ? $"default ({Settings.MediaRoot})" : Settings.Instance.MediaRoot,
                "games_root" => Settings.IsDefault(Settings.Instance.GamesRoot) ? $"default ({Settings.GamesRoot})" : Settings.Instance.GamesRoot,
                "others_root" => Settings.IsDefault(Settings.Instance.OthersRoot) ? $"default ({Settings.OthersRoot})" : Settings.Instance.OthersRoot,
                "parallel_download" => Settings.Instance.ParallelDownloadEnabled.ToString().ToLower(),
                "connections_per_file" => Settings.Instance.ConnectionsPerFile.ToString(),
                "skip_existing_episodes" => Settings.Instance.SkipExistingEpisodes.ToString().ToLower(),
                _ => "N/A"
            };

            // Highlight the API key differently
            var displayValue = key == "real_debrid_api_key" && !string.IsNullOrEmpty(value)
                ? $"[green]{value}[/]"
                : $"[white]{value}[/]";

            table.AddRow(
                $"[yellow]{key}[/]",
                displayValue
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey] Use 'mediadebrid set <key> <value>' to modify these settings[/]");
        AnsiConsole.WriteLine();
    }

    private async Task<bool> ConfirmAsync(string prompt, CancellationToken ct, bool defaultValue = true)
    {
        var choice = defaultValue ? "[[y/n]] (y)" : "[[y/n]] (n)";

        while (!ct.IsCancellationRequested)
        {
            var result = await ReadLineWithEffectAsync($"{prompt} [green]{choice}[/]: ", ct);
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrWhiteSpace(result)) return defaultValue;

            var trimmed = result.Trim().ToLowerInvariant();
            if (trimmed is "y" or "yes") return true;
            if (trimmed is "n" or "no") return false;

            AnsiConsole.MarkupLine("[red]Please enter 'y' or 'n'.[/]");
        }

        throw new OperationCanceledException(ct);
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

        if (!string.IsNullOrEmpty(meta.Season))
        {
            AddGridRow("Season", $"[orange1]{Markup.Escape(meta.Season)}[/]");
        }

        if (!string.IsNullOrEmpty(meta.Episode))
        {
            AddGridRow("Episode", $"[orange1]{Markup.Escape(meta.Episode)}[/]");
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

    private sealed class CustomTransferSpeedColumn(ConcurrentDictionary<int, double> speeds) : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            speeds.TryGetValue(task.Id, out var speed);
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

    private sealed class CustomEtaColumn(ConcurrentDictionary<int, double> speeds) : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            speeds.TryGetValue(task.Id, out var speed);
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

    private sealed class SpinnerColumn(TuiApp app, Downloader downloader) : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            if (app._taskDisplayStatuses.TryGetValue(task.Id, out var status))
            {
                switch (status)
                {
                    case TaskDisplayStatus.Finished: return new Markup("[bold green]✓[/] ");
                    case TaskDisplayStatus.Saved:
                        app._frozenFrames.TryGetValue(task.Id, out var sIdx);
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

            if (downloader.IsPaused)
            {
                app._frozenFrames.TryGetValue(task.Id, out var pIdx);
                pIdx %= AppSpinner.Frames.Count;
                var pFrame = AppSpinner.Frames[pIdx];
                return new Markup($"[bold yellow]{Markup.Escape(pFrame)}[/] ");
            }

            var frameIndex = (int)((Environment.TickCount64 / (long)AppSpinner.Interval.TotalMilliseconds) % AppSpinner.Frames.Count);
            var activeFrame = AppSpinner.Frames[frameIndex];
            return new Markup($"[bold yellow]{Markup.Escape(activeFrame)}[/] ");
        }
    }

    private sealed class EpisodeColumn(ConcurrentDictionary<int, string> episodeTexts) : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            if (episodeTexts.TryGetValue(task.Id, out var epText))
            {
                return new Markup($"[orange1]{Markup.Escape(epText)}[/] ");
            }
            return Text.Empty;
        }
    }
}