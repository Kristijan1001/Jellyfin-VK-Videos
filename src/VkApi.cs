using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VkVideos;

// The only permitted VK operations are read-only video listing and playback lookups.
public sealed class VkApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest;

    public VkApi() : this(new HttpClientHandler()) { }
    public VkApi(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("okhttp/4.9.0");
    }

    public async Task<JsonElement> CallAsync(string method, Dictionary<string, string> parameters,
        PluginConfiguration config, CancellationToken ct)
    {
        if (method is not ("video.get" or "video.getAlbums"))
            throw new ArgumentException("Only read-only VK video methods are supported.", nameof(method));
        if (config.OwnerId == 0 || string.IsNullOrWhiteSpace(config.AccessToken))
            throw new InvalidOperationException("Configure the VK owner ID and access token in the VK Videos plugin settings.");

        var body = new Dictionary<string, string>(parameters)
        {
            ["access_token"] = config.AccessToken.Trim(), ["v"] = "5.199"
        };
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            bool retry = false;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = TimeSpan.FromMilliseconds(400) - (DateTimeOffset.UtcNow - _lastRequest);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                _lastRequest = DateTimeOffset.UtcNow;
                using var content = new FormUrlEncodedContent(body);
                using var response = await _http.PostAsync("https://api.vk.com/method/" + method, content, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    retry = true;
                else
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"VK {method} returned HTTP {(int)response.StatusCode}.");
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (json.RootElement.TryGetProperty("error", out var error))
                    {
                        int code = JsonValue.Int(error, "error_code");
                        if (code is 1 or 6 or 9 or 10 or 29 or 204) retry = true;
                        else throw new InvalidOperationException($"VK {method} failed (error {code}). Check the token, permissions and owner ID.");
                    }
                    else if (json.RootElement.TryGetProperty("response", out var value)) return value.Clone();
                    else throw new InvalidOperationException("VK returned an unexpected response; existing cached data was preserved.");
                }
            }
            catch (HttpRequestException) { retry = true; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { retry = true; }
            finally { _gate.Release(); }
            if (retry && attempt < 4) await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << attempt)), ct).ConfigureAwait(false);
        }
        // Do not log response bodies or request contents: VK errors can echo credentials.
        throw new InvalidOperationException($"VK {method} is temporarily unavailable after five attempts. Existing cached data was preserved.");
    }

    public async Task<List<JsonElement>> ListAsync(string method, Dictionary<string, string> parameters,
        PluginConfiguration config, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        int offset = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var pageParams = new Dictionary<string, string>(parameters) { ["offset"] = offset.ToString(CultureInfo.InvariantCulture) };
            var page = await CallAsync(method, pageParams, config, ct).ConfigureAwait(false);
            if (!page.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                !page.TryGetProperty("count", out var totalValue) || !totalValue.TryGetInt32(out int total) || total < 0)
                throw new InvalidOperationException("VK returned an incomplete listing; existing cached data was preserved.");
            int count = items.GetArrayLength();
            if (count == 0)
            {
                if (offset < total) throw new InvalidOperationException("VK ended pagination early; existing cached data was preserved.");
                return result;
            }
            int added = 0;
            foreach (var item in items.EnumerateArray())
            {
                string id = $"{JsonValue.Long(item, "owner_id")}:{JsonValue.Long(item, "id")}";
                if (seen.Add(id)) { result.Add(item.Clone()); added++; }
            }
            if (added == 0) throw new InvalidOperationException("VK repeated a page; existing cached data was preserved.");
            offset += count;
            if (offset >= total) return result;
        }
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}

internal static class JsonValue
{
    public static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    public static long Long(JsonElement item, string name) => item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out long n) ? n : 0;
    public static int Int(JsonElement item, string name) => (int)Math.Clamp(Long(item, name), int.MinValue, int.MaxValue);
    public static string? Image(JsonElement item)
    {
        var image = SelectImage(item);
        return image.HasValue ? Text(image.Value, "url") : null;
    }
    public static double? ImageAspectRatio(JsonElement item)
    {
        var image = SelectImage(item);
        if (!image.HasValue) return null;
        long width = Long(image.Value, "width"), height = Long(image.Value, "height");
        return width > 0 && height > 0 ? (double)width / height : null;
    }
    private static JsonElement? SelectImage(JsonElement item)
    {
        if (!item.TryGetProperty("image", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        foreach (var image in images.EnumerateArray().OrderByDescending(i => Long(i, "width")))
            if (IsHttpUrl(Text(image, "url"))) return image;
        return null;
    }
    public static bool IsHttpUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    public static string Scope(PluginConfiguration c) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{c.OwnerId}:{c.AccessToken}")))[..20];
}
