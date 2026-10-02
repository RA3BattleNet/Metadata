using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 资源解析：地址按原远端 origin 解析、对象内容寻址、叶子必须核对、媒体可命中本地。
/// </summary>
[TestClass]
public class CacheResourceTests
{
    private const string Entry = "https://metadata.example.test/metadata.xml";
    private const string Origin = "https://metadata.example.test/";

    private string _root = string.Empty;

    [TestInitialize]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), $"md-res-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理尽力而为 */ }
    }

    // ------------------------------------------------------------------ 地址解析

    [TestMethod]
    public async Task Resolve_RelativeSource_UsesOriginRatherThanCacheRoot()
    {
        var server = new Endpoints().Ok(Origin + "mods/x/images/a.png", "PNG-BYTES", "\"v1\"");
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Media("mods/x/images/a.png"));

        result.Success.Should().BeTrue();
        result.Resource!.SourceUri.AbsoluteUri.Should().Be(Origin + "mods/x/images/a.png");
        result.Resource.LocalPath.Should().Be(Path.Combine(_root, "objects", result.Resource.Sha256, "payload"));
        server.Calls.Should().ContainSingle().Which.Url.Should().Be(Origin + "mods/x/images/a.png");
    }

    [TestMethod]
    public async Task Resolve_RelativeSource_StaysUnderEntryDirectory()
    {
        var entry = "https://metadata.example.test/root/metadata.xml";
        var server = new Endpoints().Ok("https://metadata.example.test/root/mods/a.png", "IMG");
        using var cache = NewCache(server, entry);
        using var lease = cache.AcquireLease(Snapshot(entry));

        var result = await cache.ResolveResourceAsync(lease, Media("mods/a.png"));

        result.Success.Should().BeTrue();
        result.Resource!.SourceUri.AbsoluteUri.Should().Be("https://metadata.example.test/root/mods/a.png");
    }

    [TestMethod]
    public async Task Resolve_EscapingSource_IsRejectedWithoutNetwork()
    {
        var entry = "https://metadata.example.test/root/metadata.xml";
        var server = new Endpoints();
        using var cache = NewCache(server, entry);
        using var lease = cache.AcquireLease(Snapshot(entry));

        var result = await cache.ResolveResourceAsync(lease, Media("../../secret.png"));

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(CacheErrorCodes.PathEscape);
        server.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Resolve_AbsoluteWindowsPath_IsRejected()
    {
        using var cache = NewCache(new Endpoints());
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Media(@"C:\Windows\system32\a.png"));

        result.Error!.Code.Should().Be(CacheErrorCodes.PathEscape);
    }

    [TestMethod]
    public async Task Resolve_RemoteDocumentPointingAtLocalFile_IsRejected()
    {
        using var cache = NewCache(new Endpoints());
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Media(@"file:///C:/Windows/win.ini"));

        result.Error!.Code.Should().Be(CacheErrorCodes.UnsupportedScheme);
    }

    [TestMethod]
    public async Task Resolve_LocalEntry_OnlyReadsInsideEntryDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"md-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "a.md"), "hello");

        try
        {
            var entry = new Uri(Path.Combine(dir, "metadata.xml")).AbsoluteUri;
            using var cache = NewCache(new Endpoints(), entry);
            using var lease = cache.AcquireLease(Snapshot(entry));

            var ok = await cache.ResolveResourceAsync(lease, Media("a.md"));
            ok.Success.Should().BeTrue();
            File.ReadAllText(ok.Resource!.LocalPath).Should().Be("hello");

            var escape = await cache.ResolveResourceAsync(lease, Media("../outside.md"));
            escape.Error!.Code.Should().Be(CacheErrorCodes.PathEscape);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 尽力而为 */ }
        }
    }

    // ------------------------------------------------------------------ 媒体与叶子

    [TestMethod]
    public async Task Resolve_Media_SecondCallServesLocalObject()
    {
        var server = new Endpoints().Ok(Origin + "img/a.png", "PNG");
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var first = await cache.ResolveResourceAsync(lease, Media("img/a.png"));
        var second = await cache.ResolveResourceAsync(lease, Media("img/a.png"));

        first.Resource!.FromCache.Should().BeFalse();
        second.Resource!.FromCache.Should().BeTrue();
        second.Resource.Sha256.Should().Be(first.Resource.Sha256);
        server.Calls.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Resolve_Media_SecondInstanceUsesPersistedMap()
    {
        var server = new Endpoints().Ok(Origin + "img/a.png", "PNG");
        using (var first = NewCache(server))
        {
            using var lease = first.AcquireLease(Snapshot());
            await first.ResolveResourceAsync(lease, Media("img/a.png"));
        }

        using var second = NewCache(server);
        using var lease2 = second.AcquireLease(Snapshot());
        var result = await second.ResolveResourceAsync(lease2, Media("img/a.png"));

        result.Resource!.FromCache.Should().BeTrue();
        server.Calls.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Resolve_Leaf_RevalidatesAndReusesObjectOnNotModified()
    {
        var server = new Endpoints().Ok(Origin + "manifests/1.xml", "<Manifest/>", "\"leaf-1\"");
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var first = await cache.ResolveResourceAsync(lease, Leaf("manifests/1.xml"));
        var second = await cache.ResolveResourceAsync(lease, Leaf("manifests/1.xml"));

        first.Resource!.FromCache.Should().BeFalse();
        second.Resource!.FromCache.Should().BeTrue();
        second.Resource.Sha256.Should().Be(first.Resource.Sha256);
        server.Calls.Should().HaveCount(2);
        server.Calls[1].IfNoneMatch.Should().Be("\"leaf-1\"");
    }

    [TestMethod]
    public async Task Resolve_Leaf_ChangedBytesProduceNewObject()
    {
        var server = new Endpoints().Ok(Origin + "manifests/1.xml", "<Manifest v=\"1\"/>", "\"leaf-1\"");
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var first = await cache.ResolveResourceAsync(lease, Leaf("manifests/1.xml"));
        server.Ok(Origin + "manifests/1.xml", "<Manifest v=\"2\"/>", "\"leaf-2\"");
        var second = await cache.ResolveResourceAsync(lease, Leaf("manifests/1.xml"));

        second.Resource!.Sha256.Should().NotBe(first.Resource!.Sha256);
        second.Resource.FromCache.Should().BeFalse();
        File.Exists(first.Resource.LocalPath).Should().BeTrue("旧对象按内容寻址保留，不被原地改写");
    }

    [TestMethod]
    public async Task Resolve_Leaf_ServerErrorDoesNotFallBackToStaleObject()
    {
        var server = new Endpoints().Ok(Origin + "manifests/1.xml", "<Manifest/>", "\"leaf-1\"");
        using var cache = NewCache(server, retryCount: 0);
        using var lease = cache.AcquireLease(Snapshot());

        await cache.ResolveResourceAsync(lease, Leaf("manifests/1.xml"));
        server.Status(Origin + "manifests/1.xml", HttpStatusCode.InternalServerError);
        var second = await cache.ResolveResourceAsync(lease, Leaf("manifests/1.xml"));

        second.Success.Should().BeFalse("拿不到当前发布就不能拿旧字节冒充");
        second.Error!.Code.Should().Be(CacheErrorCodes.HttpStatus);
    }

    // ------------------------------------------------------------------ 校验

    [TestMethod]
    public async Task Resolve_DeclaredMd5Match_MarksLegacyBinding()
    {
        const string body = "markdown-body";
        var md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body)));
        var server = new Endpoints().Ok(Origin + "posts/a.md", body);
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Media("posts/a.md", md5, "MD5"));

        result.Success.Should().BeTrue();
        result.Resource!.Binding.Should().Be(ResourceBinding.Legacy);
    }

    [TestMethod]
    public async Task Resolve_DeclaredMd5Mismatch_IsRejectedWithoutCommittingObject()
    {
        var server = new Endpoints().Ok(Origin + "posts/a.md", "markdown-body");
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Media("posts/a.md", new string('0', 32), "MD5"));

        result.Error!.Code.Should().Be(CacheErrorCodes.DigestMismatch);
        var objects = Path.Combine(_root, "objects");
        (!Directory.Exists(objects) || Directory.GetDirectories(objects).Length == 0)
            .Should().BeTrue("校验不过的字节不登记成对象");
    }

    [TestMethod]
    public async Task Resolve_DeclaredSha256_MarksStrongBinding()
    {
        const string body = "leaf-bytes";
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var server = new Endpoints().Ok(Origin + "leaf.xml", body);
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Leaf("leaf.xml", sha, "SHA256"));

        result.Success.Should().BeTrue();
        result.Resource!.Binding.Should().Be(ResourceBinding.Strong);
    }

    [TestMethod]
    public async Task Resolve_UnknownHashAlgorithm_FailsWithoutNetwork()
    {
        var server = new Endpoints().Ok(Origin + "leaf.xml", "x");
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Leaf("leaf.xml", "AABBCCDD", "CRC32C"));

        result.Error!.Code.Should().Be(CacheErrorCodes.UnsupportedHashAlgorithm);
        server.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Resolve_HashWithoutAlgorithm_IsRejected()
    {
        using var cache = NewCache(new Endpoints());
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Leaf("leaf.xml", new string('a', 64), null));

        result.Error!.Code.Should().Be(CacheErrorCodes.InvalidSource);
    }

    [TestMethod]
    public async Task Resolve_DeclaredSizeMismatch_IsRejected()
    {
        var server = new Endpoints().Ok(Origin + "a.png", "12345");
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var resource = new ResourceRef
        {
            RegistryId = "r",
            Source = "a.png",
            Kind = ResourceKind.Media,
            ExpectedSize = 99,
        };
        var result = await cache.ResolveResourceAsync(lease, resource);

        result.Error!.Code.Should().Be(CacheErrorCodes.DigestMismatch);
    }

    [TestMethod]
    public async Task Resolve_OversizedResource_IsRejected()
    {
        var server = new Endpoints().Ok(Origin + "a.png", new string('x', 2048));
        using var cache = NewCache(server, mediaMaxBytes: 512);
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Media("a.png"));

        result.Error!.Code.Should().Be(CacheErrorCodes.TooLarge);
    }

    [TestMethod]
    public async Task Resolve_MissingResource_ReportsUnavailable()
    {
        using var cache = NewCache(new Endpoints());
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Media("gone.png"));

        result.Error!.Code.Should().Be(CacheErrorCodes.ResourceUnavailable);
    }

    // ------------------------------------------------------------------ 租约

    [TestMethod]
    public async Task Resolve_LeaseFromAnotherOrigin_IsRejected()
    {
        using var cache = NewCache(new Endpoints());
        using var lease = cache.AcquireLease(Snapshot("https://mirror.example.test/metadata.xml"));

        var result = await cache.ResolveResourceAsync(lease, Media("a.png"));

        result.Error!.Code.Should().Be(CacheErrorCodes.ForeignSnapshot);
    }

    [TestMethod]
    public async Task Resolve_DisposedLease_IsRejected()
    {
        using var cache = NewCache(new Endpoints());
        var lease = cache.AcquireLease(Snapshot());
        lease.Dispose();

        var result = await cache.ResolveResourceAsync(lease, Media("a.png"));

        result.Error!.Code.Should().Be(CacheErrorCodes.ForeignSnapshot);
        cache.ActiveLeases.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Resolve_TracksObjectOnLease()
    {
        var server = new Endpoints().Ok(Origin + "a.png", "PNG");
        using var cache = NewCache(server);
        using var lease = cache.AcquireLease(Snapshot());

        var result = await cache.ResolveResourceAsync(lease, Media("a.png"));

        lease.ReferencedObjects.Should().ContainSingle().Which.Should().Be(result.Resource!.Sha256);
    }

    // ------------------------------------------------------------------ fixture

    private static ResourceRef Media(string source, string? hash = null, string? algorithm = null) => new()
    {
        RegistryId = "registry:" + source,
        Source = source,
        Kind = ResourceKind.Media,
        ExpectedHash = hash,
        ExpectedHashAlgorithm = algorithm,
    };

    private static ResourceRef Leaf(string source, string? hash = null, string? algorithm = null) => new()
    {
        RegistryId = "registry:" + source,
        Source = source,
        Kind = ResourceKind.Leaf,
        ExpectedHash = hash,
        ExpectedHashAlgorithm = algorithm,
    };

    private CatalogSnapshot Snapshot(string entry = Entry) => new()
    {
        SnapshotId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry))),
        CacheRoot = _root,
        OriginEntryUri = new Uri(entry),
        RetrievedAtUtc = DateTimeOffset.UnixEpoch,
        MetadataPath = "unused.xml",
        Root = new Metadata(),
        SchemaVersion = "1.0",
    };

    private MetadataCache NewCache(
        Endpoints endpoints,
        string entry = Entry,
        int retryCount = 0,
        long mediaMaxBytes = 32L * 1024 * 1024,
        long leafMaxBytes = 32L * 1024 * 1024) =>
        new(new CacheOptions
        {
            EntryUri = new Uri(entry),
            CacheRoot = _root,
            HttpClient = new HttpClient(new StubHandler(endpoints.Respond)),
            RefreshRetryCount = retryCount,
            MediaMaxBytes = mediaMaxBytes,
            LeafMaxBytes = leafMaxBytes,
        });

    /// <summary>按绝对地址提供响应的小型服务器替身，并记录每次请求的地址与条件头。</summary>
    private sealed class Endpoints
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body, string? ETag)> _map = new(StringComparer.Ordinal);

        public List<(string Url, string? IfNoneMatch)> Calls { get; } = [];

        public Endpoints Ok(string url, string body, string? etag = null)
        {
            _map[url] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(body), etag);
            return this;
        }

        public Endpoints Status(string url, HttpStatusCode status)
        {
            _map[url] = (status, [], null);
            return this;
        }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values)
                ? string.Join(",", values)
                : null;
            Calls.Add((url, ifNoneMatch));

            if (!_map.TryGetValue(url, out var entry)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (entry.ETag is not null && ifNoneMatch == entry.ETag) return new HttpResponseMessage(HttpStatusCode.NotModified);

            var response = new HttpResponseMessage(entry.Status) { Content = new ByteArrayContent(entry.Body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (entry.ETag is not null) response.Headers.ETag = new EntityTagHeaderValue(entry.ETag);
            return response;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
