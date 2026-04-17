using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Core;

public class Downloader
{
    private readonly HttpClient _httpClient;

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;

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
                Interlocked.Add(ref bytesDownloaded, read);

                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                double speed = elapsed > 0 ? bytesDownloaded / elapsed : 0;

                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    Filename = Path.GetFileName(destPath),
                    BytesDownloaded = bytesDownloaded,
                    TotalBytes = totalSize,
                    SpeedBytesPerSecond = speed
                });
            }
        });

        try
        {
            await Task.WhenAll(tasks);
            FinalizeDownload(tempPath, destPath);
        }
        catch
        {
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

                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                double speed = elapsed > 0 ? bytesDownloaded / elapsed : 0;

                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    Filename = Path.GetFileName(destPath),
                    BytesDownloaded = bytesDownloaded,
                    TotalBytes = totalSize,
                    SpeedBytesPerSecond = speed
                });
            }

            fileStream.Close();
            FinalizeDownload(tempPath, destPath);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private string CreateTempFile(string destPath, long totalSize)
    {
        string tempDir = Path.Combine(Path.GetDirectoryName(destPath)!, ".mediadebrid-temp");
        Directory.CreateDirectory(tempDir);
        string tempPath = Path.Combine(tempDir, "part-" + Guid.NewGuid() + ".tmp");

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (totalSize > 0)
            {
                fs.SetLength(totalSize);
            }
        }
        return tempPath;
    }

    private void FinalizeDownload(string tempPath, string destPath)
    {
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
