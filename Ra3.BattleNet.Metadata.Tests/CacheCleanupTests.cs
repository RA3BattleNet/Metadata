using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 缓存回收：保留 current/previous、只回收无引用对象、容量策略与跨进程租约。
/// </summary>
[TestClass]
public class CacheCleanupTests
{
    private const string Entry = "https://metadata.example.test/metadata.xml";
    private const string Origin = "https://metadata.example.test/";
    private const string MediaUrl = Origin + "img/a.png";

    private string _root = string.Empty;
    private string _rootXml = RootXml("rev-1");
    private string _mediaBody = "AAA";

    [TestInitialize]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), $"md-clean-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理尽力而为 */ }
    }

    // ------------------------------------------------------------------ 快照与对象

    [TestMethod]
    public async Task Cleanup_RemovesSnapshotsNoPointerReferences()
    {
        var layout = new CacheLayout(_root);
        using var cache = NewCache();
        await cache.RefreshAsync();
        await cache.RefreshAsync();

        var orphan = CacheSnapshotStore.SaveRoot(layout, Utf8(RootXml("orphan")));

        var report = cache.Cleanup();

        report.RemovedSnapshots.Should().Contain(orphan);
        report.Preserved.Should().Contain(entry => entry.StartsWith("snapshot:", StringComparison.Ordinal));
        Directory.Exists(layout.SnapshotRoot(orphan)).Should().BeFalse();
        (await cache.OpenAsync()).Snapshot.Should().NotBeNull("有效快照一个都不能少");
    }

    [TestMethod]
    public async Task Cleanup_RemovesObjectsOnlyReferencedByDroppedSnapshot()
    {
        var layout = new CacheLayout(_root);
        using var cache = NewCache();

        // S1：解析出对象 O_A
        var first = await cache.RefreshAsync();
        _mediaBody = "AAA";
        var mediaA = await Resolve(cache, first.Snapshot!);

        // S2：根字节变了（新快照），同一地址现在是别的字节 → 对象 O_B
        _rootXml = RootXml("rev-2");
        _mediaBody = "BBB";
        var second = await cache.RefreshAsync();
        var mediaB = await Resolve(cache, second.Snapshot!);

        // S3：S1 掉出 current/previous
        _rootXml = RootXml("rev-3");
        var third = await cache.RefreshAsync();
        third.Snapshot!.SnapshotId.Should().NotBe(second.Snapshot!.SnapshotId);

        var report = cache.Cleanup();

        report.RemovedObjects.Should().Contain(mediaA.Sha256);
        report.RemovedObjects.Should().NotContain(mediaB.Sha256);
        File.Exists(mediaA.LocalPath).Should().BeFalse();
        File.Exists(mediaB.LocalPath).Should().BeTrue("previous 快照引用的对象必须留着");
    }

    [TestMethod]
    public async Task Cleanup_KeepsSnapshotAndObjectsHeldByLease()
    {
        var layout = new CacheLayout(_root);
        using var cache = NewCache();

        var first = await cache.RefreshAsync();
        _mediaBody = "AAA";
        var media = await Resolve(cache, first.Snapshot!);
        using var lease = cache.AcquireLease(first.Snapshot!);

        // 让 S1 掉出 current/previous
        _rootXml = RootXml("rev-2");
        await cache.RefreshAsync();
        _rootXml = RootXml("rev-3");
        await cache.RefreshAsync();

        var report = cache.Cleanup();

        report.RemovedSnapshots.Should().NotContain(first.Snapshot!.SnapshotId);
        report.RemovedObjects.Should().NotContain(media.Sha256);
        Directory.Exists(layout.SnapshotRoot(first.Snapshot.SnapshotId)).Should().BeTrue();
        File.Exists(media.LocalPath).Should().BeTrue();
    }

    [TestMethod]
    public async Task Cleanup_WithoutValidPointer_DeletesNothing()
    {
        var layout = new CacheLayout(_root);
        using var cache = NewCache();
        await cache.RefreshAsync();
        var orphan = CacheSnapshotStore.SaveRoot(layout, Utf8(RootXml("orphan")));
        var objectPath = PutObject(new string('x', 128));

        File.WriteAllText(layout.CurrentPointerPath, "<CachePointer");
        File.WriteAllText(layout.PreviousPointerPath, "<CachePointer");

        var report = cache.Cleanup();

        report.Error!.Code.Should().Be(CacheErrorCodes.NoCache);
        report.RemovedSnapshots.Should().BeEmpty();
        report.RemovedObjects.Should().BeEmpty();
        Directory.Exists(layout.SnapshotRoot(orphan)).Should().BeTrue();
        File.Exists(objectPath).Should().BeTrue();
    }

    [TestMethod]
    public async Task Cleanup_UnsupportedResourceMapVersion_KeepsObjects()
    {
        var layout = new CacheLayout(_root);
        using var cache = NewCache();
        var first = await cache.RefreshAsync();
        var objectPath = PutObject(new string('x', 64));

        var mapPath = layout.SnapshotResourcesPath(first.Snapshot!.SnapshotId);
        Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        File.WriteAllText(mapPath, "<ResourceMap SchemaVersion=\"99\" Generation=\"1\" />");

        var report = cache.Cleanup();

        report.Error!.Code.Should().Be(CacheErrorCodes.UnsupportedFormat);
        report.RemovedObjects.Should().BeEmpty();
        File.Exists(objectPath).Should().BeTrue();
        File.Exists(mapPath).Should().BeTrue("不认识的格式保留原文件，不覆盖");
    }

    // ------------------------------------------------------------------ 容量

    [TestMethod]
    public async Task Cleanup_MaxBytes_RecyclesUnreferencedObjectsLargestFirst()
    {
        using var cache = NewCache();
        await cache.RefreshAsync();

        var small = PutObject(new string('s', 64));
        var large = PutObject(new string('l', 4096));

        // 上限刚好只容得下一个（小的）：先回收大的，够了就停手
        var report = cache.Cleanup(new CacheCleanupOptions { MaxTotalBytes = 128 });

        report.RemovedObjects.Should().Contain(Path.GetFileName(Path.GetDirectoryName(large)!));
        File.Exists(large).Should().BeFalse();
        File.Exists(small).Should().BeTrue();
        report.BytesAfter.Should().BeLessThanOrEqualTo(128);
        report.Error.Should().BeNull();
    }

    [TestMethod]
    public async Task Cleanup_StillOverLimit_ReportsCacheFullWithoutTouchingReferenced()
    {
        using var cache = NewCache();
        var first = await cache.RefreshAsync();
        _mediaBody = "AAA";
        var media = await Resolve(cache, first.Snapshot!);

        var report = cache.Cleanup(new CacheCleanupOptions { MaxTotalBytes = 1 });

        report.Error!.Code.Should().Be(CacheErrorCodes.CacheFull);
        report.RemovedObjects.Should().BeEmpty();
        File.Exists(media.LocalPath).Should().BeTrue("宁可达不到上限，也不破坏唯一可用缓存");
    }

    [TestMethod]
    public async Task Cleanup_DryRun_ReportsWithoutDeleting()
    {
        var layout = new CacheLayout(_root);
        using var cache = NewCache();
        await cache.RefreshAsync();
        var orphan = CacheSnapshotStore.SaveRoot(layout, Utf8(RootXml("orphan")));

        var report = cache.Cleanup(new CacheCleanupOptions { DryRun = true });

        report.RemovedSnapshots.Should().Contain(orphan);
        Directory.Exists(layout.SnapshotRoot(orphan)).Should().BeTrue();
    }

    // ------------------------------------------------------------------ 暂存与跨进程

    [TestMethod]
    public async Task Cleanup_RemovesOnlyAgedStagingLeftovers()
    {
        var layout = new CacheLayout(_root);
        using var cache = NewCache();
        await cache.RefreshAsync();

        var staleName = CacheHash.Sha256Hex("stale");
        var freshName = CacheHash.Sha256Hex("fresh");
        var stale = Path.Combine(layout.StagingRoot, staleName);
        var fresh = Path.Combine(layout.StagingRoot, freshName);
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(stale, "resource.part"), "half");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-5));

        var report = cache.Cleanup(new CacheCleanupOptions { StagingRetention = TimeSpan.FromHours(1) });

        report.RemovedStaging.Should().Contain(staleName);
        Directory.Exists(stale).Should().BeFalse();
        Directory.Exists(fresh).Should().BeTrue("正在写的暂存目录不能当垃圾扫掉");
    }

    [TestMethod]
    public async Task Refresh_WhenAnotherProcessHoldsTheLock_ReportsBusy()
    {
        Directory.CreateDirectory(_root);
        var lockPath = Path.Combine(_root, "cache.lock");

        using var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cache = NewCache(fileLockTimeout: TimeSpan.FromMilliseconds(150));

        var result = await cache.RefreshAsync();

        result.Outcome.Should().Be(CatalogRefreshOutcome.Unavailable);
        result.Error!.Code.Should().Be(CacheErrorCodes.Busy);
    }

    [TestMethod]
    public async Task Cleanup_WhenAnotherProcessHoldsTheLock_ReportsBusy()
    {
        Directory.CreateDirectory(_root);
        var lockPath = Path.Combine(_root, "cache.lock");

        using var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cache = NewCache(fileLockTimeout: TimeSpan.FromMilliseconds(150));

        var report = cache.Cleanup();

        report.Error!.Code.Should().Be(CacheErrorCodes.Busy);
    }

    // ------------------------------------------------------------------ fixture

    private async Task<CachedResource> Resolve(MetadataCache cache, CatalogSnapshot snapshot)
    {
        using var lease = cache.AcquireLease(snapshot);
        var result = await cache.ResolveResourceAsync(lease, new ResourceRef
        {
            RegistryId = "registry:img/a.png",
            Source = "img/a.png",
            Kind = ResourceKind.Media,
        });
        result.Success.Should().BeTrue(result.Error?.ToString());
        return result.Resource!;
    }

    /// <summary>直接放一个"没有任何快照引用"的对象，用来验证回收策略。</summary>
    private string PutObject(string content)
    {
        var layout = new CacheLayout(_root);
        var bytes = Utf8(content);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var staged = Path.Combine(layout.StagingRoot, CacheHash.Sha256Hex(Guid.NewGuid().ToString("N")), "resource.part");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllBytes(staged, bytes);
        return CacheObjectStore.Commit(layout, digest, staged);
    }

    private static string RootXml(string revision) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <Metadata SchemaVersion="1.0" ContentRevision="{revision}">
           <Tags>
             <Commit>{revision}</Commit>
           </Tags>
         </Metadata>
         """;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private MetadataCache NewCache(TimeSpan? fileLockTimeout = null) =>
        new(new CacheOptions
        {
            EntryUri = new Uri(Entry),
            CacheRoot = _root,
            HttpClient = new HttpClient(new StubHandler(Respond)),
            RefreshRetryCount = 0,
            FileLockTimeout = fileLockTimeout ?? TimeSpan.FromSeconds(30),
        });

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url == Entry) return Ok(_rootXml);
        if (url == MediaUrl) return Ok(_mediaBody);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Ok(string body)
    {
        var content = new ByteArrayContent(Utf8(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
