using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.VkVideos;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class PluginTests
{
    private static PluginConfiguration Config => new() { OwnerId = -123, AccessToken = "test-secret-never-log" };
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
    private static Dictionary<string, string> Parameters => new() { ["owner_id"] = "-123", ["count"] = "200" };

    [Fact]
    public async Task PaginationContinuesBeyondFirstTwoHundred()
    {
        var handler = new FakeHandler((n, _) => n == 0
            ? JsonSerializer.Serialize(new { response = new { count = 203, items = Enumerable.Range(1, 200).Select(id => new { id, owner_id = -123 }) } })
            : JsonSerializer.Serialize(new { response = new { count = 203, items = Enumerable.Range(201, 3).Select(id => new { id, owner_id = -123 }) } }));
        using var api = new VkApi(handler);
        var items = await api.ListAsync("video.get", Parameters, Config, default);
        Assert.Equal(203, items.Count);
        Assert.Contains("offset=200", handler.Bodies[1]);
        Assert.All(handler.Uris, u => Assert.DoesNotContain("access_token", u));
    }

    [Fact]
    public async Task UnexpectedEmptyPageFailsInsteadOfPublishingPartialListing()
    {
        using var api = new VkApi(new FakeHandler((n, _) => n == 0
            ? "{\"response\":{\"count\":3,\"items\":[{\"id\":1}]}}"
            : "{\"response\":{\"count\":3,\"items\":[]}}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.ListAsync("video.get", Parameters, Config, default));
    }

    [Fact]
    public async Task RepeatedPageCannotLoopForever()
    {
        var handler = new FakeHandler((_, _) => "{\"response\":{\"count\":20,\"items\":[{\"id\":1}]}}");
        using var api = new VkApi(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.ListAsync("video.get", Parameters, Config, default));
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task TransientErrorRetriesTheSameOffset()
    {
        var handler = new FakeHandler((n, _) => n == 0
            ? "{\"error\":{\"error_code\":6}}"
            : "{\"response\":{\"count\":1,\"items\":[{\"id\":1}]}}");
        using var api = new VkApi(handler);
        Assert.Single(await api.ListAsync("video.get", Parameters, Config, default));
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }

    [Fact]
    public async Task VkErrorsDoNotExposeEchoedTokens()
    {
        using var api = new VkApi(new FakeHandler((_, _) => "{\"error\":{\"error_code\":5,\"error_msg\":\"test-secret-never-log\"}}"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => api.CallAsync("video.get", Parameters, Config, default));
        Assert.DoesNotContain(Config.AccessToken, ex.ToString());
        Assert.Contains("error 5", ex.Message);
    }

    [Fact]
    public async Task MutationMethodsAreRejectedBeforeNetworkAccess()
    {
        var handler = new FakeHandler((_, _) => "{}");
        using var api = new VkApi(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => api.CallAsync("video.delete", Parameters, Config, default));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(2160, "https://cdn.example/2160.mp4")]
    [InlineData(1080, "https://cdn.example/720.mp4")]
    [InlineData(240, "https://cdn.example/720.mp4")]
    public void QualityPreferenceAndNearestFallback(int cap, string expected)
    {
        var selected = VkChannel.SelectStream(Json("{\"mp4_2160\":\"https://cdn.example/2160.mp4\",\"mp4_720\":\"https://cdn.example/720.mp4\"}"), cap);
        Assert.Equal(expected, selected.Url);
    }

    [Fact]
    public void HlsFallbackAndInvalidUrls()
    {
        Assert.Equal("hls", VkChannel.SelectStream(Json("{\"hls\":\"https://cdn.example/video.m3u8\"}"), 2160).Container);
        Assert.Throws<InvalidOperationException>(() => VkChannel.SelectStream(Json("{\"mp4_2160\":\"file:///private.mp4\"}"), 2160));
    }

    [Fact]
    public async Task FailedRefreshPreservesLastCompleteCache()
    {
        using var fixture = new PluginFixture();
        var handler = new FakeHandler((n, _) => n == 0
            ? "{\"response\":{\"count\":1,\"items\":[{\"id\":7,\"title\":\"Keep me\"}]}}"
            : "{\"error\":{\"error_code\":5}}");
        using var api = new VkApi(handler);
        var catalog = new VkCatalog(api, NullLogger<VkCatalog>.Instance);
        await catalog.AlbumsAsync(default);
        string path = Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories).Single();
        string before = File.ReadAllText(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.AlbumsAsync(default, true));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Single(await catalog.AlbumsAsync(default));
    }

    [Fact]
    public async Task AlbumMembershipIdsDoNotCollideAndActualVideoOwnerIsPreserved()
    {
        using var fixture = new PluginFixture();
        using var api = new VkApi(new FakeHandler((_, _) => "{\"response\":{\"count\":1,\"items\":[{\"id\":9,\"owner_id\":42,\"title\":\"Shared video\",\"duration\":null,\"date\":null}]}}"));
        var channel = new VkChannel(new VkCatalog(api, NullLogger<VkCatalog>.Instance), api, Mock.Of<IMediaEncoder>());
        var first = await channel.GetChannelItems(new() { FolderId = "album:-123:7" }, default);
        var second = await channel.GetChannelItems(new() { FolderId = "album:-123:8" }, default);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.EndsWith(":42:9", first.Items[0].Id);
        Assert.Equal(0, first.Items[0].RunTimeTicks);
    }

    [Fact]
    public void UserAllowlistAndDisabledSourceAreEnforced()
    {
        using var fixture = new PluginFixture();
        using var api = new VkApi(new FakeHandler((_, _) => "{}"));
        var channel = new VkChannel(new VkCatalog(api, NullLogger<VkCatalog>.Instance), api, Mock.Of<IMediaEncoder>());
        var allowed = Guid.NewGuid();
        Plugin.Instance.Configuration.AllowedUserIds = [allowed.ToString("D")];
        Assert.True(channel.IsEnabledFor(allowed.ToString("N")));
        Assert.False(channel.IsEnabledFor(Guid.NewGuid().ToString()));
        Plugin.Instance.Configuration.Enabled = false;
        Assert.False(channel.IsEnabledFor(allowed.ToString()));
    }

    [Fact]
    public void ChangingCredentialsOrSourceInvalidatesListingCacheKey()
    {
        using var fixture = new PluginFixture();
        using var api = new VkApi(new FakeHandler((_, _) => "{}"));
        var catalog = new VkCatalog(api, NullLogger<VkCatalog>.Instance);
        string before = catalog.CacheKey;
        Plugin.Instance.Configuration.OwnerId = -456;
        Assert.NotEqual(before, catalog.CacheKey);
        before = catalog.CacheKey;
        Plugin.Instance.Configuration.AccessToken = "other-token";
        Assert.NotEqual(before, catalog.CacheKey);
        before = catalog.CacheKey;
        Plugin.Instance.Configuration.ShowAllVideos = true;
        Assert.NotEqual(before, catalog.CacheKey);
    }

    [Fact]
    public async Task PlaybackUsesGuidSourceIdProbedCodecsAndRequiredHeaders()
    {
        using var fixture = new PluginFixture();
        using var api = new VkApi(new FakeHandler((_, _) => "{\"response\":{\"items\":[{\"duration\":60,\"files\":{\"mp4_1080\":\"https://cdn.example/1080.mp4\"}}]}}"));
        var encoder = new Mock<IMediaEncoder>();
        encoder.Setup(x => x.GetMediaInfo(It.Is<MediaInfoRequest>(r => r.MediaSource.RequiredHttpHeaders["User-Agent"] == "okhttp/4.9.0"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaInfo { MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080 }], RunTimeTicks = 600000000 });
        var channel = new VkChannel(new VkCatalog(api, NullLogger<VkCatalog>.Instance), api, encoder.Object);
        var sources = await channel.GetChannelItemMediaInfo("video:-123:7:-123:9", default);
        var source = Assert.Single(sources);
        Assert.True(Guid.TryParse(source.Id, out _));
        Assert.False(source.SupportsDirectPlay);
        Assert.Equal("h264", Assert.Single(source.MediaStreams).Codec);
        Assert.Equal(600000000, source.RunTimeTicks);
    }

    [Fact]
    public async Task ThumbnailRatioUsesSelectedImageDimensionsAndSurvivesColdMemoryCache()
    {
        using var fixture = new PluginFixture();
        var handler = new FakeHandler((_, _) => """
            {"response":{"count":1,"items":[{"id":9,"owner_id":42,"width":1080,"height":1920,
            "image":[{"url":"https://cdn.example/small.jpg","width":320,"height":180},
            {"url":"https://cdn.example/large.jpg","width":1280,"height":720}]}]}}
            """);
        using var api = new VkApi(handler);
        var catalog = new VkCatalog(api, NullLogger<VkCatalog>.Instance);
        await catalog.VideosAsync(7, default);
        // Simulate a plugin restart while Jellyfin still has its item listing cached.
        catalog = new VkCatalog(api, NullLogger<VkCatalog>.Instance);
        Assert.Equal(16.0 / 9, await catalog.GetCachedThumbnailRatioAsync("video:-123:7:42:9", default));
        Assert.Null(await catalog.GetCachedThumbnailRatioAsync("video:-456:7:42:9", default));
        Assert.Null(await catalog.GetCachedThumbnailRatioAsync("video:-123:7:43:9", default));
        Assert.Null(await catalog.GetCachedThumbnailRatioAsync("video:-123:../other:42:9", default));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task ThumbnailFilterFixesOnlyMissingVkRatiosBeforeSerialization()
    {
        using var fixture = new PluginFixture();
        using var api = new VkApi(new FakeHandler((_, _) => """
            {"response":{"count":1,"items":[{"id":9,"owner_id":-123,
            "image":[{"url":"https://cdn.example/thumbnail.jpg","width":1280,"height":720}]}]}}
            """));
        var catalog = new VkCatalog(api, NullLogger<VkCatalog>.Instance);
        await catalog.VideosAsync(7, default);
        var id = Guid.NewGuid();
        var library = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        library.Setup(x => x.GetItemById(id)).Returns(new MediaBrowser.Controller.Entities.Video { ExternalId = "video:-123:7:-123:9" });
        var missing = new MediaBrowser.Model.Dto.BaseItemDto { Id = id, ChannelName = "VK Videos", IsFolder = false, PrimaryImageAspectRatio = 0 };
        var known = new MediaBrowser.Model.Dto.BaseItemDto { Id = id, ChannelName = "VK Videos", IsFolder = false, PrimaryImageAspectRatio = 0.75 };
        var unrelated = new MediaBrowser.Model.Dto.BaseItemDto { Id = id, ChannelName = "Other channel", IsFolder = false };
        var folder = new MediaBrowser.Model.Dto.BaseItemDto { Id = id, ChannelName = "VK Videos", IsFolder = true };
        var result = new Microsoft.AspNetCore.Mvc.ObjectResult(new MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Model.Dto.BaseItemDto>
            { Items = [missing, known, unrelated, folder] });
        var action = new Microsoft.AspNetCore.Mvc.ActionContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var context = new Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext(action, [], result, new object());
        bool serialized = false;
        await new VkThumbnailAspectRatioFilter(catalog, library.Object).OnResultExecutionAsync(context, () =>
        {
            Assert.Equal(16.0 / 9, missing.PrimaryImageAspectRatio);
            serialized = true;
            return Task.FromResult(new Microsoft.AspNetCore.Mvc.Filters.ResultExecutedContext(action, [], result, new object()));
        });
        Assert.True(serialized);
        Assert.Equal(0.75, known.PrimaryImageAspectRatio);
        Assert.Null(unrelated.PrimaryImageAspectRatio);
        Assert.Null(folder.PrimaryImageAspectRatio);
        library.Verify(x => x.GetItemById(id), Times.Once);
    }

    [Fact]
    public void ChannelFilterReportsOnlyTheVkChannelAsAFoldersLibrary()
    {
        var vk = new MediaBrowser.Model.Dto.BaseItemDto { Type = Jellyfin.Data.Enums.BaseItemKind.Channel, Name = "VK Videos", IsFolder = true };
        var single = new MediaBrowser.Model.Dto.BaseItemDto { Type = Jellyfin.Data.Enums.BaseItemKind.Channel, Name = "VK Videos", IsFolder = true };
        var other = new MediaBrowser.Model.Dto.BaseItemDto { Type = Jellyfin.Data.Enums.BaseItemKind.Channel, Name = "Other channel", IsFolder = true };
        var library = new MediaBrowser.Model.Dto.BaseItemDto { Type = Jellyfin.Data.Enums.BaseItemKind.CollectionFolder, Name = "VK Videos",
            CollectionType = Jellyfin.Data.Enums.CollectionType.movies };
        var playlist = new MediaBrowser.Model.Dto.BaseItemDto { Type = Jellyfin.Data.Enums.BaseItemKind.Folder, Name = "VK Videos", ChannelName = "VK Videos", IsFolder = true };
        var action = new Microsoft.AspNetCore.Mvc.ActionContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var filter = new VkChannelCollectionTypeFilter();
        var views = new Microsoft.AspNetCore.Mvc.ObjectResult(new MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Model.Dto.BaseItemDto>
            { Items = [vk, other, library, playlist] });
        filter.OnResultExecuting(new Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext(action, [], views, new object()));
        filter.OnResultExecuting(new Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext(action, [],
            new Microsoft.AspNetCore.Mvc.ObjectResult(single), new object()));
        Assert.Equal(Jellyfin.Data.Enums.CollectionType.folders, vk.CollectionType);
        Assert.Equal(Jellyfin.Data.Enums.BaseItemKind.Channel, vk.Type);
        Assert.Equal(Jellyfin.Data.Enums.CollectionType.folders, single.CollectionType);
        Assert.Null(other.CollectionType);
        Assert.Equal(Jellyfin.Data.Enums.CollectionType.movies, library.CollectionType);
        Assert.Null(playlist.CollectionType);
    }

    [Fact]
    public async Task ThumbnailLookupWithoutMetadataNeverMakesNetworkRequests()
    {
        using var fixture = new PluginFixture();
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected request"));
        using var api = new VkApi(handler);
        var catalog = new VkCatalog(api, NullLogger<VkCatalog>.Instance);
        Assert.Null(await catalog.GetCachedThumbnailRatioAsync("video:-123:7:-123:9", default));
        Assert.Empty(handler.Bodies);
    }

    private sealed class FakeHandler(Func<int, string, string> response) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            int n = Bodies.Count; Bodies.Add(body); Uris.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(n, body), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class PluginFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vk-plugin-tests-" + Guid.NewGuid().ToString("N"));
        public PluginFixture()
        {
            Directory.CreateDirectory(Root);
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(x => x.DataPath).Returns(Root);
            paths.SetupGet(x => x.PluginsPath).Returns(Root);
            paths.SetupGet(x => x.PluginConfigurationsPath).Returns(Root);
            var plugin = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());
            plugin.UpdateConfiguration(Config);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
