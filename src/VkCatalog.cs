using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VkVideos;

public sealed class VkCatalog(VkApi api, ILogger<VkCatalog> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _memory = new();
    private readonly ConditionalWeakTable<CacheEntry, Dictionary<(long Owner, long Id), double>> _imageRatios = new();
    private long _generation;
    public string CacheKey => $"{JsonValue.Scope(Plugin.Instance.Configuration)}-{Plugin.Instance.Configuration.ShowAllVideos}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (60 * Math.Clamp(Plugin.Instance.Configuration.RefreshMinutes, 1, 1440))}-{Interlocked.Read(ref _generation)}";

    public Task<List<JsonElement>> AlbumsAsync(CancellationToken ct, bool force = false) => LoadAsync("albums", null, ct, force);
    public Task<List<JsonElement>> VideosAsync(long? albumId, CancellationToken ct, bool force = false) => LoadAsync(albumId is null ? "all" : $"album-{albumId}", albumId, ct, force);

    // Read only the existing catalog, including after a restart where Jellyfin already has
    // the channel listing cached. Thumbnail layout must never trigger a VK API request.
    public async Task<double?> GetCachedThumbnailRatioAsync(string externalId, CancellationToken ct)
    {
        var parts = externalId.Split(':');
        var config = Plugin.Instance.Configuration;
        if (parts.Length != 5 || parts[0] != "video" || !config.Enabled ||
            !long.TryParse(parts[1], out long sourceOwner) || sourceOwner != config.OwnerId ||
            !long.TryParse(parts[3], out long owner) || !long.TryParse(parts[4], out long id)) return null;
        string listing;
        if (parts[2] == "all") listing = "all";
        else if (long.TryParse(parts[2], out long album)) listing = $"album-{album}";
        else return null;
        string key = JsonValue.Scope(config) + "-" + listing;
        if (!_memory.TryGetValue(key, out var cached))
        {
            string path = Path.Combine(Plugin.Instance.CacheDirectory, key + ".json");
            try
            {
                cached = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
                if (cached is null) return null;
                cached = _memory.GetOrAdd(key, cached);
            }
            catch (IOException) { return null; }
            catch (JsonException) { return null; }
        }
        var ratios = _imageRatios.GetValue(cached, entry =>
        {
            var result = new Dictionary<(long, long), double>();
            foreach (var video in entry.Items)
            {
                var ratio = JsonValue.ImageAspectRatio(video);
                if (!ratio.HasValue) continue;
                long videoOwner = JsonValue.Long(video, "owner_id");
                result[(videoOwner == 0 ? sourceOwner : videoOwner, JsonValue.Long(video, "id"))] = ratio.Value;
            }
            return result;
        });
        return ratios.TryGetValue((owner, id), out double value) ? value : null;
    }

    private async Task<List<JsonElement>> LoadAsync(string name, long? albumId, CancellationToken ct, bool force)
    {
        // Capture a snapshot so settings changes never mix credentials and owner IDs mid-request.
        var c = Plugin.Instance.Configuration;
        var config = new PluginConfiguration { OwnerId = c.OwnerId, AccessToken = c.AccessToken, RefreshMinutes = c.RefreshMinutes };
        if (!c.Enabled) return [];
        string key = JsonValue.Scope(config) + "-" + name;
        string path = Path.Combine(Plugin.Instance.CacheDirectory, key + ".json");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_memory.TryGetValue(key, out var cached) && File.Exists(path))
            {
                try { cached = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)); }
                catch (JsonException) { logger.LogWarning("Ignoring an invalid VK Videos cache file."); }
                catch (IOException) { logger.LogWarning("Could not read a VK Videos cache file."); }
                if (cached is not null) _memory[key] = cached;
            }
            if (!force && cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(Math.Clamp(config.RefreshMinutes, 1, 1440)))
                return cached.Items;
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    ["owner_id"] = config.OwnerId.ToString(CultureInfo.InvariantCulture),
                    ["count"] = name == "albums" ? "100" : "200", ["extended"] = "1"
                };
                if (albumId.HasValue) parameters["album_id"] = albumId.Value.ToString(CultureInfo.InvariantCulture);
                var items = await api.ListAsync(name == "albums" ? "video.getAlbums" : "video.get", parameters, config, ct).ConfigureAwait(false);
                var next = new CacheEntry { FetchedAt = DateTimeOffset.UtcNow, Items = items };
                Directory.CreateDirectory(Plugin.Instance.CacheDirectory);
                string tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(next), ct).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                _memory[key] = next;
                return items;
            }
            catch (Exception ex) when (!force && cached is not null && ex is not OperationCanceledException)
            {
                logger.LogWarning("VK Videos could not refresh {Listing}; showing the last complete cached listing. {Reason}", name, ex.Message);
                return cached.Items;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync(IProgress<double> progress, CancellationToken ct)
    {
        if (!Plugin.Instance.Configuration.Enabled) return;
        var albums = await AlbumsAsync(ct, true).ConfigureAwait(false);
        progress.Report(5);
        for (int i = 0; i < albums.Count; i++)
        {
            await VideosAsync(JsonValue.Long(albums[i], "id"), ct, true).ConfigureAwait(false);
            progress.Report(5 + 90.0 * (i + 1) / Math.Max(1, albums.Count));
        }
        if (Plugin.Instance.Configuration.ShowAllVideos) await VideosAsync(null, ct, true).ConfigureAwait(false);
        Interlocked.Increment(ref _generation);
        progress.Report(100);
    }

    public sealed class CacheEntry
    {
        public DateTimeOffset FetchedAt { get; set; }
        public List<JsonElement> Items { get; set; } = [];
    }
}
