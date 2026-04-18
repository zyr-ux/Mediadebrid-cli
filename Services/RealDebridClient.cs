using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MediaDebrid_cli.Models;

namespace MediaDebrid_cli.Services;

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
        return await HandleResponseAsync<TorrentAddResponse>(res, cancellationToken);
    }

    public async Task<List<TorrentItem>> GetTorrentsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var res = await _client.GetAsync($"{BaseUrl}/torrents?limit={limit}", cancellationToken);
        return await HandleResponseAsync<List<TorrentItem>>(res, cancellationToken);
    }

    public async Task<TorrentInfo> GetTorrentInfoAsync(string torrentId, CancellationToken cancellationToken = default)
    {
        var res = await _client.GetAsync($"{BaseUrl}/torrents/info/{torrentId}", cancellationToken);
        return await HandleResponseAsync<TorrentInfo>(res, cancellationToken);
    }

    public async Task SelectFilesAsync(string torrentId, string fileIds, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("files", fileIds)
        });

        var res = await _client.PostAsync($"{BaseUrl}/torrents/selectFiles/{torrentId}", content, cancellationToken);
        await HandleResponseAsync<object>(res, cancellationToken);
    }

    public async Task<UnrestrictResponse> UnrestrictLinkAsync(string link, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("link", link)
        });

        var res = await _client.PostAsync($"{BaseUrl}/unrestrict/link", content, cancellationToken);
        return await HandleResponseAsync<UnrestrictResponse>(res, cancellationToken);
    }

    private async Task<T> HandleResponseAsync<T>(HttpResponseMessage res, CancellationToken cancellationToken)
    {
        if (!res.IsSuccessStatusCode)
        {
            var errorBody = await res.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var errorRes = JsonSerializer.Deserialize<RealDebridErrorResponse>(errorBody);
                if (errorRes != null && !string.IsNullOrEmpty(errorRes.Error))
                {
                    throw new HttpRequestException($"Real-Debrid API Error: {errorRes.Error} (Code: {errorRes.ErrorCode})", null, res.StatusCode);
                }
            }
            catch (JsonException) { /* Not a JSON error or different format, fallback to default */ }

            res.EnsureSuccessStatusCode();
        }

        if (typeof(T) == typeof(object)) return default!;
        return await res.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Failed to deserialize response.");
    }
}
