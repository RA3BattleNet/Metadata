using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 图片读取、ETag 后台校验与会话级磁盘缓存开关的对外契约。
/// 这些行为决定客户端能否在断网/慢网下先显示旧图、再悄悄换成新图，因此在这里断言。
/// </summary>
[TestClass]
public class MetadataClientImageTests
{
    private const string RootUrl = "https://metadata.example/metadata.xml";
    private const string IconUrl = "https://metadata.example/images/icon.png";

    [TestMethod]
    public async Task DisabledDiskCache_NeverCreatesOrReadsCacheDirectory()
    {
        var cache = NewCache();
        using var fixture = Fixture.Create(cache, enableDiskCache: false);
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == IconUrl ? Png(IconBytes) : Xml(RootXml()));

        var opened = await fixture.Client.OpenSnapshotAsync(RootUrl);
        opened.Status.Should().Be(MetadataFreshness.Unavailable);
        fixture.Calls.Should().BeEmpty();

        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        root.Status.Should().Be(MetadataFreshness.Fresh);

        var image = await fixture.Client.GetImageAsync(root, "icon");
        image.Status.Should().Be(MetadataFreshness.Fresh);
        image.Bytes.Should().Equal(IconBytes);
        image.ContentType.Should().Be("image/png");

        Directory.Exists(cache).Should().BeFalse("禁用缓存的会话不创建缓存目录");
        fixture.Calls.Select(call => call.Conditional).Should().OnlyContain(value => !value);
    }

    [TestMethod]
    public async Task DisabledDiskCache_SecondImageRequestReadsSourceAgain()
    {
        using var fixture = Fixture.Create(NewCache(), enableDiskCache: false);
        var body = IconBytes;
        fixture.Handler.Next = (_, _) => Task.FromResult(Xml(RootXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == IconUrl ? Png(body) : Xml(RootXml()));

        (await fixture.Client.GetImageAsync(root, "icon")).Status.Should().Be(MetadataFreshness.Fresh);
        (await fixture.Client.GetImageAsync(root, "icon")).Status.Should().Be(MetadataFreshness.Fresh);

        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(2, "禁用缓存时不复用上一次的正文");
    }

    [TestMethod]
    public async Task Image_RelativeSource_IsStoredInReleaseTreeWithContentType()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");

        var image = await fixture.Client.GetImageAsync(root, "icon");

        image.Status.Should().Be(MetadataFreshness.Fresh);
        var body = Directory.EnumerateFiles(fixture.Cache, "icon.png", SearchOption.AllDirectories).Single();
        Path.GetRelativePath(fixture.Cache, body).Replace('\\', '/').Should().Be("images/icon.png");
        File.ReadAllText(body + ".etag").Should().Contain("\"ContentType\":\"image/png\"");
    }

    [TestMethod]
    public async Task Image_CacheHit_ReturnsImmediatelyThenValidatesWithETag()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        await fixture.Client.GetImageAsync(root, "icon");

        var updated = 0;
        fixture.Client.ImageUpdated += (_, _) => Interlocked.Increment(ref updated);
        fixture.Handler.Next = (request, _) =>
        {
            request.Headers.IfNoneMatch.ToString().Should().Be("\"icon-1\"");
            return Task.FromResult(NotModified());
        };

        var cached = await fixture.Client.GetImageAsync(root, "icon");

        cached.Status.Should().Be(MetadataFreshness.Stale);
        cached.Bytes.Should().Equal(IconBytes);
        (await WaitUntilAsync(() => fixture.Calls.Count(call => call.Uri == IconUrl) >= 2)).Should().BeTrue();
        fixture.Calls.Last().Conditional.Should().BeTrue();
        Interlocked.CompareExchange(ref updated, 0, 0).Should().Be(0, "304 不产生内容更新");
    }

    [TestMethod]
    public async Task Image_BackgroundChangedBytes_UpdatesCacheAndRaisesOnce()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        await fixture.Client.GetImageAsync(root, "icon");

        var changed = new byte[] { 9, 9, 9, 9 };
        var raised = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.ImageUpdated += (_, args) =>
        {
            args.ImageUri.Should().Be(IconUrl);
            if (Interlocked.Increment(ref raised) == 1)
                done.TrySetResult();
        };
        fixture.Handler.Next = (_, _) => Task.FromResult(Png(changed, "\"icon-2\""));

        var cached = await fixture.Client.GetImageAsync(root, "icon");
        cached.Status.Should().Be(MetadataFreshness.Stale);
        cached.Bytes.Should().Equal(IconBytes, "前台先拿到旧图");

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Client.ImageRevision(root, "icon").Should().Be(1);
        var body = Directory.EnumerateFiles(fixture.Cache, "icon.png", SearchOption.AllDirectories).Single();
        File.ReadAllBytes(body).Should().Equal(changed);

        // 内容已经是最新的，再访问一次只应拿到 304，不再发内容更新。
        fixture.Handler.Next = (_, _) => Task.FromResult(NotModified());
        (await fixture.Client.GetImageAsync(root, "icon")).Bytes.Should().Equal(changed);
        (await WaitUntilAsync(() => fixture.Calls.Count(call => call.Uri == IconUrl) >= 3)).Should().BeTrue();
        await Task.Delay(150);
        Interlocked.CompareExchange(ref raised, 0, 0).Should().Be(1);
    }

    [TestMethod]
    public async Task Image_OfflineWithCache_KeepsLastImageAndDoesNotBlock()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        await fixture.Client.GetImageAsync(root, "icon");

        fixture.Handler.Next = (_, _) => throw new HttpRequestException("down");
        var cached = await fixture.Client.GetImageAsync(root, "icon");

        cached.Status.Should().Be(MetadataFreshness.Stale);
        cached.Bytes.Should().Equal(IconBytes);
        (await WaitUntilAsync(() => fixture.Calls.Count(call => call.Uri == IconUrl) >= 2)).Should().BeTrue();
        fixture.Client.ImageRevision(root, "icon").Should().Be(0);
    }

    [TestMethod]
    public async Task Image_QueryUrl_UsesRequestKeyAndKeepsQueriesApart()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == RootUrl
                ? Xml(RootXml("""
                    <Image ID="banner-a" Url="https://cdn.example/banner.png?v=1" />
                    <Image ID="banner-b" Url="https://cdn.example/banner.png?v=2" />
                    """))
                : Png([1, 2, 3, 4]));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);

        await fixture.Client.GetImageAsync(root, "banner-a");
        await fixture.Client.GetImageAsync(root, "banner-b");

        var bodies = Directory.EnumerateFiles(fixture.Cache, "body", SearchOption.AllDirectories).ToList();
        bodies.Should().HaveCount(2, "不同查询串不能撞到同一份正文");
        foreach (var body in bodies)
        {
            var key = Path.GetFileName(Path.GetDirectoryName(body)!);
            key.Should().MatchRegex("^[0-9a-f]{64}$");
            Path.GetRelativePath(fixture.Cache, body).Replace('\\', '/').Should().StartWith(".requests/");
        }
    }

    [TestMethod]
    public async Task Image_XmlHashIsIgnored()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == RootUrl
                ? Xml(RootXml("""<Image ID="icon" Source="images/icon.png" Hash="0000000000000000000000000000DEAD" />"""))
                : Png(IconBytes, "\"icon-1\""));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);

        var first = await fixture.Client.GetImageAsync(root, "icon");
        first.Bytes.Should().Equal(IconBytes, "XML 里的 Image Hash 不参与图片校验");

        fixture.Handler.Next = (_, _) => Task.FromResult(NotModified());
        var second = await fixture.Client.GetImageAsync(root, "icon");
        second.Status.Should().Be(MetadataFreshness.Stale);
        second.Bytes.Should().Equal(IconBytes);
    }

    [TestMethod]
    public async Task Image_NonImageResponse_IsRejectedAndNotCached()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == RootUrl
                ? Xml(RootXml())
                : Png(Encoding.UTF8.GetBytes("<html/>"), contentType: "text/html"));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);

        var image = await fixture.Client.GetImageAsync(root, "icon");

        image.Status.Should().Be(MetadataFreshness.Unavailable);
        Directory.EnumerateFiles(fixture.Cache, "icon.png", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [TestMethod]
    public async Task Image_CorruptCacheBody_IsRefetchedInsteadOfServed()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        await fixture.Client.GetImageAsync(root, "icon");
        var body = Directory.EnumerateFiles(fixture.Cache, "icon.png", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(body, [7, 7, 7]);

        fixture.Handler.Next = (_, _) => Task.FromResult(Png(IconBytes, "\"icon-1\""));
        var image = await fixture.Client.GetImageAsync(root, "icon");

        image.Status.Should().Be(MetadataFreshness.Fresh);
        image.Bytes.Should().Equal(IconBytes);
    }

    [TestMethod]
    public async Task Image_UnregisteredId_IsUnavailableWithoutRequest()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        var before = fixture.Calls.Count;

        var image = await fixture.Client.GetImageAsync(root, "mods/nope:icon");

        image.Status.Should().Be(MetadataFreshness.Unavailable);
        image.Error.Should().NotBeNullOrWhiteSpace();
        fixture.Calls.Should().HaveCount(before);
        fixture.Client.ImageRevision(root, "mods/nope:icon").Should().Be(0);
    }

    [TestMethod]
    public async Task Image_MissingDocument_IsUnavailable()
    {
        using var fixture = Fixture.Create();
        var snapshot = new RootSnapshotResult(null, null, null, MetadataFreshness.Unavailable, "x", DateTimeOffset.UtcNow, "none");

        (await fixture.Client.GetImageAsync(snapshot, "icon")).Status.Should().Be(MetadataFreshness.Unavailable);
    }

    [TestMethod]
    public async Task FetchText_UsesSharedTransportAndWritesNothing()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri.EndsWith("/posts/news.md", StringComparison.Ordinal)
                ? Text("# 标题\n正文")
                : Xml(RootXml()));

        var text = await fixture.Client.FetchTextAsync(RootUrl, "posts/news.md");

        text.Should().Be("# 标题\n正文");
        Directory.Exists(fixture.Cache).Should().BeTrue();
        Directory.EnumerateFiles(fixture.Cache, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path))
            .Should().NotContain("news.md");
    }

    [TestMethod]
    public async Task FetchText_MissingSource_ReturnsNull()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        (await fixture.Client.FetchTextAsync(RootUrl, "posts/nope.md")).Should().BeNull();
        (await fixture.Client.FetchTextAsync(RootUrl, "   ")).Should().BeNull();
    }

    private static readonly byte[] IconBytes = [1, 2, 3, 4, 5, 6, 7, 8];

    private static async Task<RootSnapshotResult> RootAsync(Fixture fixture, byte[] image, string etag)
    {
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == RootUrl ? Xml(RootXml()) : Png(image, etag));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        root.Status.Should().Be(MetadataFreshness.Fresh);
        return root;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }

        return condition();
    }

    private static string RootXml(string images = """<Image ID="icon" Source="images/icon.png" />""") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata SchemaVersion="1.0" ContentRevision="1">
          <Application ID="RA3BattleNet">
            <Version>1.0.0</Version>
            <Packages>
              <Package Version="1.0.0"><Manifest>app-leaf</Manifest></Package>
            </Packages>
          </Application>
          <Manifest ID="app-leaf" Source="apps/leaf.xml" />
          {{images}}
        </Metadata>
        """;

    private static HttpResponseMessage Xml(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return response;
    }

    private static HttpResponseMessage Text(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        return response;
    }

    private static HttpResponseMessage Png(byte[] body, string? etag = null, string contentType = "image/png")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (etag is not null)
            response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    private static HttpResponseMessage NotModified() => new(HttpStatusCode.NotModified);

    private static string NewCache() => Path.Combine(Path.GetTempPath(), "metadata-image-" + Guid.NewGuid().ToString("N"));

    private sealed class Fixture : IDisposable
    {
        public required string Cache { get; init; }

        public required ScriptedHandler Handler { get; init; }

        public required HttpClient Http { get; init; }

        public required MetadataClient Client { get; init; }

        public List<Call> Calls => Handler.Calls;

        public static Fixture Create(string? cache = null, bool enableDiskCache = true)
        {
            cache ??= NewCache();
            var handler = new ScriptedHandler();
            var http = new HttpClient(handler);
            return new Fixture
            {
                Cache = cache,
                Handler = handler,
                Http = http,
                Client = new MetadataClient(
                    cache,
                    TimeSpan.FromSeconds(10),
                    maxLeafConcurrency: 4,
                    httpClient: http,
                    enableDiskCache: enableDiskCache),
            };
        }

        public void Dispose()
        {
            Client.Dispose();
            Http.Dispose();
            try
            {
                if (Directory.Exists(Cache))
                    Directory.Delete(Cache, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不改变断言。
            }
        }
    }

    private sealed record Call(string Uri, bool Conditional);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Next { get; set; } =
            (_, _) => throw new InvalidOperationException("测试没有安排响应");

        public List<Call> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(new Call(
                    request.RequestUri?.AbsoluteUri ?? "",
                    request.Headers.IfNoneMatch.Any() || request.Headers.IfModifiedSince is not null));
            }

            return await Next(request, cancellationToken);
        }
    }
}
