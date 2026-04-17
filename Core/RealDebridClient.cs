using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Core;

public class RealDebridClient
{
    private const string BaseUrl = "https://api.real-debrid.com/rest/1.0";
    private readonly HttpClient _client;

    public RealDebridClient()
    {
        if (string.IsNullOrWhiteSpace(Settings.RealDebridApiToken))
        {
            throw new InvalidOperationException("Real-Debrid API token is missing in .env configuration.");
        }

        _client = new HttpClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Settings.RealDebridApiToken);
    }

    public async Task<TorrentAddResponse> AddMagnetAsync(string magnet, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("magnet", magnet)
        });

        var res = await _client.PostAsync($"{BaseUrl}/torrents/addMagnet", content, cancellationToken);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<TorrentAddResponse>(cancellationToken: cancellationToken) ?? new TorrentAddResponse();
    }

    public async Task<List<TorrentItem>> GetTorrentsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var res = await _client.GetAsync($"{BaseUrl}/torrents?limit={limit}", cancellationToken);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<TorrentItem>>(cancellationToken: cancellationToken) ?? new List<TorrentItem>();
    }

    public async Task<TorrentInfo> GetTorrentInfoAsync(string torrentId, CancellationToken cancellationToken = default)
    {
        var res = await _client.GetAsync($"{BaseUrl}/torrents/info/{torrentId}", cancellationToken);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<TorrentInfo>(cancellationToken: cancellationToken) ?? new TorrentInfo();
    }

    public async Task SelectFilesAsync(string torrentId, string fileIds, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("files", fileIds)
        });

        var res = await _client.PostAsync($"{BaseUrl}/torrents/selectFiles/{torrentId}", content, cancellationToken);
        res.EnsureSuccessStatusCode();
    }

    public async Task<UnrestrictResponse> UnrestrictLinkAsync(string link, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("link", link)
        });

        var res = await _client.PostAsync($"{BaseUrl}/unrestrict/link", content, cancellationToken);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<UnrestrictResponse>(cancellationToken: cancellationToken) ?? new UnrestrictResponse();
    }
}
