using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.VkVideos;

// Jellyfin lists a channel among the user's libraries as Type "Channel" with no CollectionType,
// so clients that only open known library kinds draw the tile but cannot open it.
// Report VK Videos as a "folders" library: it stays a Channel, and jellyfin-web opens a
// folder-typed channel on the same list page as before.
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
            if (dto.Type == BaseItemKind.Channel && dto.Name == "VK Videos" && dto.CollectionType is null)
                dto.CollectionType = CollectionType.folders;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
