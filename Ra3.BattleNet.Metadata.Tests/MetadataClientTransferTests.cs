using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ra3.BattleNet.Metadata;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 正文传输口（<see cref="IMetadataTransfer"/>）的落盘契约：正文先落到传输方指定的本地文件，
/// 校验通过后搬进缓存（不再把整份正文读回内存再重写一遍），不通过、304、失败与取消都不许留下文件、
/// 也不许把半截正文当成缓存发布出去。
/// </summary>
[TestClass]
public class MetadataClientTransferTests
{
    private const string RootUrl = "https://metadata.example/metadata.xml";
    private const string LeafSource = "apps/leaf.xml";

    [TestMethod]
    public async Task Success_ParsesStagedFile_AdoptsIntoCache_AndDropsStagingFile()
    {
        var cache = NewDirectory("metadata-transfer-cache");
        var staging = NewDirectory("metadata-transfer-stage");
        var transfer = new ScriptedTransfer((request, _) =>
        {
            File.WriteAllText(request.StagingPath, RootXml(), Utf8NoBom);
            return Task.FromResult(new MetadataTransferResult(200, "\"v1\"", "Wed, 08 Oct 2026 10:00:00 GMT", "application/xml"));
        });
        using var client = Client(cache, transfer, staging);

        var root = await client.RefreshRootAsync(RootUrl);

        root.Status.Should().Be(MetadataFreshness.Fresh);
        root.Document.Should().NotBeNull();
        transfer.Requests.Should().HaveCount(1);
        var staged = transfer.Requests[0].StagingPath;
        Path.GetDirectoryName(staged).Should().Be(Path.GetFullPath(staging), "传输方必须写在调用方指定的临时目录里");
        File.Exists(staged).Should().BeFalse("校验通过的正文被搬进缓存，临时文件不再留下");

        // 关掉网络后还能读到根清单，证明正文真的落进了缓存（而不是只在内存里成功过一次）。
        var offline = new ScriptedTransfer((_, _) => throw new InvalidOperationException("缓存命中时不该联网"));
        using var reopened = Client(cache, offline, staging);
        var cached = await reopened.OpenSnapshotAsync(RootUrl);
        cached.Document.Should().NotBeNull();
        cached.Status.Should().Be(MetadataFreshness.Stale);
        offline.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task InvalidPayload_IsNotPublished_AndStagingFileIsRemoved()
    {
        var cache = NewDirectory("metadata-transfer-cache");
        var staging = NewDirectory("metadata-transfer-stage");
        var transfer = new ScriptedTransfer((request, _) =>
        {
            File.WriteAllText(request.StagingPath, "<Metadata><Application ID=\"x\">", Utf8NoBom);
            return Task.FromResult(new MetadataTransferResult(200, "\"v1\""));
        });
        using var client = Client(cache, transfer, staging);

        var root = await client.RefreshRootAsync(RootUrl);

        root.Status.Should().Be(MetadataFreshness.Unavailable);
        root.Error.Should().Contain("解析失败");
        File.Exists(transfer.Requests[0].StagingPath).Should().BeFalse();
        await AssertNothingPublishedAsync(cache, staging);
    }

    [TestMethod]
    public async Task TruncatedLeaf_IsNotPublished_AndStagingFileIsRemoved()
    {
        var cache = NewDirectory("metadata-transfer-cache");
        var staging = NewDirectory("metadata-transfer-stage");
        var transfer = new ScriptedTransfer((request, _) =>
        {
            var body = request.Source.AbsoluteUri == RootUrl
                ? RootXml()
                : LeafXml("app-leaf")[..40];
            File.WriteAllText(request.StagingPath, body, Utf8NoBom);
            return Task.FromResult(new MetadataTransferResult(200));
        });
        using var client = Client(cache, transfer, staging);
        var root = await client.RefreshRootAsync(RootUrl);
        var leafUri = MetadataResourceUri.Resolve(RootUrl, LeafSource);
        var before = transfer.Requests.Count;

        var leaf = await client.RefreshLeafAsync(root, "1.0.0", leafUri);

        leaf.Status.Should().Be(MetadataFreshness.Unavailable);
        leaf.Error.Should().Contain("解析失败");
        transfer.Requests.Skip(before).Should().OnlyContain(request => !File.Exists(request.StagingPath));
        Directory.EnumerateFiles(staging).Should().BeEmpty("半截叶子正文不能留在临时目录里");

        // 叶子没有进缓存：只读缓存也拿不到它。
        var cached = await client.GetCachedLeafAsync(root, "1.0.0", leafUri);
        cached.Status.Should().Be(MetadataFreshness.Unavailable);
    }

    [TestMethod]
    public async Task TransportFailure_LeavesNoFileAndPublishesNothing()
    {
        var cache = NewDirectory("metadata-transfer-cache");
        var staging = NewDirectory("metadata-transfer-stage");
        var transfer = new ScriptedTransfer((request, _) =>
        {
            File.WriteAllText(request.StagingPath, "<Metadata>半截正文", Utf8NoBom);
            throw new IOException("连接被重置");
        });
        using var client = Client(cache, transfer, staging);

        var root = await client.RefreshRootAsync(RootUrl);

        root.Status.Should().Be(MetadataFreshness.Unavailable);
        root.Error.Should().Contain("连接被重置");
        File.Exists(transfer.Requests[0].StagingPath).Should().BeFalse();
        await AssertNothingPublishedAsync(cache, staging);
    }

    [TestMethod]
    public async Task CallerCancellation_KeepsSharedTransfer_AndDisposeCleansStagingFile()
    {
        var cache = NewDirectory("metadata-transfer-cache");
        var staging = NewDirectory("metadata-transfer-stage");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = new ScriptedTransfer(async (request, token) =>
        {
            File.WriteAllText(request.StagingPath, RootXml(), Utf8NoBom);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new MetadataTransferResult(200);
        });
        using var client = Client(cache, transfer, staging);
        using var cancellation = new CancellationTokenSource();

        var caller = client.RefreshRootAsync(RootUrl, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var staged = transfer.Requests[0].StagingPath;
        File.Exists(staged).Should().BeTrue();

        // 调用方取消只取消自己的等待：已经发出的共享传输继续跑，临时正文这时还在。
        cancellation.Cancel();
        await FluentActions
            .Awaiting(() => caller)
            .Should().ThrowAsync<OperationCanceledException>();
        File.Exists(staged).Should().BeTrue("调用方取消不打断已发出的共享传输");

        // 释放会话才是取消共享传输：收尾必须清掉临时正文，也不许把半截正文发布出去。
        client.Dispose();
        await WaitUntilAsync(() => !File.Exists(staged), "取消共享传输后必须清掉临时正文");
        await AssertNothingPublishedAsync(cache, staging);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }

        condition().Should().BeTrue(because);
    }

    [TestMethod]
    public async Task NotModified_SendsCachedValidators_ReusesCacheWithoutFile()
    {
        var cache = NewDirectory("metadata-transfer-cache");
        var staging = NewDirectory("metadata-transfer-stage");
        var transfer = new ScriptedTransfer((request, _) =>
        {
            File.WriteAllText(request.StagingPath, RootXml(), Utf8NoBom);
            return Task.FromResult(new MetadataTransferResult(200, "\"v1\"", "Wed, 08 Oct 2026 10:00:00 GMT"));
        });
        using var client = Client(cache, transfer, staging);
        (await client.RefreshRootAsync(RootUrl)).Status.Should().Be(MetadataFreshness.Fresh);

        transfer.Next = (_, _) => Task.FromResult(new MetadataTransferResult(304));
        var again = await client.RefreshRootAsync(RootUrl);

        again.Status.Should().Be(MetadataFreshness.Fresh);
        again.Document.Should().NotBeNull("304 表示本地缓存仍然有效");
        var conditional = transfer.Requests[^1];
        conditional.ETag.Should().Be("\"v1\"");
        conditional.LastModified.Should().Be("Wed, 08 Oct 2026 10:00:00 GMT");
        File.Exists(conditional.StagingPath).Should().BeFalse("304 不落盘");
    }

    [TestMethod]
    public async Task ErrorStatus_IsReportedWithoutPublishing()
    {
        var cache = NewDirectory("metadata-transfer-cache");
        var staging = NewDirectory("metadata-transfer-stage");
        var transfer = new ScriptedTransfer((request, _) =>
        {
            File.WriteAllText(request.StagingPath, "<Metadata />", Utf8NoBom);
            return Task.FromResult(new MetadataTransferResult(404));
        });
        using var client = Client(cache, transfer, staging);

        var root = await client.RefreshRootAsync(RootUrl);

        root.Status.Should().Be(MetadataFreshness.Unavailable);
        root.Error.Should().Contain("HTTP 404");
        File.Exists(transfer.Requests[0].StagingPath).Should().BeFalse("非 200 不许留下正文");
        await AssertNothingPublishedAsync(cache, staging);
    }

    [TestMethod]
    public async Task NoDiskCache_SuccessReadsStagedFile_AndStillCleansItUp()
    {
        var staging = NewDirectory("metadata-transfer-stage");
        var transfer = new ScriptedTransfer((request, _) =>
        {
            File.WriteAllText(request.StagingPath, RootXml(), Utf8NoBom);
            return Task.FromResult(new MetadataTransferResult(200));
        });
        using var client = new MetadataClient(
            "",
            TimeSpan.FromSeconds(30),
            httpClient: null,
            enableDiskCache: false,
            transfer: transfer,
            stagingDirectory: staging);

        var root = await client.RefreshRootAsync(RootUrl);

        root.Status.Should().Be(MetadataFreshness.Fresh);
        File.Exists(transfer.Requests[0].StagingPath).Should().BeFalse();
        Directory.EnumerateFiles(staging).Should().BeEmpty("临时目录里不该留下任何正文");
    }

    private static async Task AssertNothingPublishedAsync(string cache, string staging)
    {
        var offline = new ScriptedTransfer((_, _) => throw new InvalidOperationException("缓存命中时不该联网"));
        using var reopened = Client(cache, offline, staging);
        var root = await reopened.OpenSnapshotAsync(RootUrl);
        root.Document.Should().BeNull("校验不通过的正文不能被当成缓存发布");
        offline.Requests.Should().BeEmpty();
        Directory.EnumerateFiles(staging).Should().BeEmpty();
    }

    private static MetadataClient Client(string cache, ScriptedTransfer transfer, string staging) =>
        new(cache, TimeSpan.FromSeconds(30), maxLeafConcurrency: 4, transfer: transfer, stagingDirectory: staging);

    private static string RootXml() => """
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata SchemaVersion="1.0" ContentRevision="1">
          <Application ID="RA3BattleNet">
            <Version>1.0.0</Version>
            <Packages>
              <Package Version="1.0.0"><Manifest>app-leaf</Manifest></Package>
            </Packages>
          </Application>
          <Manifest ID="app-leaf" Source="apps/leaf.xml" />
        </Metadata>
        """;

    private static string LeafXml(string id) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Metadata>
          <Manifest ID="{{id}}" HashAlgorithm="CRC32C">
            <File Hash="AABBCCDD" Size="1">
              <FileName>a.bin</FileName>
              <RelativePath>/</RelativePath>
              <KindOf>MISC;</KindOf>
            </File>
          </Manifest>
        </Metadata>
        """;

    private static string NewDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private sealed class ScriptedTransfer(Func<MetadataTransferRequest, CancellationToken, Task<MetadataTransferResult>> next)
        : IMetadataTransfer
    {
        public Func<MetadataTransferRequest, CancellationToken, Task<MetadataTransferResult>> Next { get; set; } = next;

        public List<MetadataTransferRequest> Requests { get; } = [];

        public Task<MetadataTransferResult> FetchAsync(MetadataTransferRequest request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request);
            return Next(request, cancellationToken);
        }

        public void Dispose()
        {
        }
    }
}
