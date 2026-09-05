using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.VkVideos;

// Jellyfin returns ratio 0 for a remote thumbnail until its first image request.
// Supply VK's actual thumbnail dimensions before serialization so the first render
// chooses the same card shape as subsequent renders. No web-client or theme edits.
public sealed class VkThumbnailAspectRatioFilter(VkCatalog catalog, ILibraryManager library) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult result)
        {
            IEnumerable<BaseItemDto> items = result.Value switch
            {
                QueryResult<BaseItemDto> query => query.Items,
                BaseItemDto item => [item],
                IEnumerable<BaseItemDto> list => list,
                _ => []
            };
            foreach (var dto in items)
            {
                if (dto.ChannelName != "VK Videos" || dto.IsFolder == true || dto.PrimaryImageAspectRatio > 0) continue;
                var item = library.GetItemById(dto.Id);
                if (item?.ExternalId is not { } externalId || !externalId.StartsWith("video:", StringComparison.Ordinal)) continue;
                var ratio = await catalog.GetCachedThumbnailRatioAsync(externalId, context.HttpContext.RequestAborted).ConfigureAwait(false);
                if (ratio.HasValue) dto.PrimaryImageAspectRatio = ratio.Value;
            }
        }
        await next().ConfigureAwait(false);
    }
}
