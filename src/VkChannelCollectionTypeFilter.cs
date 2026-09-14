using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.VkVideos;

// Jellyfin lists a channel among the user's libraries as Type "Channel" with no CollectionType,
// and sends the channel's own folders as "ChannelFolderItem", so clients that only open known
// library kinds draw those tiles but cannot open them. Report VK Videos and its playlists as
// "folders" libraries: their types are unchanged, and jellyfin-web opens them on the same
// list pages as before.
public sealed class VkChannelCollectionTypeFilter : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult result) return;
        IEnumerable<BaseItemDto> items = result.Value switch
        {
            QueryResult<BaseItemDto> query => query.Items,
            BaseItemDto item => [item],
            IEnumerable<BaseItemDto> list => list,
            _ => []
        };
        foreach (var dto in items)
        {
            if (dto.CollectionType is not null) continue;
            bool channel = dto.Type == BaseItemKind.Channel && dto.Name == "VK Videos";
            bool playlist = dto.Type == BaseItemKind.ChannelFolderItem && dto.ChannelName == "VK Videos";
            if (channel || playlist) dto.CollectionType = CollectionType.folders;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
