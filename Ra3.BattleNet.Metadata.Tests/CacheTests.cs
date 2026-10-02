using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 运行期缓存：打开、条件刷新、崩溃恢复与来源绑定。
/// 全部用本地 stub 响应，不依赖任何线上服务。
/// </summary>
[TestClass]
public class CacheTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"md-cache-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理尽力而为 */ }
    }

    // ------------------------------------------------------------------ 打开

    [TestMethod]
    public async Task Open_WithoutCache_ReportsUnavailable()
    {
        using var cache = NewCache(Status(HttpStatusCode.InternalServerError));

        var result = await cache.OpenAsync();

        result.Status.Should().Be(CatalogFreshness.Unavailable);
        result.Snapshot.Should().BeNull();
        result.Error!.Code.Should().Be(CacheErrorCodes.NoCache);
    }

    [TestMethod]
    public async Task Refresh_ThenOpen_ReadsSnapshotBackWithoutNetwork()
    {
        using (var cache = NewCache(Body(RootXml("rev-1"), "\"etag-1\"")))
        {
            var refreshed = await cache.RefreshAsync();
            refreshed.Outcome.Should().Be(CatalogRefreshOutcome.Updated);
            refreshed.Snapshot!.ContentRevision.Should().Be("rev-1");
        }

        var offline = new StubHandler(_ => throw new InvalidOperationException("打开不该联网"));
        using var reader = NewCache(offline.Respond);
        var opened = await reader.OpenAsync();

        opened.Status.Should().Be(CatalogFreshness.Stale);
        opened.Snapshot!.ContentRevision.Should().Be("rev-1");
        opened.Snapshot.Catalog.Mods.Should().BeEmpty();
        offline.RequestCount.Should().Be(0);
    }

    [TestMethod]
    public async Task Open_BindsCacheRootToEntryOrigin()
    {
        using (var cache = NewCache(Body(RootXml())))
        {
            (await cache.RefreshAsync()).Outcome.Should().Be(CatalogRefreshOutcome.Updated);
        }

        using var other = new MetadataCache(new CacheOptions
        {
            EntryUri = new Uri("https://mirror.example.test/metadata.xml"),
            CacheRoot = _root,
        });
        var opened = await other.OpenAsync();

        opened.Status.Should().Be(CatalogFreshness.Unavailable);
        opened.Error!.Code.Should().Be(CacheErrorCodes.OriginMismatch);
    }

    [TestMethod]
    public async Task Open_RefusesDirectoryWithSnapshotsButNoOriginBinding()
    {
        var layout = new CacheLayout(_root);
        layout.EnsureDirectories();
        var snapshotId = CacheSnapshotStore.SaveRoot(layout, Utf8(RootXml()));
        CachePointerFile.Publish(layout, Pointer(1, snapshotId, "https://metadata.example.test/metadata.xml"));

        using var cache = NewCache(Status(HttpStatusCode.OK));
        var opened = await cache.OpenAsync();

        opened.Error!.Code.Should().Be(CacheErrorCodes.OriginMismatch);
        File.Exists(layout.CurrentPointerPath).Should().BeTrue("拒绝复用不等于破坏现场");
    }

    // ------------------------------------------------------------------ 刷新

    [TestMethod]
    public async Task Refresh_NotModified_KeepsTheSameSnapshot()
    {
        using (var first = NewCache(Body(RootXml("rev-1"), "\"etag-1\"")))
        {
            await first.RefreshAsync();
        }

        var handler = new StubHandler(request =>
            HasHeader(request, "If-None-Match", "\"etag-1\"")
                ? NotModified()
                : Ok(RootXml("rev-2"), "\"etag-2\""));

        using var cache = NewCache(handler.Respond);
        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Unchanged);
        result.Snapshot!.ContentRevision.Should().Be("rev-1");
    }

    [TestMethod]
    public async Task Refresh_NewBytes_PublishHigherGeneration()
    {
        var layout = new CacheLayout(_root);
        using (var first = NewCache(Body(RootXml("rev-1"))))
        {
            await first.RefreshAsync();
        }
        var generation1 = CachePointerFile.ReadHighest(layout)!.Generation;

        using var cache = NewCache(Body(RootXml("rev-2")));
        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Updated);
        result.Snapshot!.ContentRevision.Should().Be("rev-2");
        CachePointerFile.ReadHighest(layout)!.Generation.Should().BeGreaterThan(generation1);
        File.Exists(layout.PreviousPointerPath).Should().BeTrue("上一版指针留作恢复材料");
    }

    [TestMethod]
    public async Task Refresh_HtmlChallengePage_KeepsLastValidSnapshot()
    {
        using (var first = NewCache(Body(RootXml("rev-1"))))
        {
            await first.RefreshAsync();
        }

        using var cache = NewCache(Body("<html><body>challenge</body></html>", contentType: "text/html"));
        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Stale);
        result.Snapshot!.ContentRevision.Should().Be("rev-1");
        result.Error!.Code.Should().Be(CacheErrorCodes.InvalidXml);
    }

    [TestMethod]
    public async Task Refresh_HtmlChallengePageWithXmlContentType_IsStillRejected()
    {
        using var cache = NewCache(Body("<html><body>challenge</body></html>"));

        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Unavailable);
        result.Error!.Code.Should().Be(CacheErrorCodes.InvalidXml);
    }

    [TestMethod]
    public async Task Refresh_DtdDocument_IsRejected()
    {
        const string withDtd = "<?xml version=\"1.0\"?><!DOCTYPE Metadata [<!ENTITY x \"y\">]><Metadata SchemaVersion=\"1.0\"/>";
        using var cache = NewCache(Body(withDtd));

        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Unavailable);
        result.Error!.Code.Should().Be(CacheErrorCodes.InvalidXml);
    }

    [TestMethod]
    public async Task Refresh_OversizedDeclaredLength_IsRejected()
    {
        using var cache = NewCache(Body(RootXml() + new string(' ', 4096)), rootMaxBytes: 512);

        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Unavailable);
        result.Error!.Code.Should().Be(CacheErrorCodes.TooLarge);
    }

    [TestMethod]
    public async Task Refresh_UnknownSchema_IsRejectedAndKeepsOldSnapshot()
    {
        using (var first = NewCache(Body(RootXml("rev-1"))))
        {
            await first.RefreshAsync();
        }

        using var cache = NewCache(Body(RootXml("rev-2", schema: "9.9")));
        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Stale);
        result.Snapshot!.ContentRevision.Should().Be("rev-1");
        result.Error!.Code.Should().Be(CacheErrorCodes.SchemaMismatch);
    }

    [TestMethod]
    public async Task Refresh_ServerError_DegradesWithoutTouchingTheSnapshot()
    {
        using var cache = NewCache(Status(HttpStatusCode.Forbidden), retryCount: 0);

        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Unavailable);
        result.Error!.Code.Should().Be(CacheErrorCodes.HttpStatus);
    }

    [TestMethod]
    public async Task Refresh_WeakETag_IsNotStored()
    {
        var layout = new CacheLayout(_root);
        using var cache = NewCache(Body(RootXml("rev-1"), "\"weak\"", weakETag: true));

        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Updated);
        CachePointerFile.ReadHighest(layout)!.ETag.Should().BeNull("弱验证器不能用于严格恢复");
    }

    // ------------------------------------------------------------------ 崩溃恢复

    [TestMethod]
    public void Pointer_HighestGenerationWinsAcrossCrashPoints()
    {
        var layout = new CacheLayout(_root);
        layout.EnsureDirectories();

        var id1 = CacheSnapshotStore.SaveRoot(layout, Utf8(RootXml("rev-1")));
        var id2 = CacheSnapshotStore.SaveRoot(layout, Utf8(RootXml("rev-2")));

        CachePointerFile.Publish(layout, Pointer(1, id1));
        var pointer1 = File.ReadAllBytes(layout.CurrentPointerPath);

        CachePointerFile.Publish(layout, Pointer(2, id2));
        var pointer2 = File.ReadAllBytes(layout.CurrentPointerPath);
        CachePointerFile.ReadHighest(layout)!.Generation.Should().Be(2);

        CachePointerFile.Publish(layout, Pointer(3, id1));
        var pointer3 = File.ReadAllBytes(layout.CurrentPointerPath);

        // 崩溃在"pending 写好、还没轮换"：current=2、previous=1、pending=3 → 取 3
        File.WriteAllBytes(layout.CurrentPointerPath, pointer2);
        File.WriteAllBytes(layout.PreviousPointerPath, pointer1);
        File.WriteAllBytes(layout.PendingPointerPath, pointer3);
        CachePointerFile.ReadHighest(layout)!.Generation.Should().Be(3);

        // 崩溃在"current 已改名、pending 还没改名"：没有 current，靠 pending 前滚
        File.Delete(layout.CurrentPointerPath);
        CachePointerFile.ReadHighest(layout)!.Generation.Should().Be(3);

        // 只剩 previous：上一版指针仍在，退回它而不是当成"没有缓存"
        File.Delete(layout.PendingPointerPath);
        File.WriteAllBytes(layout.PreviousPointerPath, pointer2);
        var recovered = CachePointerFile.ReadHighest(layout);
        recovered!.Generation.Should().Be(2);
        recovered.SnapshotId.Should().Be(id2);
    }

    [TestMethod]
    public async Task Open_CorruptCurrentPointer_FallsBackToPrevious()
    {
        var layout = new CacheLayout(_root);
        using (var first = NewCache(Body(RootXml("rev-1"))))
        {
            await first.RefreshAsync();
        }
        using (var second = NewCache(Body(RootXml("rev-2"))))
        {
            await second.RefreshAsync();
        }

        File.WriteAllText(layout.CurrentPointerPath, "<CachePointer");

        using var cache = NewCache(Status(HttpStatusCode.OK));
        var opened = await cache.OpenAsync();

        opened.Status.Should().Be(CatalogFreshness.Stale);
        opened.Snapshot!.ContentRevision.Should().Be("rev-1");
    }

    [TestMethod]
    public async Task Open_PointerPointingAtMissingSnapshot_IsIgnored()
    {
        var layout = new CacheLayout(_root);
        layout.EnsureDirectories();
        CacheOrigin.EnsureBound(layout, new Uri("https://metadata.example.test/metadata.xml")).Should().BeNull();
        var id = CacheSnapshotStore.SaveRoot(layout, Utf8(RootXml("rev-1")));
        CachePointerFile.Publish(layout, Pointer(1, id));

        File.Delete(layout.SnapshotMetadataPath(id));

        using var cache = NewCache(Status(HttpStatusCode.OK));
        var opened = await cache.OpenAsync();

        opened.Status.Should().Be(CatalogFreshness.Unavailable);
        opened.Error!.Code.Should().Be(CacheErrorCodes.NoCache);
    }

    // ------------------------------------------------------------------ fixture

    private static CachePointer Pointer(int generation, string snapshotId, string? origin = null) => new()
    {
        Generation = generation,
        SnapshotId = snapshotId,
        RetrievedAtUtc = DateTimeOffset.UnixEpoch,
        OriginIdentity = origin ?? "https://metadata.example.test/metadata.xml",
    };

    private static string RootXml(string revision = "rev-1", string schema = "1.0") =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <Metadata SchemaVersion="{schema}" ContentRevision="{revision}">
           <Tags>
             <Commit>{revision}</Commit>
           </Tags>
         </Metadata>
         """;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private MetadataCache NewCache(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        long rootMaxBytes = 8L * 1024 * 1024,
        int retryCount = 0) =>
        new(new CacheOptions
        {
            EntryUri = new Uri("https://metadata.example.test/metadata.xml"),
            CacheRoot = _root,
            HttpClient = new HttpClient(new StubHandler(responder)),
            RootMaxBytes = rootMaxBytes,
            RefreshRetryCount = retryCount,
        });

    private static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status) =>
        _ => new HttpResponseMessage(status);

    private static Func<HttpRequestMessage, HttpResponseMessage> Body(
        string body, string? etag = null, string? contentType = "application/xml", bool weakETag = false) =>
        _ => Ok(body, etag, contentType, weakETag);

    private static bool HasHeader(HttpRequestMessage request, string name, string expected) =>
        request.Headers.TryGetValues(name, out var values) && values.Contains(expected, StringComparer.Ordinal);

    private static HttpResponseMessage Ok(
        string body, string? etag = null, string? contentType = "application/xml", bool weakETag = false)
    {
        var content = new ByteArrayContent(Utf8(body));
        if (contentType is not null) content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        if (etag is not null) response.Headers.ETag = new EntityTagHeaderValue(etag, weakETag);
        return response;
    }

    private static HttpResponseMessage NotModified() => new(HttpStatusCode.NotModified);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public Func<HttpRequestMessage, HttpResponseMessage> Respond => _responder;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responder(request));
        }
    }
}
