using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.VkVideos;

public sealed class VkChannel(VkCatalog catalog, VkApi api, IMediaEncoder encoder) : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    public string Name => "VK Videos";
    public string Description => "Your VK playlists and categories.";
    public string DataVersion => "1";
    public string HomePageUrl => "https://vk.com/video";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId)
    {
        var config = Plugin.Instance.Configuration;
        return config.Enabled && config.OwnerId != 0 && !string.IsNullOrWhiteSpace(config.AccessToken) &&
            (config.AllowedUserIds.Length == 0 || config.AllowedUserIds.Any(id =>
                Guid.TryParse(id, out var allowed) && Guid.TryParse(userId, out var current) && allowed == current));
    }
    public string? GetCacheKey(string? userId) => catalog.CacheKey;
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Clip],
        DefaultSortFields = [ChannelItemSortField.Name, ChannelItemSortField.DateCreated, ChannelItemSortField.Runtime],
        SupportsSortOrderToggle = true, AutoRefreshLevels = 2, SupportsContentDownloading = false
    };

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        // Background channel refreshes use Guid.Empty; interactive access is checked by Jellyfin and here.
        if (query.UserId != Guid.Empty && !IsEnabledFor(query.UserId.ToString())) return new ChannelItemResult();
        var config = Plugin.Instance.Configuration;
        var items = new List<ChannelItemInfo>();
        if (string.IsNullOrEmpty(query.FolderId))
        {
            var albums = await catalog.AlbumsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var a in albums)
            {
                items.Add(new ChannelItemInfo
                {
                    Id = $"album:{config.OwnerId}:{JsonValue.Long(a, "id")}", Name = JsonValue.Text(a, "title"),
                    Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container,
                    ImageUrl = JsonValue.Image(a), Overview = $"VK playlist · {JsonValue.Int(a, "count")} videos"
                });
            }
            if (config.ShowAllVideos) items.Insert(0, new ChannelItemInfo { Id = $"all:{config.OwnerId}", Name = "All Videos", Type = ChannelItemType.Folder });
        }
        else
        {
            var parts = query.FolderId.Split(':');
            if (parts.Length < 2 || !long.TryParse(parts[1], out var owner) || owner != config.OwnerId)
                throw new ArgumentException("This folder belongs to a different VK source.");
            long? album;
            if (parts.Length == 2 && parts[0] == "all" && config.ShowAllVideos) album = null;
            else if (parts.Length == 3 && parts[0] == "album" && long.TryParse(parts[2], out long id)) album = id;
            else throw new ArgumentException("Invalid VK folder ID.");
            foreach (var v in await catalog.VideosAsync(album, cancellationToken).ConfigureAwait(false))
                items.Add(ToVideo(v, config.OwnerId, album));
        }
        IEnumerable<ChannelItemInfo> sorted = query.SortBy switch
        {
            ChannelItemSortField.DateCreated => items.OrderBy(x => x.DateCreated),
            ChannelItemSortField.Runtime => items.OrderBy(x => x.RunTimeTicks),
            _ => items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        };
        if (query.SortDescending) sorted = sorted.Reverse();
        int total = items.Count;
        sorted = sorted.Skip(Math.Max(0, query.StartIndex ?? 0));
        if (query.Limit.HasValue) sorted = sorted.Take(Math.Max(0, query.Limit.Value));
        return new ChannelItemResult { Items = sorted.ToList(), TotalRecordCount = total };
    }

    private static ChannelItemInfo ToVideo(JsonElement video, long sourceOwner, long? albumId)
    {
        long owner = JsonValue.Long(video, "owner_id");
        if (owner == 0) owner = sourceOwner;
        long id = JsonValue.Long(video, "id");
        long date = JsonValue.Long(video, "date");
        return new ChannelItemInfo
        {
            Id = $"video:{sourceOwner}:{(albumId.HasValue ? albumId.Value.ToString(CultureInfo.InvariantCulture) : "all")}:{owner}:{id}",
            Name = JsonValue.Text(video, "title"), Overview = JsonValue.Text(video, "description"),
            Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Clip,
            ImageUrl = JsonValue.Image(video), RunTimeTicks = Math.Max(0, JsonValue.Long(video, "duration")) * TimeSpan.TicksPerSecond,
            DateCreated = date > 0 && date <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(date).UtcDateTime : null,
            HomePageUrl = $"https://vk.com/video{owner}_{id}"
        };
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var parts = id.Split(':');
        if (!config.Enabled || parts.Length != 5 || parts[0] != "video" ||
            !long.TryParse(parts[1], out long sourceOwner) || sourceOwner != config.OwnerId ||
            !long.TryParse(parts[3], out long owner) || !long.TryParse(parts[4], out long videoId))
            throw new ArgumentException("Invalid VK video ID or source disabled.");
        var data = await api.CallAsync("video.get", new Dictionary<string, string>
        {
            ["videos"] = $"{owner}_{videoId}", ["owner_id"] = owner.ToString(CultureInfo.InvariantCulture), ["count"] = "1"
        }, config, cancellationToken).ConfigureAwait(false);
        if (!data.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            throw new InvalidOperationException("This VK video was removed or is no longer accessible to the configured account.");
        var video = items[0];
        if (!video.TryGetProperty("files", out var files))
            throw new InvalidOperationException("VK did not provide a playable stream for this video. Check access permissions.");
        var selected = SelectStream(files, config.MaximumHeight);
        var source = new MediaSourceInfo
        {
            // Jellyfin's HLS/trickplay path parses source IDs as GUIDs.
            Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 16)).ToString("N"),
            Name = selected.Name, Path = selected.Url, Protocol = MediaProtocol.Http,
            IsRemote = true, Container = selected.Container, RunTimeTicks = Math.Max(0, JsonValue.Long(video, "duration")) * TimeSpan.TicksPerSecond,
            // Keep playback through Jellyfin so clients do not need VK credentials, headers or CDN access.
            SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = true, SupportsProbing = true,
            RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = "okhttp/4.9.0" }
        };
        var info = await encoder.GetMediaInfo(new MediaInfoRequest
        {
            MediaSource = source, MediaType = DlnaProfileType.Video, ExtractChapters = false
        }, cancellationToken).ConfigureAwait(false);
        source.MediaStreams = info.MediaStreams;
        source.RunTimeTicks = info.RunTimeTicks ?? source.RunTimeTicks;
        source.Size = info.Size;
        source.Bitrate = info.Bitrate;
        return [source];
    }

    public static (string Url, string Name, string Container) SelectStream(JsonElement files, int maxHeight)
    {
        int[] heights = [2160, 1440, 1080, 720, 480, 360, 240];
        int cap = Math.Clamp(maxHeight, 240, 2160);
        foreach (int height in heights.Where(h => h <= cap).Concat(heights.Where(h => h > cap).Reverse()))
        {
            string url = JsonValue.Text(files, "mp4_" + height);
            if (JsonValue.IsHttpUrl(url)) return (url, $"VK · {height}p", "mp4");
        }
        foreach (string key in new[] { "hls", "hls_ondemand" })
        {
            string url = JsonValue.Text(files, key);
            if (JsonValue.IsHttpUrl(url)) return (url, "VK · Adaptive", "hls");
        }
        throw new InvalidOperationException("VK returned no supported MP4 or HLS stream for this video.");
    }

    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        Task.FromResult(type == ImageType.Primary
            ? new DynamicImageResponse { HasImage = true, Format = ImageFormat.Png, Stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.VkVideos.Assets.channel.png") }
            : new DynamicImageResponse { HasImage = false });
}
