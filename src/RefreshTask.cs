using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VkVideos;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<VkApi>();
        services.AddSingleton<VkCatalog>();
        services.AddSingleton<IChannel, VkChannel>();
        services.AddScoped<VkThumbnailAspectRatioFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<VkThumbnailAspectRatioFilter>());
    }
}

public sealed class RefreshTask(VkCatalog catalog) : IScheduledTask
{
    public string Name => "Refresh VK Videos";
    public string Key => "RefreshVkVideos";
    public string Description => "Refresh all VK playlists and their videos. Failed listings keep their previous cache.";
    public string Category => "VK Videos";
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => catalog.RefreshAsync(progress, cancellationToken);
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }];
}
