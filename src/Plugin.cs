using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.VkVideos;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
        CacheDirectory = Path.Combine(paths.DataPath, "vk-videos");
    }

    public static Plugin Instance { get; private set; } = null!;
    public string CacheDirectory { get; }
    public override string Name => "VK Videos";
    public override string Description => "Browse and stream your VK video playlists inside Jellyfin.";
    public override Guid Id => Guid.Parse("e4f461e0-710b-4b2d-b162-09cd1ed0a99e");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [new() { Name = "vk-videos", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }];
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public long OwnerId { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public int RefreshMinutes { get; set; } = 15;
    public int MaximumHeight { get; set; } = 2160;
    public bool ShowAllVideos { get; set; }
    public string[] AllowedUserIds { get; set; } = [];
}
