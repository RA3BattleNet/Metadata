using Ra3.BattleNet.Metadata;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 共享加载与最后有效缓存的对外契约。不覆盖内部文件布局细节。
/// </summary>
[TestClass]
public class MetadataClientTests
{
    private const string RootUrl = "https://metadata.example/metadata.xml";

    [TestMethod]
    public async Task OpenSnapshot_WithoutCache_IsUnavailableAndDoesNotUseNetwork()
    {
        using var fixture = Fixture.Create();
        var opened = await fixture.Client.OpenSnapshotAsync(RootUrl);

        opened.Status.Should().Be(MetadataFreshness.Unavailable);
        opened.Document.Should().BeNull();
        opened.Error.Should().NotBeNullOrWhiteSpace();
        fixture.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RefreshRoot_ConcurrentCallers_ShareOneRequestAndDocument()
    {
        using var fixture = Fixture.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Next = async (_, _) =>
        {
            await release.Task;
            return Xml(RootXml(), "\"v1\"");
        };

        var first = fixture.Client.RefreshRootAsync(RootUrl);
        var second = fixture.Client.RefreshRootAsync(RootUrl);
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        fixture.Calls.Should().ContainSingle();
        results[0].Should().BeSameAs(results[1]);
        results[0].Status.Should().Be(MetadataFreshness.Fresh);
        results[0].Document.Should().NotBeNull();
        results[0].Catalog.Should().NotBeNull();
        results[0].OriginUri!.AbsoluteUri.Should().Be(RootUrl);
    }

    [TestMethod]
    public async Task OpenSnapshot_AfterRefresh_IsStale_AndFailureDoesNotStayFresh()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (_, _) => Task.FromResult(Xml(RootXml(), "\"v1\""));
        var fresh = await fixture.Client.RefreshRootAsync(RootUrl);

        var opened = await fixture.Client.OpenSnapshotAsync(RootUrl);
        opened.Status.Should().Be(MetadataFreshness.Stale);
        opened.Document.Should().BeSameAs(fresh.Document);
        opened.RefreshId.Should().NotBe(fresh.RefreshId);
        opened.Error.Should().BeNull();

        fixture.Handler.Next = (_, _) => throw new HttpRequestException("down");
        var failed = await fixture.Client.RefreshRootAsync(RootUrl);
        failed.Status.Should().Be(MetadataFreshness.Stale);
        failed.Document.Should().BeSameAs(fresh.Document);
        failed.Error.Should().NotBeNullOrWhiteSpace();
        failed.RefreshId.Should().NotBe(fresh.RefreshId);
    }

    [TestMethod]
    public async Task MissingValidator_DoesNotSendConditional_AndBodyStaysReadable()
    {
        var cache = NewCache();
        var handler = new ScriptedHandler((_, _) => Task.FromResult(Xml(RootXml(), "\"v1\"")));
        using var http = new HttpClient(handler);
        using (var first = Client(cache, http))
            (await first.RefreshRootAsync(RootUrl)).Status.Should().Be(MetadataFreshness.Fresh);

        var etag = Directory.EnumerateFiles(cache, "metadata.xml.etag", SearchOption.AllDirectories).Single();
        File.Delete(etag);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(etag)!, "metadata.xml.tmp"), "<not-a-cache/>");
        handler.Calls.Clear();
        using var second = Client(cache, http);
        var opened = await second.OpenSnapshotAsync(RootUrl);
        opened.Status.Should().Be(MetadataFreshness.Stale);
        opened.Document.Should().NotBeNull();
        handler.Calls.Should().BeEmpty();

        var refreshed = await second.RefreshRootAsync(RootUrl);
        refreshed.Status.Should().Be(MetadataFreshness.Fresh);
        handler.Calls.Should().ContainSingle();
        handler.Calls[0].Conditional.Should().BeFalse();
        try
        {
            Directory.Delete(cache, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不改变断言。
        }
    }

    [TestMethod]
    public async Task NotModified_WithVerifiedCache_ReusesDocument()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (_, _) => Task.FromResult(Xml(RootXml(), "\"v1\""));
        var fresh = await fixture.Client.RefreshRootAsync(RootUrl);
        fixture.Handler.Next = (request, _) =>
        {
            (request.Headers.IfNoneMatch.Any() || request.Headers.Contains("If-None-Match")).Should().BeTrue();
            return Task.FromResult(NotModified());
        };

        var again = await fixture.Client.RefreshRootAsync(RootUrl);

        again.Status.Should().Be(MetadataFreshness.Fresh);
        again.Document.Should().BeSameAs(fresh.Document);
        again.Catalog.Should().BeSameAs(fresh.Catalog);
        again.RefreshId.Should().NotBe(fresh.RefreshId);
        fixture.Calls.Should().HaveCount(2);
        fixture.Calls[1].Conditional.Should().BeTrue();
    }

    [TestMethod]
    public async Task NotModified_WhenBodyMissing_RetriesUnconditionalOnce()
    {
        var cache = NewCache();
        var mode = 0;
        using var fixture = Fixture.Create(cache);
        fixture.Handler.Next = (_, _) =>
        {
            var call = Interlocked.Increment(ref mode);
            if (call == 2)
            {
                File.Delete(Directory.EnumerateFiles(cache, "metadata.xml", SearchOption.AllDirectories).Single());
                return Task.FromResult(NotModified());
            }

            return Task.FromResult(Xml(RootXml(), "\"v1\""));
        };

        (await fixture.Client.RefreshRootAsync(RootUrl)).Status.Should().Be(MetadataFreshness.Fresh);
        var repaired = await fixture.Client.RefreshRootAsync(RootUrl);

        repaired.Status.Should().Be(MetadataFreshness.Fresh);
        repaired.Document.Should().NotBeNull();
        fixture.Calls.Should().HaveCount(3);
        fixture.Calls[1].Conditional.Should().BeTrue();
        fixture.Calls[2].Conditional.Should().BeFalse();
    }

    [TestMethod]
    public async Task Preload_FetchesRegisteredPackageLeavesOnly()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == RootUrl)
                return Task.FromResult(Xml(RootXml(), "\"v1\""));
            var id = uri switch
            {
                "https://metadata.example/apps/leaf.xml" => "app-leaf",
                "https://metadata.example/apps/old.xml" => "app-old",
                "https://metadata.example/mods/current.xml" => "mod-current",
                "https://metadata.example/mods/old.xml" => "mod-old",
                _ => "",
            };
            id.Should().NotBeNullOrEmpty($"不应预取 {uri}");
            return Task.FromResult(Xml(LeafXml(id)));
        };

        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        await fixture.Client.PreloadLeavesAsync(root);

        fixture.Calls.Select(call => call.Uri).Should().BeEquivalentTo(
            RootUrl,
            "https://metadata.example/apps/leaf.xml",
            "https://metadata.example/apps/old.xml",
            "https://metadata.example/mods/current.xml",
            "https://metadata.example/mods/old.xml");
    }

    [TestMethod]
    public async Task GetLeaf_MemoryHit_DoesNotRefetch_RefreshLeafDoes()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(
            request.RequestUri!.AbsoluteUri == RootUrl ? RootXml() : LeafXml("app-leaf"),
            "\"v1\""));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        var leafUri = MetadataResourceUri.Resolve(RootUrl, "apps/leaf.xml");
        var loaded = await fixture.Client.RefreshLeafAsync(root, "1.0.0", leafUri);
        var before = fixture.Calls.Count;

        var shared = await fixture.Client.GetLeafAsync(root, "1.0.0", leafUri);
        shared.Document.Should().BeSameAs(loaded.Document);
        shared.ManifestNode.Should().BeSameAs(loaded.ManifestNode);
        shared.Entry.Should().BeSameAs(loaded.Entry);
        fixture.Calls.Should().HaveCount(before);

        fixture.Handler.Next = (request, _) =>
        {
            if (request.Headers.IfNoneMatch.Any())
                return Task.FromResult(NotModified());
            return Task.FromResult(Xml(LeafXml("app-leaf"), "\"v1\""));
        };
        var refreshed = await fixture.Client.RefreshLeafAsync(root, "1.0.0", leafUri);
        refreshed.Status.Should().Be(MetadataFreshness.Fresh);
        refreshed.ManifestNode.Should().BeSameAs(loaded.ManifestNode);
        fixture.Calls.Should().HaveCount(before + 1);
    }

    [TestMethod]
    public async Task Leaf_RejectsStubIncludeAndMissingFile_AcceptsEmptyManifest()
    {
        await AssertLeafRejected(StubXml(), "存根");
        await AssertLeafRejected(IncludeXml(), "Include");
        await AssertLeafRejected(MissingHashXml(), "FileName 或 Hash");

        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(
            request.RequestUri!.AbsoluteUri == RootUrl ? RootXml() : EmptyLeafXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        var leaf = await fixture.Client.RefreshLeafAsync(root, "1.0.0", MetadataResourceUri.Resolve(RootUrl, "apps/leaf.xml"));
        leaf.Status.Should().Be(MetadataFreshness.Fresh);
        leaf.Entry!.Files.Should().BeEmpty();
        leaf.ManifestNode.Should().NotBeNull();
    }

    [TestMethod]
    public async Task Leaf_VersionMismatch_DoesNotFetch()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (_, _) => Task.FromResult(Xml(RootXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        fixture.Calls.Clear();

        var leaf = await fixture.Client.GetLeafAsync(root, "9.9.9", MetadataResourceUri.Resolve(RootUrl, "apps/leaf.xml"));

        leaf.Status.Should().Be(MetadataFreshness.Unavailable);
        leaf.Entry.Should().BeNull();
        fixture.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SourceChange_UsesNewUri_AndKeepsCapturedUriDistinct()
    {
        using var fixture = Fixture.Create();
        var generation = 0;
        fixture.Handler.Next = (request, _) =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == RootUrl)
            {
                var body = generation == 0 ? RootXml() : RootXml("apps/b.xml", "leaf-b");
                return Task.FromResult(Xml(body, "\"v" + generation + "\""));
            }

            var id = uri.EndsWith("/b.xml", StringComparison.Ordinal) ? "leaf-b" : "app-leaf";
            var file = uri.EndsWith("/b.xml", StringComparison.Ordinal) ? "b.bin" : "a.bin";
            return Task.FromResult(Xml(LeafXml(id, file)));
        };

        var capturedRoot = await fixture.Client.RefreshRootAsync(RootUrl);
        var oldUri = MetadataResourceUri.Resolve(RootUrl, "apps/leaf.xml");
        var oldLeaf = await fixture.Client.RefreshLeafAsync(capturedRoot, "1.0.0", oldUri);
        generation = 1;
        var newer = await fixture.Client.RefreshRootAsync(RootUrl);
        var newUri = MetadataResourceUri.Resolve(RootUrl, "apps/b.xml");
        var before = fixture.Calls.Count;

        var captured = await fixture.Client.GetLeafAsync(capturedRoot, "1.0.0", oldUri);
        captured.Document.Should().BeSameAs(oldLeaf.Document);
        fixture.Calls.Should().HaveCount(before);

        var moved = await fixture.Client.GetLeafAsync(newer, "1.0.0", newUri);
        moved.Document.Should().NotBeSameAs(oldLeaf.Document);
        moved.Entry!.Files[0].FileName.Should().Be("b.bin");
        moved.SourceUri.AbsoluteUri.Should().Be(newUri.AbsoluteUri);
    }

    [TestMethod]
    public async Task CallerCancel_DoesNotCancelSharedRefresh()
    {
        using var fixture = Fixture.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Next = async (_, _) =>
        {
            await release.Task;
            return Xml(RootXml(), "\"v1\"");
        };
        var first = fixture.Client.RefreshRootAsync(RootUrl);
        using var cts = new CancellationTokenSource();
        var second = fixture.Client.RefreshRootAsync(RootUrl, cts.Token);
        cts.Cancel();

        var act = async () => await second;
        await act.Should().ThrowAsync<OperationCanceledException>();
        release.TrySetResult();
        var result = await first;

        result.Status.Should().Be(MetadataFreshness.Fresh);
        fixture.Calls.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Dispose_CancelsSharedFetch()
    {
        using var fixture = Fixture.Create(timeout: TimeSpan.FromSeconds(5));
        fixture.Handler.Next = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Xml(RootXml());
        };
        var pending = fixture.Client.RefreshRootAsync(RootUrl);
        fixture.Client.Dispose();

        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task FileEntry_RefreshIsFresh_AndResolvesLeavesAgainstOriginNotCache()
    {
        var source = Path.Combine(Path.GetTempPath(), "metadata-file-" + Guid.NewGuid().ToString("N"));
        var cache = NewCache();
        Directory.CreateDirectory(Path.Combine(source, "apps"));
        var metadataPath = Path.Combine(source, "metadata.xml");
        File.WriteAllText(metadataPath, RootXml(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(source, "apps", "leaf.xml"), LeafXml("app-leaf"), new UTF8Encoding(false));
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("file 入口不应走 HTTP"));
        using var http = new HttpClient(handler);
        using var client = Client(cache, http, TimeSpan.FromSeconds(5));
        try
        {
            var root = await client.RefreshRootAsync(metadataPath);
            root.Status.Should().Be(MetadataFreshness.Fresh);
            root.OriginUri!.IsFile.Should().BeTrue();
            root.OriginUri.LocalPath.TrimEnd('\\', '/').Should().Be(Path.GetFullPath(metadataPath).TrimEnd('\\', '/'));
            handler.Calls.Should().BeEmpty();

            var opened = await client.OpenSnapshotAsync(metadataPath);
            opened.Status.Should().Be(MetadataFreshness.Stale);
            opened.Document.Should().BeSameAs(root.Document);

            var leafUri = MetadataResourceUri.Resolve(root.OriginUri.AbsoluteUri, "apps/leaf.xml");
            leafUri.LocalPath.Should().StartWith(Path.GetFullPath(source));
            var leaf = await client.RefreshLeafAsync(root, "1.0.0", leafUri);
            leaf.Status.Should().Be(MetadataFreshness.Fresh);
            leaf.Entry!.Id.Should().Be("app-leaf");
            handler.Calls.Should().BeEmpty();

            var other = await client.OpenSnapshotAsync("https://other.example/metadata.xml");
            other.Status.Should().Be(MetadataFreshness.Unavailable);
            other.Document.Should().BeNull();
        }
        finally
        {
            TryDelete(source);
            TryDelete(cache);
        }
    }

    private static async Task AssertLeafRejected(string leafXml, string errorFragment)
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(
            request.RequestUri!.AbsoluteUri == RootUrl ? RootXml() : leafXml));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        var leaf = await fixture.Client.RefreshLeafAsync(root, "1.0.0", MetadataResourceUri.Resolve(RootUrl, "apps/leaf.xml"));
        leaf.Status.Should().Be(MetadataFreshness.Unavailable);
        leaf.Entry.Should().BeNull();
        leaf.Error.Should().Contain(errorFragment);
        if (Directory.Exists(fixture.Cache))
            Directory.EnumerateFiles(fixture.Cache, "leaf.xml", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [TestMethod]
    public async Task IncompatibleSchema_DoesNotReplaceLastGood()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (_, _) => Task.FromResult(Xml(RootXml(), "\"v1\""));
        var fresh = await fixture.Client.RefreshRootAsync(RootUrl);
        fixture.Handler.Next = (_, _) => Task.FromResult(Xml(RootXml().Replace("SchemaVersion=\"1.0\"", "SchemaVersion=\"2.0\"", StringComparison.Ordinal), "\"v2\""));

        var rejected = await fixture.Client.RefreshRootAsync(RootUrl);

        rejected.Status.Should().Be(MetadataFreshness.Stale);
        rejected.Document.Should().BeSameAs(fresh.Document);
        rejected.Error.Should().Contain("不兼容");
        using var later = Client(fixture.Cache, fixture.Http);
        var opened = await later.OpenSnapshotAsync(RootUrl);
        opened.Status.Should().Be(MetadataFreshness.Stale);
        opened.Document!.Get("SchemaVersion").Should().Be("1.0");
        later.Dispose();
    }

    [TestMethod]
    public async Task OtherOrigin_DoesNotEraseCachedRoot()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(
            request.RequestUri!.Host == "other.example"
                ? """
                  <?xml version="1.0" encoding="utf-8"?>
                  <Metadata SchemaVersion="1.0" ContentRevision="b">
                    <Application ID="Other"><Version>9.0</Version></Application>
                  </Metadata>
                  """
                : RootXml(),
            "\"v1\""));
        await fixture.Client.RefreshRootAsync(RootUrl);
        (await fixture.Client.RefreshRootAsync("https://other.example/metadata.xml")).Status.Should().Be(MetadataFreshness.Fresh);

        using var later = Client(fixture.Cache, fixture.Http);
        var opened = await later.OpenSnapshotAsync(RootUrl);
        opened.Document!.Catalog().Application("RA3BattleNet").Should().NotBeNull();
        opened.Document!.Catalog().Application("Other").Should().BeNull();
        later.Dispose();
    }

    [TestMethod]
    public async Task NotModified_WhenVerifiedBodyDoesNotParse_RetriesUnconditionalOnce()
    {
        using var fixture = Fixture.Create();
        var phase = 0;
        fixture.Handler.Next = (_, _) =>
        {
            var call = Interlocked.Increment(ref phase);
            if (call == 2)
                return Task.FromResult(NotModified());
            return Task.FromResult(Xml(RootXml(), "\"v1\""));
        };
        (await fixture.Client.RefreshRootAsync(RootUrl)).Status.Should().Be(MetadataFreshness.Fresh);
        ReplaceVerifiedBody(fixture.Cache, "metadata.xml", Encoding.UTF8.GetBytes("<not-xml"));

        var repaired = await fixture.Client.RefreshRootAsync(RootUrl);

        repaired.Status.Should().Be(MetadataFreshness.Fresh);
        repaired.Document!.Get("SchemaVersion").Should().Be("1.0");
        fixture.Calls.Should().HaveCount(3);
        fixture.Calls[1].Conditional.Should().BeTrue();
        fixture.Calls[2].Conditional.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetLeaf_SameUriDifferentManifestId_DoesNotReturnWrongEntry()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(
            request.RequestUri!.AbsoluteUri == RootUrl ? SharedSourceRoot() : BothIdsLeafXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        var leafUri = MetadataResourceUri.Resolve(RootUrl, "apps/leaf.xml");
        var first = await fixture.Client.GetLeafAsync(root, "1.0.0", leafUri);
        var before = fixture.Calls.Count;
        var second = await fixture.Client.GetLeafAsync(root, "2.0.0", leafUri);
        var missing = await fixture.Client.GetLeafAsync(root, "3.0.0", leafUri);

        first.Entry!.Id.Should().Be("id-a");
        second.Entry!.Id.Should().Be("id-b");
        second.Document.Should().BeSameAs(first.Document);
        fixture.Calls.Should().HaveCount(before);
        missing.Status.Should().Be(MetadataFreshness.Unavailable);
        missing.Entry.Should().BeNull();
        missing.ManifestNode.Should().BeNull();
    }

    [TestMethod]
    public async Task SameVersionSameUri_CapturedRoots_KeepTheirOwnManifestId()
    {
        using var fixture = Fixture.Create();
        var generation = 0;
        fixture.Handler.Next = (request, _) => Task.FromResult(Xml(
            request.RequestUri!.AbsoluteUri == RootUrl
                ? IdRoot(generation == 0 ? "id-a" : "id-b")
                : BothIdsLeafXml()));
        var rootA = await fixture.Client.RefreshRootAsync(RootUrl);
        var uri = MetadataResourceUri.Resolve(RootUrl, "apps/leaf.xml");
        var fromA = await fixture.Client.GetLeafAsync(rootA, "1.0.0", uri);
        var before = fixture.Calls.Count;
        generation = 1;
        var rootB = await fixture.Client.RefreshRootAsync(RootUrl);
        var fromB = await fixture.Client.GetLeafAsync(rootB, "1.0.0", uri);
        var againA = await fixture.Client.GetLeafAsync(rootA, "1.0.0", uri);

        fromA.Entry!.Id.Should().Be("id-a");
        fromB.Entry!.Id.Should().Be("id-b");
        fromB.Document.Should().BeSameAs(fromA.Document);
        againA.Entry.Should().BeSameAs(fromA.Entry);
        fromB.Entry.Should().NotBeSameAs(fromA.Entry);
        fixture.Calls.Should().HaveCount(before + 1);
    }


    [TestMethod]
    public async Task GetLeaf_Canceled_DoesNotFetch()
    {
        using var fixture = Fixture.Create();
        fixture.Handler.Next = (_, _) => Task.FromResult(Xml(RootXml()));
        var root = await fixture.Client.RefreshRootAsync(RootUrl);
        fixture.Calls.Clear();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await fixture.Client.GetLeafAsync(
            root, "1.0.0", MetadataResourceUri.Resolve(RootUrl, "apps/leaf.xml"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Calls.Should().BeEmpty();
    }

    private static string SharedSourceRoot() => """
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata SchemaVersion="1.0" ContentRevision="1">
          <Application ID="RA3BattleNet">
            <Packages>
              <Package Version="1.0.0"><Manifest>id-a</Manifest></Package>
              <Package Version="2.0.0"><Manifest>id-b</Manifest></Package>
              <Package Version="3.0.0"><Manifest>id-c</Manifest></Package>
            </Packages>
          </Application>
          <Manifest ID="id-a" Source="apps/leaf.xml" />
          <Manifest ID="id-b" Source="apps/leaf.xml" />
          <Manifest ID="id-c" Source="apps/leaf.xml" />
        </Metadata>
        """;

    private static string IdRoot(string manifestId) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata SchemaVersion="1.0" ContentRevision="1">
          <Application ID="RA3BattleNet">
            <Packages>
              <Package Version="1.0.0"><Manifest>{{manifestId}}</Manifest></Package>
            </Packages>
          </Application>
          <Manifest ID="{{manifestId}}" Source="apps/leaf.xml" />
        </Metadata>
        """;


    private static string BothIdsLeafXml() => """
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata>
          <Manifest ID="id-a" HashAlgorithm="CRC32C">
            <File Hash="AABBCCDD" Size="1">
              <FileName>a.bin</FileName>
              <RelativePath>/</RelativePath>
              <KindOf>MISC;</KindOf>
            </File>
          </Manifest>
          <Manifest ID="id-b" HashAlgorithm="CRC32C">
            <File Hash="BBCCDDEE" Size="2">
              <FileName>b.bin</FileName>
              <RelativePath>/</RelativePath>
              <KindOf>MISC;</KindOf>
            </File>
          </Manifest>
        </Metadata>
        """;

    private static void ReplaceVerifiedBody(string cache, string fileName, byte[] body)
    {
        var path = Directory.EnumerateFiles(cache, fileName, SearchOption.AllDirectories).Single();
        File.WriteAllBytes(path, body);
        var stamp = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path + ".etag"))!.AsObject();
        stamp["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        File.WriteAllText(path + ".etag", stamp.ToJsonString());
    }

    private static string RootXml(string currentSource = "apps/leaf.xml", string currentId = "app-leaf") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata SchemaVersion="1.0" ContentRevision="1">
          <Application ID="RA3BattleNet">
            <Version>1.0.0</Version>
            <Packages>
              <Package Version="1.0.0"><Manifest>{{currentId}}</Manifest></Package>
              <Package Version="0.9.0"><Manifest>app-old</Manifest></Package>
            </Packages>
            <UpdateKind ID="RA3BattleNet" DisplayName="客户端" Current="1.0.0" BaseUrl="https://update.example/app/" FallbackBaseUrl="">
              <Updater Version="1.0.0" Source="https://update.example/app/manifests/manifest-1.0.0.xml" />
            </UpdateKind>
          </Application>
          <Mod ID="Corona">
            <CurrentVersion>3.258</CurrentVersion>
            <Packages>
              <Package Version="3.258"><Manifest>mod-current</Manifest></Package>
              <Package Version="3.200"><Manifest>mod-old</Manifest></Package>
            </Packages>
          </Mod>
          <Manifest ID="{{currentId}}" Source="{{currentSource}}" />
          <Manifest ID="app-old" Source="apps/old.xml" />
          <Manifest ID="mod-current" Source="mods/current.xml" />
          <Manifest ID="mod-old" Source="mods/old.xml" />
          <Image ID="icon" Source="images/icon.png" />
          <Markdown ID="news" Source="posts/news.md" />
        </Metadata>
        """;

    private static string LeafXml(string id, string fileName = "a.bin") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata>
          <Manifest ID="{{id}}" HashAlgorithm="CRC32C">
            <File Hash="AABBCCDD" Size="1">
              <FileName>{{fileName}}</FileName>
              <RelativePath>/</RelativePath>
              <KindOf>MISC;</KindOf>
            </File>
          </Manifest>
        </Metadata>
        """;

    private static string EmptyLeafXml() => """
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata>
          <Manifest ID="app-leaf" HashAlgorithm="CRC32C" />
        </Metadata>
        """;

    private static string StubXml() => """
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata>
          <Manifest ID="app-leaf" Source="apps/leaf.xml" />
        </Metadata>
        """;

    private static string IncludeXml() => """
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata>
          <Include Source="other.xml" />
          <Manifest ID="app-leaf" HashAlgorithm="CRC32C" />
        </Metadata>
        """;

    private static string MissingHashXml() => """
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata>
          <Manifest ID="app-leaf" HashAlgorithm="CRC32C">
            <File>
              <FileName>a.bin</FileName>
              <RelativePath>/</RelativePath>
            </File>
          </Manifest>
        </Metadata>
        """;

    private static HttpResponseMessage Xml(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        if (etag is not null)
            response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    private static HttpResponseMessage NotModified() => new(HttpStatusCode.NotModified);

    private static string NewCache() => Path.Combine(Path.GetTempPath(), "metadata-cache-" + Guid.NewGuid().ToString("N"));

    private static MetadataClient Client(string cache, HttpClient http, TimeSpan? timeout = null) =>
        new(cache, timeout ?? TimeSpan.FromSeconds(30), maxLeafConcurrency: 4, http);

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不改变断言。
        }
    }

    private sealed class Fixture : IDisposable
    {
        public required string Cache { get; init; }

        public required ScriptedHandler Handler { get; init; }

        public required HttpClient Http { get; init; }

        public required MetadataClient Client { get; init; }

        public List<Call> Calls => Handler.Calls;

        public static Fixture Create(string? cache = null, TimeSpan? timeout = null)
        {
            cache ??= NewCache();
            var handler = new ScriptedHandler();
            var http = new HttpClient(handler);
            return new Fixture
            {
                Cache = cache,
                Handler = handler,
                Http = http,
                Client = Client(cache, http, timeout),
            };
        }

        public void Dispose()
        {
            Client.Dispose();
            Http.Dispose();
            TryDelete(Cache);
        }
    }

    private sealed record Call(string Uri, bool Conditional);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? next = null)
        {
            if (next is not null)
                Next = next;
        }

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Next { get; set; } =
            (_, _) => throw new InvalidOperationException("测试没有安排响应");

        public List<Call> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = new Call(
                request.RequestUri?.AbsoluteUri ?? "",
                request.Headers.IfNoneMatch.Any() || request.Headers.IfModifiedSince is not null || request.Headers.Contains("If-None-Match"));
            lock (Calls)
                Calls.Add(call);
            return await Next(request, cancellationToken);
        }
    }
}
