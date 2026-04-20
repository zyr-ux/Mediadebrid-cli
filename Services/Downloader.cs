using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

public class Downloader
{
    private readonly HttpClient _httpClient;
    private static readonly ConcurrentDictionary<string, string> _activeTempFiles = new();

    static Downloader()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupStatic();
    }

    public static void CleanupStatic()
    {
        CleanupFiles(_activeTempFiles.Keys);
    }

    public static void CleanupFiles(IEnumerable<string> tempPaths, string? rootPath = null)
    {
        foreach (var path in tempPaths.ToList())
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                
                string? currentRoot = rootPath;
                if (currentRoot == null)
                {
                    _activeTempFiles.TryGetValue(path, out currentRoot);
                    currentRoot ??= Settings.MediaRoot;
                }

                string? dir = Path.GetDirectoryName(path);
                if (dir != null) DeleteEmptyDirectories(dir, currentRoot);
            }
            catch { /* Ignore cleanup errors */ }
            finally
            {
                _activeTempFiles.TryRemove(path, out _);
            }
        }
    }

    private static void DeleteEmptyDirectories(string? directoryPath, string rootPath)
    {
        if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(rootPath)) return;

        try
        {
            var currentDir = new DirectoryInfo(directoryPath);
            var rootDir = new DirectoryInfo(Path.GetFullPath(rootPath));

            while (currentDir != null && currentDir.Exists &&
                   currentDir.FullName.Length > rootDir.FullName.Length &&
                   currentDir.FullName.StartsWith(rootDir.FullName, StringComparison.OrdinalIgnoreCase))
            {
                // Refresh to get latest state
                currentDir.Refresh();
                if (!currentDir.Exists) break;

                // Check if directory is empty
                if (currentDir.GetFileSystemInfos().Length == 0)
                {
                    try
                    {
                        currentDir.Delete();
                    }
                    catch (IOException) { /* Directory might have become non-empty or been deleted */ break; }
                    
                    currentDir = currentDir.Parent;
                }
                else
                {
                    break;
                }
            }
        }
        catch
        {
            // Ignore errors during directory cleanup
        }
    }


    public event EventHandler<DownloadProgressModel>? ProgressChanged;

    private static readonly long UpdateIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;

    public Downloader()
    {
        _httpClient = new HttpClient();
    }

    public async Task DownloadFileAsync(string url, string destPath, string? rootPath = null, string? progressKey = null, CancellationToken cancellationToken = default)
    {
        bool segmented = Settings.ParallelDownloadEnabled;
        int segments = Settings.ConnectionsPerFile;
        string resolvedProgressKey = string.IsNullOrWhiteSpace(progressKey) ? destPath : progressKey;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (segmented)
        {
            await DownloadSegmentedAsync(url, destPath, segments, resolvedProgressKey, rootPath, cancellationToken);
        }
        else
        {
            await DownloadSingleAsync(url, destPath, resolvedProgressKey, rootPath, 0, cancellationToken);
        }
    }

    private async Task DownloadSegmentedAsync(string url, string destPath, int segments, string progressKey, string? rootPath = null, CancellationToken cancellationToken = default)
    {
        var headReq = new HttpRequestMessage(HttpMethod.Head, url);
        var headRes = await _httpClient.SendAsync(headReq, cancellationToken);
        headRes.EnsureSuccessStatusCode();

        long totalSize = headRes.Content.Headers.ContentLength ?? 0;
        bool acceptRanges = headRes.Headers.AcceptRanges.Contains("bytes");

        if (totalSize == 0 || !acceptRanges || segments <= 1)
        {
            await DownloadSingleAsync(url, destPath, progressKey, rootPath, totalSize, cancellationToken);
            return;
        }

        string tempPath = CreateTempFile(destPath, totalSize, rootPath);
        var ranges = PlanByteRanges(totalSize, segments);

        long bytesDownloaded = 0;
        long lastUpdateTicks = 0;
        DateTime startTime = DateTime.UtcNow;

        using var internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = ranges.Select(async range =>
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(range.Item1, range.Item2);

                var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, internalCts.Token);
                res.EnsureSuccessStatusCode();

                using var stream = await res.Content.ReadAsStreamAsync(internalCts.Token);
                using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.Write, 8192, useAsync: true);
                fileStream.Position = range.Item1;

                byte[] buffer = new byte[65536];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, internalCts.Token)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, internalCts.Token);
                    var currentDownloaded = Interlocked.Add(ref bytesDownloaded, read);

                    long currentTicks = DateTime.UtcNow.Ticks;
                    if (currentTicks - Interlocked.Read(ref lastUpdateTicks) > UpdateIntervalTicks || currentDownloaded == totalSize)
                    {
                        Interlocked.Exchange(ref lastUpdateTicks, currentTicks);
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        double speed = elapsed > 0 ? currentDownloaded / elapsed : 0;

                        ProgressChanged?.Invoke(this, new DownloadProgressModel
                        {
                            ProgressKey = progressKey,
                            Filename = Path.GetFileName(destPath),
                            BytesDownloaded = currentDownloaded,
                            TotalBytes = totalSize,
                            SpeedBytesPerSecond = speed
                        });
                    }
                }
            }
            catch
            {
                internalCts.Cancel();
                throw;
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
            FinalizeDownload(tempPath, destPath);
        }
        catch
        {
            internalCts.Cancel();
            try { await Task.WhenAll(tasks); } catch { } 
            CleanupFiles(new[] { tempPath }, rootPath);
            throw;
        }
    }

    private async Task DownloadSingleAsync(string url, string destPath, string progressKey, string? rootPath = null, long totalSize = 0, CancellationToken cancellationToken = default)
    {
        var res = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        res.EnsureSuccessStatusCode();

        totalSize = totalSize > 0 ? totalSize : (res.Content.Headers.ContentLength ?? 0);
        string tempPath = CreateTempFile(destPath, totalSize, rootPath);

        long bytesDownloaded = 0;
        long lastUpdateTicks = 0;
        DateTime startTime = DateTime.UtcNow;

        try
        {
            using var stream = await res.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 8192, useAsync: true);

            byte[] buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                bytesDownloaded += read;

                long currentTicks = DateTime.UtcNow.Ticks;
                if (currentTicks - lastUpdateTicks > UpdateIntervalTicks || bytesDownloaded == totalSize)
                {
                    lastUpdateTicks = currentTicks;
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    double speed = elapsed > 0 ? bytesDownloaded / elapsed : 0;

                    ProgressChanged?.Invoke(this, new DownloadProgressModel
                    {
                        ProgressKey = progressKey,
                        Filename = Path.GetFileName(destPath),
                        BytesDownloaded = bytesDownloaded,
                        TotalBytes = totalSize,
                        SpeedBytesPerSecond = speed
                    });
                }
            }

            fileStream.Close();
            FinalizeDownload(tempPath, destPath);
        }
        catch
        {
            CleanupFiles(new[] { tempPath }, rootPath);
            throw;
        }
    }

    private string CreateTempFile(string destPath, long totalSize, string? rootPath = null)
    {
        string tempPath = destPath + ".mdebrid";
        _activeTempFiles.TryAdd(tempPath, rootPath ?? Settings.MediaRoot);

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (totalSize > 0)
            {
                fs.SetLength(totalSize);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetAttributes(tempPath, FileAttributes.Hidden); } catch { }
        }

        return tempPath;
    }

    private void FinalizeDownload(string tempPath, string destPath)
    {
        _activeTempFiles.TryRemove(tempPath, out _);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetAttributes(tempPath, FileAttributes.Normal); } catch { }
        }

        File.Move(tempPath, destPath, overwrite: true);
    }

    private List<Tuple<long, long>> PlanByteRanges(long totalSize, int segments)
    {
        var ranges = new List<Tuple<long, long>>();
        long baseSize = totalSize / segments;
        long remainder = totalSize % segments;
        long cursor = 0;

        for (int i = 0; i < segments; i++)
        {
            long size = baseSize + (i < remainder ? 1 : 0);
            long start = cursor;
            long end = cursor + size - 1;
            ranges.Add(Tuple.Create(start, end));
            cursor = end + 1;
        }
        return ranges;
    }
}
