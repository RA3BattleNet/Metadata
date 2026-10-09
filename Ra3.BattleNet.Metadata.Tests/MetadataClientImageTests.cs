using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 已登记图片的缓存读取、显式条件刷新和冷失败抑制。
/// 普通读取不得发 HTTP；只有 RefreshImageAsync 才做条件请求。
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
    public async Task Image_WarmReads_DoNotValidate()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        await fixture.Client.GetImageAsync(root, "icon");
        var revision = fixture.Client.ImageRevision(root, "icon");
        var calls = fixture.Calls.Count(call => call.Uri == IconUrl);

        fixture.Handler.Next = (_, _) => throw new HttpRequestException("warm read must not touch the network");
        for (var i = 0; i < 5; i++)
        {
            var cached = await fixture.Client.GetImageAsync(root, "icon");
            cached.Status.Should().Be(MetadataFreshness.Stale);
            cached.Bytes.Should().Equal(IconBytes);
            cached.Error.Should().BeNull();
            await Task.Delay(150);
        }

        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(calls);
        fixture.Client.ImageRevision(root, "icon").Should().Be(revision);
        revision.Should().NotBe(0);
    }

    [TestMethod]
    public async Task Image_ExplicitRefresh_NotModified_KeepsBytesAndRevision()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        await fixture.Client.GetImageAsync(root, "icon");
        var revision = fixture.Client.ImageRevision(root, "icon");
        var updated = 0;
        fixture.Client.ImageUpdated += (_, _) => Interlocked.Increment(ref updated);
        fixture.Handler.Next = (request, _) =>
        {
            request.Headers.IfNoneMatch.ToString().Should().Be("\"icon-1\"");
            return Task.FromResult(NotModified());
        };

        var refreshed = await fixture.Client.RefreshImageAsync(root, "icon");

        refreshed.Status.Should().Be(MetadataFreshness.Stale);
        refreshed.Error.Should().BeNull();
        refreshed.Bytes.Should().Equal(IconBytes);
        fixture.Calls.Last().Conditional.Should().BeTrue();
        fixture.Client.ImageRevision(root, "icon").Should().Be(revision);
        Interlocked.CompareExchange(ref updated, 0, 0).Should().Be(0);
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(2);
        (await fixture.Client.GetImageAsync(root, "icon")).Bytes.Should().Equal(IconBytes);
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(2, "刷新之后的普通读取不再请求");
    }

    [TestMethod]
    public async Task Image_ExplicitRefresh_ChangedBytes_UpdatesCacheAndRaisesOnce()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        await fixture.Client.GetImageAsync(root, "icon");
        var revision = fixture.Client.ImageRevision(root, "icon");
        var changed = new byte[] { 9, 9, 9, 9 };
        var raised = 0;
        fixture.Client.ImageUpdated += (_, args) =>
        {
            args.ImageUri.Should().Be(IconUrl);
            Interlocked.Increment(ref raised);
        };
        fixture.Handler.Next = (request, _) =>
        {
            request.Headers.IfNoneMatch.ToString().Should().Be("\"icon-1\"");
            return Task.FromResult(Png(changed, "\"icon-2\""));
        };

        var refreshed = await fixture.Client.RefreshImageAsync(root, "icon");

        refreshed.Status.Should().Be(MetadataFreshness.Fresh);
        refreshed.Error.Should().BeNull();
        refreshed.Bytes.Should().Equal(changed);
        raised.Should().Be(1);
        fixture.Client.ImageRevision(root, "icon").Should().NotBe(revision);
        var updatedRevision = fixture.Client.ImageRevision(root, "icon");
        var body = Directory.EnumerateFiles(fixture.Cache, "icon.png", SearchOption.AllDirectories).Single();
        File.ReadAllBytes(body).Should().Equal(changed);

        fixture.Handler.Next = (_, _) => Task.FromResult(NotModified());
        var again = await fixture.Client.RefreshImageAsync(root, "icon");
        again.Bytes.Should().Equal(changed);
        again.Error.Should().BeNull();
        raised.Should().Be(1);
        fixture.Client.ImageRevision(root, "icon").Should().Be(updatedRevision);
    }

    [TestMethod]
    public async Task Image_RefreshFailure_KeepsOldBytesWithoutAnotherRead()
    {
        using var fixture = Fixture.Create();
        var root = await RootAsync(fixture, IconBytes, "\"icon-1\"");
        await fixture.Client.GetImageAsync(root, "icon");
        var revision = fixture.Client.ImageRevision(root, "icon");
        var raised = 0;
        fixture.Client.ImageUpdated += (_, _) => Interlocked.Increment(ref raised);
        fixture.Handler.Next = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var failed = await fixture.Client.RefreshImageAsync(root, "icon");
        var calls = fixture.Calls.Count(call => call.Uri == IconUrl);

        failed.Ok.Should().BeTrue();
        failed.Status.Should().Be(MetadataFreshness.Stale);
        failed.Bytes.Should().Equal(IconBytes);
        failed.Error.Should().Contain("503");
        raised.Should().Be(0);
        fixture.Client.ImageRevision(root, "icon").Should().Be(revision);
        fixture.Handler.Next = (_, _) => throw new HttpRequestException("ordinary read must not retry");
        (await fixture.Client.GetImageAsync(root, "icon")).Bytes.Should().Equal(IconBytes);
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(calls);
    }

    [TestMethod]
    public async Task Image_ExplicitRefresh_AfterColdFailure_NotifiesWithoutAnotherRequest()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == RootUrl
                ? Xml(RootXml())
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        (await fixture.Client.GetImageAsync(root, "icon")).Status.Should().Be(MetadataFreshness.Unavailable);

        var raised = 0;
        fixture.Client.ImageUpdated += (_, args) =>
        {
            args.ImageUri.Should().Be(IconUrl);
            Interlocked.Increment(ref raised);
            var during = fixture.Client.GetImageAsync(root, "icon").GetAwaiter().GetResult();
            during.Ok.Should().BeTrue();
            during.Bytes.Should().Equal(IconBytes);
        };
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == IconUrl ? Png(IconBytes, "\"icon-1\"") : Xml(RootXml()));

        var refreshed = await fixture.Client.RefreshImageAsync(root, "icon");
        var calls = fixture.Calls.Count(call => call.Uri == IconUrl);

        refreshed.Ok.Should().BeTrue();
        refreshed.Status.Should().Be(MetadataFreshness.Fresh);
        refreshed.Error.Should().BeNull();
        raised.Should().Be(1);
        calls.Should().Be(2, "失败一次，显式成功一次；通知回调里的读取不能再发请求");
        (await fixture.Client.GetImageAsync(root, "icon")).Bytes.Should().Equal(IconBytes);
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(calls);
    }

    [TestMethod]
    public async Task Image_ColdFailure_BlocksOrdinaryReadsUntilExplicitRefreshInterval()
    {
        var clock = new ManualClock();
        using var fixture = Fixture.Create(clock: clock);
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == RootUrl
                ? Xml(RootXml())
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);

        (await fixture.Client.GetImageAsync(root, "icon")).Status.Should().Be(MetadataFreshness.Unavailable);
        (await fixture.Client.GetImageAsync(root, "icon")).Error.Should().Contain("显式刷新");
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(1);

        clock.Now += TimeSpan.FromMinutes(9);
        await fixture.Client.GetImageAsync(root, "icon");
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(1);

        var refreshed = await fixture.Client.RefreshImageAsync(root, "icon");
        refreshed.Status.Should().Be(MetadataFreshness.Unavailable);
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(2, "显式刷新不受冷失败抑制");
        await fixture.Client.GetImageAsync(root, "icon");
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(2, "失败的显式刷新之后，普通读取不能再发第二次");

        clock.Now += TimeSpan.FromMinutes(10);
        await fixture.Client.GetImageAsync(root, "icon");
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(3);
    }

    [TestMethod]
    public async Task Image_ColdReadAndRefresh_ShareOneFlightAndThenServeCache()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(RootXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Next = async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri != IconUrl)
                return Xml(RootXml());
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Png(IconBytes, "\"icon-1\"");
        };

        var cold = fixture.Client.GetImageAsync(root, "icon");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refresh = fixture.Client.RefreshImageAsync(root, "icon");
        release.TrySetResult();
        var results = await Task.WhenAll(cold, refresh);

        results.Should().OnlyContain(image => image.Ok && image.Bytes!.SequenceEqual(IconBytes));
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(1);
        (await fixture.Client.GetImageAsync(root, "icon")).Status.Should().Be(MetadataFreshness.Stale);
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(1);
    }

    [TestMethod]
    public async Task Image_ConcurrentColdReads_ShareOneInstantRequest()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == RootUrl ? Xml(RootXml()) : Png(IconBytes, "\"icon-1\""));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);

        var reads = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Client.GetImageAsync(root, "icon")));

        reads.Should().OnlyContain(image => image.Ok && image.Bytes!.SequenceEqual(IconBytes));
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(1);
        (await fixture.Client.GetImageAsync(root, "icon")).Status.Should().Be(MetadataFreshness.Stale);
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(1);
    }

    [TestMethod]
    public async Task Image_SourceChange_ChangesRevisionEvenWhenBytesMatch()
    {
        using var fixture = Fixture.Create();
        var source = "images/a.png";
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == RootUrl
                ? Xml(RootXml($"""<Image ID="icon" Source="{source}" />"""))
                : Png(IconBytes, "\"icon\""));
        var rootA = await fixture.Client.RefreshRootAsync(RootUrl);
        await fixture.Client.GetImageAsync(rootA, "icon");
        var revisionA = fixture.Client.ImageRevision(rootA, "icon");

        source = "images/b.png";
        var rootB = await fixture.Client.RefreshRootAsync(RootUrl);
        var beforeDownload = fixture.Client.ImageRevision(rootB, "icon");
        await fixture.Client.GetImageAsync(rootB, "icon");
        var revisionB = fixture.Client.ImageRevision(rootB, "icon");

        revisionA.Should().NotBe(0);
        beforeDownload.Should().NotBe(revisionA);
        revisionB.Should().NotBe(revisionA);
        fixture.Client.ImageRevision(rootA, "icon").Should().Be(revisionA);
    }

    [TestMethod]
    public async Task Image_Revision_IsStableAcrossProcessAndTracksCachedBytes()
    {
        var cache = NewCache();
        int revision;
        using (var first = Fixture.Create(cache, keepCache: true))
        {
            var root = await RootAsync(first, IconBytes, "\"icon-1\"");
            await first.Client.GetImageAsync(root, "icon");
            revision = first.Client.ImageRevision(root, "icon");
            revision.Should().NotBe(0);
        }

        using (var second = Fixture.Create(cache, keepCache: true))
        {
            second.Handler.Next = (_, _) => throw new HttpRequestException("restart must not refetch unchanged bytes");
            var opened = await second.Client.OpenSnapshotAsync(RootUrl);
            second.Client.ImageRevision(opened, "icon").Should().Be(revision);
            (await second.Client.GetImageAsync(opened, "icon")).Bytes.Should().Equal(IconBytes);
            second.Calls.Should().BeEmpty();
        }

        var changed = new byte[] { 4, 4, 4, 4 };
        ReplaceImageBody(cache, "icon.png", changed);
        using var third = Fixture.Create(cache);
        third.Handler.Next = (_, _) => throw new HttpRequestException("changed disk bytes are already cached");
        var reopened = await third.Client.OpenSnapshotAsync(RootUrl);
        third.Client.ImageRevision(reopened, "icon").Should().NotBe(revision);
        (await third.Client.GetImageAsync(reopened, "icon")).Bytes.Should().Equal(changed);
        third.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Image_CallerCancel_DoesNotCancelSharedDownload()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(RootXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Next = async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri != IconUrl)
                return Xml(RootXml());
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Png(IconBytes, "\"icon-1\"");
        };
        using var cts = new CancellationTokenSource();
        var cancelled = fixture.Client.GetImageAsync(root, "icon", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        var act = async () => await cancelled;
        await act.Should().ThrowAsync<OperationCanceledException>();
        var joined = fixture.Client.GetImageAsync(root, "icon");
        release.TrySetResult();

        (await joined).Bytes.Should().Equal(IconBytes);
        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(1);
    }

    [TestMethod]
    public async Task Image_Dispose_CancelsOutstandingDownload()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(RootXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Next = async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri != IconUrl)
                return Xml(RootXml());
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Png(IconBytes);
        };
        var pending = fixture.Client.GetImageAsync(root, "icon");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Client.Dispose();

        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task DisabledDiskCache_RefreshStillReadsLiveSource()
    {
        using var fixture = Fixture.Create(NewCache(), enableDiskCache: false);
        fixture.Handler.Next = (_, _) => Task.FromResult(Xml(RootXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        fixture.Handler.Next = (request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == IconUrl ? Png(IconBytes) : Xml(RootXml()));

        (await fixture.Client.RefreshImageAsync(root, "icon")).Status.Should().Be(MetadataFreshness.Fresh);
        (await fixture.Client.RefreshImageAsync(root, "icon")).Status.Should().Be(MetadataFreshness.Fresh);

        fixture.Calls.Count(call => call.Uri == IconUrl).Should().Be(2);
        fixture.Calls.Where(call => call.Uri == IconUrl).Should().OnlyContain(call => !call.Conditional);
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


    private static void ReplaceImageBody(string cache, string fileName, byte[] body)
    {
        var path = Directory.EnumerateFiles(cache, fileName, SearchOption.AllDirectories).Single();
        File.WriteAllBytes(path, body);
        var stamp = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path + ".etag"))!.AsObject();
        stamp["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        File.WriteAllText(path + ".etag", stamp.ToJsonString());
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
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

        public bool KeepCache { get; init; }
        public List<Call> Calls => Handler.Calls;

        public static Fixture Create(string? cache = null, bool enableDiskCache = true, TimeProvider? clock = null, bool keepCache = false)
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
                    enableDiskCache: enableDiskCache,
                    clock: clock),
                KeepCache = keepCache,
            };
        }

        public void Dispose()
        {
            Client.Dispose();
            Http.Dispose();
            try
            {
                if (!KeepCache && Directory.Exists(Cache))
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
