using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

public class Downloader
{
    private readonly HttpClient _httpClient;
    private static readonly ConcurrentDictionary<string, byte> _activeTempFiles = new();

    static Downloader()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupStatic();
    }

    public static void CleanupStatic()
    {
        foreach (var file in _activeTempFiles.Keys)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch { /* Ignore */ }
        }
    }

    public static void CleanupStaleFiles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return;

        try
        {
            var files = Directory.GetFiles(rootPath, "*.mdebrid", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { /* Ignore errors accessing directories */ }
    }

    public event EventHandler<DownloadProgressModel>? ProgressChanged;

    private long _lastUpdateTicks = 0;
    private static readonly long UpdateIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;

    public Downloader()
    {
        _httpClient = new HttpClient();
    }

    public async Task DownloadFileAsync(string url, string destPath, CancellationToken cancellationToken = default)
    {
        bool segmented = Settings.ParallelDownloadEnabled;
        int segments = Settings.ConnectionsPerFile;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (segmented)
        {
            await DownloadSegmentedAsync(url, destPath, segments, cancellationToken);
        }
        else
        {
            await DownloadSingleAsync(url, destPath, cancellationToken: cancellationToken);
        }
    }

    private async Task DownloadSegmentedAsync(string url, string destPath, int segments, CancellationToken cancellationToken = default)
    {
        var headReq = new HttpRequestMessage(HttpMethod.Head, url);
        var headRes = await _httpClient.SendAsync(headReq, cancellationToken);
        headRes.EnsureSuccessStatusCode();

        long totalSize = headRes.Content.Headers.ContentLength ?? 0;
        bool acceptRanges = headRes.Headers.AcceptRanges.Contains("bytes");

        if (totalSize == 0 || !acceptRanges || segments <= 1)
        {
            await DownloadSingleAsync(url, destPath, totalSize, cancellationToken);
            return;
        }

        string tempPath = CreateTempFile(destPath, totalSize);
        var ranges = PlanByteRanges(totalSize, segments);

        long bytesDownloaded = 0;
        DateTime startTime = DateTime.UtcNow;

        var tasks = ranges.Select(async range =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(range.Item1, range.Item2);

            var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.Write, 8192, useAsync: true);
            fileStream.Position = range.Item1;

            byte[] buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                var currentDownloaded = Interlocked.Add(ref bytesDownloaded, read);

                long currentTicks = DateTime.UtcNow.Ticks;
                if (currentTicks - Interlocked.Read(ref _lastUpdateTicks) > UpdateIntervalTicks || currentDownloaded == totalSize)
                {
                    Interlocked.Exchange(ref _lastUpdateTicks, currentTicks);
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    double speed = elapsed > 0 ? currentDownloaded / elapsed : 0;

                    ProgressChanged?.Invoke(this, new DownloadProgressModel
                    {
                        Filename = Path.GetFileName(destPath),
                        BytesDownloaded = currentDownloaded,
                        TotalBytes = totalSize,
                        SpeedBytesPerSecond = speed
                    });
                }
            }
        });

        try
        {
            await Task.WhenAll(tasks);
            FinalizeDownload(tempPath, destPath);
        }
        catch
        {
            _activeTempFiles.TryRemove(tempPath, out _);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private async Task DownloadSingleAsync(string url, string destPath, long totalSize = 0, CancellationToken cancellationToken = default)
    {
        var res = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        res.EnsureSuccessStatusCode();

        totalSize = totalSize > 0 ? totalSize : (res.Content.Headers.ContentLength ?? 0);
        string tempPath = CreateTempFile(destPath, totalSize);

        long bytesDownloaded = 0;
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
                if (currentTicks - Interlocked.Read(ref _lastUpdateTicks) > UpdateIntervalTicks || bytesDownloaded == totalSize)
                {
                    Interlocked.Exchange(ref _lastUpdateTicks, currentTicks);
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    double speed = elapsed > 0 ? bytesDownloaded / elapsed : 0;

                    ProgressChanged?.Invoke(this, new DownloadProgressModel
                    {
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
        catch (OperationCanceledException)
        {
            _activeTempFiles.TryRemove(tempPath, out _);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
        catch
        {
            _activeTempFiles.TryRemove(tempPath, out _);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private string CreateTempFile(string destPath, long totalSize)
    {
        string tempPath = destPath + ".mdebrid";
        _activeTempFiles.TryAdd(tempPath, 0);

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

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempPath, destPath);
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
