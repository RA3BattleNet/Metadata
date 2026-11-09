using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Ra3.BattleNet.Metadata;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 根清单、包叶子与登记图片的共享加载器。叶子与图片身份只来自调用方传入的快照。
/// 相同 Source 的正文只拉一次，每个调用再按自己的 Manifest ID 投影。
/// </summary>
public sealed class MetadataClient : IDisposable
{
    private static readonly TimeSpan ImageReadCooldown = TimeSpan.FromMinutes(10);

    private readonly MetadataDiskCache? _disk;
    private readonly IMetadataTransfer _transfer;
    private readonly bool _ownsTransfer;
    private readonly string _stagingDirectory;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _leafSlots;
    private readonly SemaphoreSlim _transferSlots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<RootSnapshotResult>> _openFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<RootSnapshotResult>> _refreshFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<LeafDoc>> _leafFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<LeafDoc>> _leafReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ImageResult>> _imageFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _imageReadBlockedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _liveImageDigests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RootMemory> _rootDocs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LeafDoc> _leaves = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="cacheDirectory">磁盘缓存根目录；<paramref name="enableDiskCache"/> 为 false 时不读取、不创建。</param>
    /// <param name="requestTimeout">单次请求超时；排在并发下载限制后面等名额的时间不算，拿到名额后才开始计时。</param>
    /// <param name="maxLeafConcurrency">叶子并发上限。</param>
    /// <param name="httpClient">外部传输实例；为 null 时自己建一个并负责释放。</param>
    /// <param name="enableDiskCache">
    /// false 表示这次会话完全不碰磁盘：不建缓存目录、不读已有 XML 与图片、不写临时文件或旁挂文件、
    /// 不发送由缓存产生的条件请求，也不把生产缓存当作失败回退。本地与测试来源用这个模式，
    /// 保证改完本地源立刻能看到新内容。
    /// </param>
    /// <param name="clock">图片下载失败后的冷却倒计时时钟。默认使用系统时间，单元测试可传入模拟时钟。</param>
    /// <param name="transfer">
    /// 正文传输实现。为 null 时用内置的 <see cref="HttpClient"/> 传输（客户端自己建 HttpClient 并负责释放）；
    /// 调用方也可以注入自己的实现——正文先落到 <paramref name="stagingDirectory"/> 下的临时文件，
    /// 校验通过后再搬进缓存。XML、图片与 Markdown 都只有这一条传输路径。
    /// </param>
    /// <param name="stagingDirectory">
    /// 下载临时目录。为 null 时：开磁盘缓存用 &lt;缓存根&gt;/.stage，关磁盘缓存用系统临时目录
    /// （后者只适合独立使用与测试）。目录在第一次真正需要网络传输时才创建，本地来源不会碰它。
    /// </param>
    /// <param name="maxTransferConcurrency">
    /// 网络下载并发上限，默认 3。元数据 XML（根与叶子）、登记图片与元数据 Markdown 走同一个上限，
    /// 调用方注入的下载实现也一样；本地 file:// 读取不占名额。
    /// </param>
    public MetadataClient(
        string cacheDirectory,
        TimeSpan requestTimeout,
        int maxLeafConcurrency = 4,
        HttpClient? httpClient = null,
        bool enableDiskCache = true,
        TimeProvider? clock = null,
        IMetadataTransfer? transfer = null,
        string? stagingDirectory = null,
        int maxTransferConcurrency = 3)
    {
        if (enableDiskCache && string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("缓存目录不能为空", nameof(cacheDirectory));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero, nameof(requestTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLeafConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTransferConcurrency);

        _requestTimeout = requestTimeout;
        _clock = clock ?? TimeProvider.System;
        if (enableDiskCache)
        {
            _disk = new MetadataDiskCache(cacheDirectory);
            Directory.CreateDirectory(cacheDirectory);
        }

        _ownsTransfer = transfer is null;
        _transfer = transfer ?? new HttpMetadataTransfer(httpClient, requestTimeout);
        _stagingDirectory = ResolveStagingDirectory(cacheDirectory, enableDiskCache, stagingDirectory);
        _leafSlots = new SemaphoreSlim(maxLeafConcurrency, maxLeafConcurrency);
        _transferSlots = new SemaphoreSlim(maxTransferConcurrency, maxTransferConcurrency);
    }

    private static string ResolveStagingDirectory(string cacheDirectory, bool enableDiskCache, string? stagingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(stagingDirectory))
            return Path.GetFullPath(stagingDirectory);
        if (enableDiskCache)
            return Path.Combine(Path.GetFullPath(cacheDirectory), ".stage");
        return Path.Combine(Path.GetTempPath(), "ra3-metadata-stage");
    }

    /// <summary>
    /// 图片下载完成、或者内容发生真实变化后触发。订阅方收到的是图片完整 URL。
    /// 每次更新只触发一次；如果服务器返回 304、请求失败或者文件内容没变，都不会触发。
    /// 触发时新图片已写入本地缓存，事件回调中再次读取会直接命中缓存，不会重复发请求。
    /// </summary>
    public event EventHandler<ImageUpdatedEventArgs>? ImageUpdated;

    /// <summary>只读本地最后有效缓存，不访问网络，也不把历史数据标成 Fresh。</summary>
    public Task<RootSnapshotResult> OpenSnapshotAsync(string metadataUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var origin = RequireEntry(metadataUrl);
        return Share(_openFlights, origin.AbsoluteUri, token => OpenCore(origin, token), ct);
    }

    /// <summary>条件刷新根清单。file:// 和本地路径按源文件读取，成功即为 Fresh。</summary>
    public Task<RootSnapshotResult> RefreshRootAsync(string metadataUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var origin = RequireEntry(metadataUrl);
        return Share(_refreshFlights, origin.AbsoluteUri, token => RefreshRootCore(origin, token), ct);
    }

    public Task<LeafResult> GetPackageAsync(RootSnapshotResult snapshot, PackageEntry package, CancellationToken ct = default) =>
        ReadPackageAsync(snapshot, package, cachedOnly: false, force: false, ct);

    public Task<LeafResult> GetCachedPackageAsync(RootSnapshotResult snapshot, PackageEntry package, CancellationToken ct = default) =>
        ReadPackageAsync(snapshot, package, cachedOnly: true, force: false, ct);

    public Task<LeafResult> RefreshPackageAsync(RootSnapshotResult snapshot, PackageEntry package, CancellationToken ct = default) =>
        ReadPackageAsync(snapshot, package, cachedOnly: false, force: true, ct);

    private async Task<LeafResult> ReadPackageAsync(RootSnapshotResult snapshot, PackageEntry package, bool cachedOnly, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(package);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (snapshot.Document is null || snapshot.OriginUri is null
            || !EnumeratePackages(snapshot.Document).Any(candidate => ReferenceEquals(candidate.Raw, package.Raw)))
            return LeafUnavailable(null, "Package does not belong to this snapshot");
        var registration = snapshot.Document.ManifestRegistration(package.ManifestId ?? "");
        if (registration is null)
            return LeafUnavailable(null, "Package has no registered manifest");
        if (!IsStub(registration))
        {
            if (!TryReadLeaf(snapshot.Document, package.ManifestId!, out var node, out var entry, out var error))
                return LeafUnavailable(null, error ?? "Invalid inline manifest");
            return new LeafResult(snapshot.Document, node, entry, null, snapshot.Status, snapshot.Error);
        }
        if (!TryResolvePackage(snapshot.Document, snapshot.OriginUri, package, out var canonical, out var manifestId))
            return LeafUnavailable(null, "Package has no valid manifest Source");
        var source = new Uri(canonical);
        if (cachedOnly)
        {
            if (TryMemoryLeaf(source, SourceRoot(snapshot)) is { } memory && (_disk is not null || !source.IsFile))
                return ProjectCached(memory, manifestId);
            return await LoadCachedLeafAsync(source, SourceRoot(snapshot), manifestId, ct).ConfigureAwait(false);
        }
        return Project(await EnsureLeafAsync(source, SourceRoot(snapshot), force, ct).ConfigureAwait(false), manifestId);
    }

    /// <summary>
    /// 刷新快照里全部 Application 与 Mod 的当前和历史包叶子，并回报这一轮有几个叶子没拿到可用文档。
    /// 相同 Source 只拉一次。不读取图片、Markdown 或 Updater 清单。
    /// 单个叶子的网络失败、超时或解析失败不影响其余叶子，只计进 <see cref="LeafPreloadSummary.Failed"/>；
    /// 只有取消和客户端已释放才会往外抛。没有摘要的话，调用方看到的永远是"拉完了"，分不清全挂。
    /// </summary>
    public async Task<LeafPreloadSummary> PreloadLeavesAsync(RootSnapshotResult rootSnapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootSnapshot);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var leaves = EnumerateLeaves(rootSnapshot);
        var inline = rootSnapshot.Document is null ? new List<PackageEntry>() : EnumeratePackages(rootSnapshot.Document)
            .Where(package => rootSnapshot.Document.ManifestRegistration(package.ManifestId ?? "") is { } manifest && !IsStub(manifest))
            .DistinctBy(package => package.ManifestId, StringComparer.OrdinalIgnoreCase).ToList();
        var inlineFailed = 0;
        foreach (var package in inline)
        {
            if (!TryReadLeaf(rootSnapshot.Document!, package.ManifestId!, out _, out _, out _))
                inlineFailed++;
        }
        if (leaves.Count == 0)
            return new LeafPreloadSummary(inline.Count, inline.Count - inlineFailed, inlineFailed);

        var waits = new List<Task<LeafDoc>>(leaves.Count);
        foreach (var leaf in leaves)
        {
            await _leafSlots.WaitAsync(ct).ConfigureAwait(false);
            Task<LeafDoc> flight;
            try
            {
                var sourceRoot = SourceRoot(rootSnapshot);
                flight = StartShared(_leafFlights, LeafKey(leaf, sourceRoot), token => FetchLeafDocumentAsync(leaf, sourceRoot, token));
            }
            catch
            {
                _leafSlots.Release();
                throw;
            }

            _ = flight.ContinueWith(
                static (_, state) => ((SemaphoreSlim)state!).Release(),
                _leafSlots,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            waits.Add(flight.WaitAsync(ct));
        }

        var docs = await Task.WhenAll(waits).ConfigureAwait(false);
        var failed = docs.Count(doc => doc.Document is null) + inlineFailed;
        var total = docs.Length + inline.Count;
        return new LeafPreloadSummary(total, total - failed, failed);
    }

    /// <summary>
    /// 读取模组图片：只要本地有完整缓存就直接返回，绝不发网络请求，也不在后台悄悄查 ETag。
    /// 如果本地没有缓存，多个并发请求会合并为一次真实下载；下载失败后 10 分钟内不再重复请求，避免卡顿。
    /// Markdown 文本里的未登记图片不走此缓存，也不检查 XML 里的哈希值。
    /// </summary>
    /// <param name="snapshot">提供登记身份与来源基地址的快照。</param>
    /// <param name="imageId">已登记的图片 ID（大小写不敏感）。</param>
    /// <param name="ct">调用方取消只取消本次等待，不取消共享传输。</param>
    public async Task<ImageResult> GetImageAsync(RootSnapshotResult snapshot, string imageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryResolveImage(snapshot, imageId, out var uri, out var error))
            return ImageResult.Unavailable(error);
        if (TryCachedImage(uri) is { } cached)
            return cached;

        var key = uri.AbsoluteUri;
        Task<ImageResult> flight;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_imageFlights.TryGetValue(key, out flight!))
            {
                if (IsImageReadBlocked(key))
                    return ImageReadBlocked();
                flight = StartShared(_imageFlights, key, token => FetchImageAsync(uri, token));
            }
        }

        return await Observe(flight, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 后台定时器主动调用的图片刷新：向服务器发条件请求校验更新，如果正在下载同一张图则复用同一任务。
    /// 取消操作只停止当前等待，不会中断后台下载；图片首次就绪或内容发生改变时触发 <see cref="ImageUpdated"/> 事件。
    /// </summary>
    public Task<ImageResult> RefreshImageAsync(RootSnapshotResult snapshot, string imageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryResolveImage(snapshot, imageId, out var uri, out var error))
            return Task.FromResult(ImageResult.Unavailable(error));
        return Share(_imageFlights, uri.AbsoluteUri, token => RefreshImageCore(uri, token), ct);
    }

    /// <summary>
    /// 计算图片版本号，用于通知前端更新图片。根据规范化的图片 URL 和本地哈希值计算得出。
    /// 图片 URL 或文件内容发生变化时版本号会变；内容未变时保持不变，避免前端无意义刷新。
    /// </summary>
    public int ImageRevision(RootSnapshotResult snapshot, string imageId)
    {
        if (snapshot is null || !TryResolveImage(snapshot, imageId, out var uri, out _))
            return 0;
        return StableRevision(uri.AbsoluteUri, ServedImageDigest(uri));
    }

    /// <summary>
    /// 这张登记图片现在能不能不发网络就拿到正文。只读旁挂摘要和正文文件的存在性，不读正文、不发请求、不写状态。
    /// 宿主用它判断展示读取要不要让出正在进行的 XML 刷新阶段；探针说"有缓存"而正文随后读不出来（例如正文被外部删掉）
    /// 只是没让出这一轮，读取本身仍按 <see cref="GetImageAsync"/> 的规则走网络。没有磁盘缓存时一律 false，
    /// 与 <see cref="GetImageAsync"/> 在非磁盘会话下永远要联网的语义一致。
    /// </summary>
    public bool HasCachedImage(RootSnapshotResult snapshot, string imageId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryResolveImage(snapshot, imageId, out var uri, out _))
            return false;
        return _disk is not null && StampDigest(uri) is not null;
    }

    /// <summary>
    /// 读取 Markdown 正文这类纯文本资源：走同一条传输出口，不落盘、不缓存，也不参与 Fresh 判定。
    /// <paramref name="sourceOrUrl"/> 是绝对地址时直接用，否则按 <paramref name="metadataUrl"/> 解析相对 Source。
    /// 取不到正文返回 null，不抛业务异常。
    /// </summary>
    public async Task<string?> FetchTextAsync(string metadataUrl, string sourceOrUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(metadataUrl) || string.IsNullOrWhiteSpace(sourceOrUrl))
            return null;

        Uri uri;
        try
        {
            uri = Uri.TryCreate(sourceOrUrl, UriKind.Absolute, out var absolute)
                ? absolute
                : MetadataResourceUri.Resolve(metadataUrl, sourceOrUrl);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return null;
        }

        var transfer = await TransferAsync(uri, validators: null, ct).ConfigureAwait(false);
        try
        {
            var bytes = await transfer.ReadBodyAsync(ct).ConfigureAwait(false);
            if (!transfer.Ok || bytes is null)
                return null;
            var text = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            transfer.DiscardStagedFile();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 重复释放时令牌可能已经关掉。
        }

        // 两个信号量都故意不释放：还在跑的任务要归还名额，Dispose 抢在前面会让它们出错。
        // SemaphoreSlim 没建过 AvailableWaitHandle 就不需要释放；Dispose 只取消会话并释放自己建的下载实现。
        if (_ownsTransfer)
            _transfer.Dispose();
    }

    private Task<LeafDoc> EnsureLeafAsync(Uri leaf, string sourceRoot, bool force, CancellationToken ct)
    {
        var key = LeafKey(leaf, sourceRoot);
        if (!force)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_leafFlights.TryGetValue(key, out var fetch))
                    return Observe(fetch, ct);
                if (_leaves.TryGetValue(key, out var memory))
                    return Task.FromResult(memory);
            }

            return Share(_leafReads, key, token => ReadOrFetchLeafAsync(leaf, sourceRoot, token), ct);
        }

        return Share(_leafFlights, key, token => FetchLeafDocumentAsync(leaf, sourceRoot, token), ct);
    }

    private async Task<LeafDoc> ReadOrFetchLeafAsync(Uri leaf, string sourceRoot, CancellationToken ct)
    {
        var disk = _disk?.TryReadSnapshot(leaf, sourceRoot);
        if (disk is not null)
        {
            var loaded = StoreParsedLeaf(leaf, sourceRoot, disk.Bytes, disk.Digest, MetadataFreshness.Stale, error: null);
            if (loaded is not null)
                return loaded;
        }

        return await Share(_leafFlights, LeafKey(leaf, sourceRoot), token => FetchLeafDocumentAsync(leaf, sourceRoot, token), CancellationToken.None)
            .WaitAsync(ct)
            .ConfigureAwait(false);
    }

    private static LeafResult Project(LeafDoc doc, string manifestId)
    {
        if (doc.Document is null)
            return LeafUnavailable(doc.Source, doc.Error ?? "叶子清单不可用");

        var projection = doc.Projection(manifestId);
        if (!projection.Ok)
            return new LeafResult(null, null, null, doc.Source, MetadataFreshness.Unavailable, projection.Error);
        return new LeafResult(
            doc.Document,
            projection.Node,
            projection.Entry,
            doc.Source,
            doc.Status,
            doc.Status == MetadataFreshness.Fresh ? null : doc.Error);
    }

    private LeafDoc? TryMemoryLeaf(Uri leaf, string sourceRoot)
    {
        lock (_gate)
        {
            return _leaves.TryGetValue(LeafKey(leaf, sourceRoot), out var memory) && memory.Document is not null
                ? memory
                : null;
        }
    }

    private static LeafResult ProjectCached(LeafDoc doc, string manifestId)
    {
        var projected = Project(doc, manifestId);
        return projected.Status == MetadataFreshness.Fresh
            ? projected with { Status = MetadataFreshness.Stale }
            : projected;
    }

    private async Task<LeafResult> LoadCachedLeafAsync(Uri leaf, string sourceRoot, string manifestId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_disk is null)
        {
            if (!leaf.IsFile)
                return LeafUnavailable(leaf, "本地没有可用的叶子清单缓存");
            var live = await ReadLiveFileLeafAsync(leaf, sourceRoot, ct).ConfigureAwait(false);
            return live is null
                ? LeafUnavailable(leaf, "本地没有可用的叶子清单缓存")
                : ProjectCached(live, manifestId);
        }

        var disk = _disk.TryReadSnapshot(leaf, sourceRoot);
        if (disk is null)
            return LeafUnavailable(leaf, "本地没有可用的叶子清单缓存");
        var loaded = StoreParsedLeaf(leaf, sourceRoot, disk.Bytes, disk.Digest, MetadataFreshness.Stale, error: null);
        return loaded is null
            ? LeafUnavailable(leaf, "本地叶子清单缓存无法解析")
            : ProjectCached(loaded, manifestId);
    }

    private Task<LeafDoc?> ReadLiveFileLeafAsync(Uri leaf, string sourceRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var source = new OfflineSource(new CapturedSource(leaf, []));
            var read = MetadataReader.Read(leaf.AbsoluteUri, new MetadataSourceOptions { SourceRoot = sourceRoot, CustomSource = source });
            if (!TryAcceptLeaf(read.Root, out _))
                return Task.FromResult<LeafDoc?>(null);
            return Task.FromResult<LeafDoc?>(new LeafDoc
            {
                Source = leaf,
                Document = read.Root,
                Status = MetadataFreshness.Stale,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
            or InvalidOperationException or ArgumentException)
        {
            return Task.FromResult<LeafDoc?>(null);
        }
    }


    private Task<RootSnapshotResult> OpenCore(Uri origin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _disk?.BindPrimary(origin);
        if (_disk?.HasForeignOwner(origin) == true)
            return Task.FromResult(RootUnavailable(origin, "Cache URI ownership mismatch"));
        lock (_gate)
        {
            if (_rootDocs.TryGetValue(origin.AbsoluteUri, out var remembered))
                return Task.FromResult(RootOf(remembered, MetadataFreshness.Stale, error: null));
        }
        var memory = ReadCompleteRoot(origin);
        return Task.FromResult(memory is null
            ? RootUnavailable(origin, "No complete cached metadata")
            : RootOf(memory, MetadataFreshness.Stale, error: null));
    }

    private async Task<RootSnapshotResult> RefreshRootCore(Uri origin, CancellationToken ct)
    {
        _disk?.BindPrimary(origin);
        Dictionary<string, CapturedXml>? captured = null;
        try
        {
            var (read, documents) = await CaptureGraphAsync(origin, ct).ConfigureAwait(false);
            captured = documents;
            var catalogError = CatalogError(read.Root);
            if (catalogError is not null)
                return RootFallback(origin, catalogError);
            var digest = GraphDigest(read);
            await CommitGraphAsync(origin, read, documents, digest, ct).ConfigureAwait(false);
            return RootOf(RememberRoot(origin, read.Root, digest), MetadataFreshness.Fresh, error: null);

        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or XmlException or InvalidOperationException or ArgumentException)
        {
            return RootFallback(origin, ex.Message);
        }
        finally
        {
            DiscardGraph(captured);
        }
    }

    private async Task<(MetadataReadResult Read, Dictionary<string, CapturedXml> Documents)> CaptureGraphAsync(Uri origin, CancellationToken ct, string? sourceRoot = null)
    {
        var documents = new Dictionary<string, CapturedXml>(StringComparer.Ordinal);
        var source = new CapturedSource(origin, documents);
        try
        {
            sourceRoot ??= source.GetDirectoryName(origin.AbsoluteUri)!;
            await CaptureAsync(origin, sourceRoot, ct).ConfigureAwait(false);
            return (MetadataReader.Read(origin.AbsoluteUri, new MetadataSourceOptions { SourceRoot = sourceRoot, CustomSource = source }), documents);
        }
        catch
        {
            DiscardGraph(documents);
            throw;
        }

        async Task CaptureAsync(Uri uri, string boundary, CancellationToken token)
        {
            if (documents.ContainsKey(uri.AbsoluteUri))
                return;
            var disk = _disk?.TryReadRoot(uri);
            var transfer = await TransferAsync(uri, disk is { Verified: true } ? disk.Stamp : null, token).ConfigureAwait(false);
            var retained = false;
            try
            {
                XDocument? xml = null;
                if (transfer.NotModified)
                {
                    disk = _disk?.TryReadRoot(uri);
                    if (disk is not { Verified: true } || (xml = TryReadXml(disk.Bytes)) is null)
                        transfer = await TransferAsync(uri, validators: null, token).ConfigureAwait(false);
                }
                var body = transfer.NotModified ? disk?.Bytes : transfer.Bytes;
                if (!transfer.Ok || (body is null && transfer.Path is null))
                    throw new IOException(transfer.Error ?? "Missing XML response body");
                if (transfer.Path is { } staged)
                {
                    using var stream = File.OpenRead(staged);
                    xml = ReadXml(stream);
                }
                else
                    xml ??= ReadXml(body!);
                var stamp = transfer.NotModified ? disk!.Stamp! : new CacheStamp
                {
                    Uri = uri.AbsoluteUri,
                    Sha256 = transfer.Path is { } path ? MetadataDiskCache.Sha256FileHex(path) : MetadataDiskCache.Sha256Hex(body!),
                    ETag = transfer.ETag,
                    LastModified = transfer.LastModified,
                };
                documents.Add(uri.AbsoluteUri, new CapturedXml(uri, body, transfer.Path, xml, stamp));
                retained = true;
                foreach (var include in xml.Descendants().Where(element => element.Name.LocalName == "Include"))
                {
                    var target = MetadataReader.ResolveInclude(source, uri.AbsoluteUri,
                        include.Attribute("Source")?.Value ?? "", boundary);
                    var child = MetadataDiskCache.CanonicalizeEntry(target);
                    await CaptureAsync(child, boundary, token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!retained)
                    transfer.DiscardStagedFile();
            }
        }
    }

    private async Task CommitGraphAsync(Uri origin, MetadataReadResult read, Dictionary<string, CapturedXml> documents, string digest, CancellationToken ct)
    {
        if (_disk is null)
            return;
        _disk.ValidateGraphPaths(documents.Values.Select(document => document.Uri));
        foreach (var captured in documents.Values)
        {
            var path = _disk.RootBodyPath(captured.Uri);
            try
            {
                if (captured.Path is { } staged)
                    _disk.StageFile(path, captured.Uri.AbsoluteUri, staged);
                else
                    await _disk.StageAsync(path, captured.Uri.AbsoluteUri, captured.Bytes!, ct).ConfigureAwait(false);
                _disk.Commit(path, captured.Stamp);
            }
            catch
            {
                _disk.Discard(path);
                throw;
            }
        }
        _disk.CommitComposition(origin, read.SourceRoot, new XDocument(new XElement("Composition",
            new XAttribute("Origin", origin.AbsoluteUri), new XAttribute("SourceRoot", read.SourceRoot), new XAttribute("Sha256", digest),
            documents.Values.Select(item => new XElement("Document", new XAttribute("Uri", item.Uri.AbsoluteUri),
                new XAttribute("Sha256", item.Stamp.Sha256))),
            new XElement("Root", new XElement(read.Document.Root!)))));
    }

    private static string GraphDigest(MetadataReadResult read) =>
        MetadataDiskCache.Sha256Hex(Encoding.UTF8.GetBytes(read.Document.Root!.ToString(SaveOptions.DisableFormatting)));

    private static void DiscardGraph(Dictionary<string, CapturedXml>? documents)
    {
        if (documents is null)
            return;
        foreach (var captured in documents.Values)
            if (captured.Path is { } path)
                TryDeleteFile(path);
    }

    private static XDocument ReadXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return ReadXml(stream);
    }

    internal static XDocument ReadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static XDocument? TryReadXml(byte[] bytes)
    {
        try { return ReadXml(bytes); }
        catch (XmlException) { return null; }
    }

    private RootMemory? ReadCompleteRoot(Uri origin)
    {
        var snapshot = _disk?.TryReadSnapshot(origin, new Uri(origin, ".").AbsoluteUri);
        return snapshot is null ? null
            : FindRoot(origin, snapshot.Digest) ?? ParseRootBytes(origin, snapshot.Bytes, snapshot.Digest);
    }

    private async Task<ImageResult> FetchImageAsync(Uri uri, CancellationToken ct)
    {
        if (TryCachedImage(uri) is { } ready)
            return ready;

        try
        {
            var transfer = await TransferAsync(uri, validators: null, ct).ConfigureAwait(false);
            try
            {
                // 图片要把正文和摘要一起交给调用方，所以这里读一次落盘文件；没有第二个来源。
                var bytes = await transfer.ReadBodyAsync(ct).ConfigureAwait(false);
                if (!transfer.Ok || bytes is null)
                    return ColdImageFailure(uri, transfer.Error ?? "读取图片失败");
                if (!TryImageContentType(uri, transfer.ContentType, out var contentType))
                    return ColdImageFailure(uri, "响应不是图片类型");

                var digest = MetadataDiskCache.Sha256Hex(bytes);
                if (_disk is not null
                    && !await TryCommitImageAsync(uri, bytes, transfer.Path, digest, contentType, transfer.ETag, transfer.LastModified, ct).ConfigureAwait(false))
                    return ColdImageFailure(uri, "图片缓存无法写入");

                ClearImageReadBlock(uri.AbsoluteUri);
                RememberLiveDigest(uri, digest);
                if (_disk is not null)
                    RaiseImageUpdated(uri);
                return new ImageResult(bytes, contentType, MetadataFreshness.Fresh, null);
            }
            finally
            {
                transfer.DiscardStagedFile();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return ColdImageFailure(uri, ex.Message);
        }
    }

    private async Task<ImageResult> RefreshImageCore(Uri uri, CancellationToken ct)
    {
        if (_disk is null)
            return await FetchImageAsync(uri, ct).ConfigureAwait(false);

        var hasCache = TryReadUsableImage(uri, out var cached, out var cachedType);
        var validators = hasCache && cached.Verified ? cached.Stamp : null;
        try
        {
            var transfer = await TransferAsync(uri, validators, ct).ConfigureAwait(false);
            try
            {
                if (transfer.NotModified)
                {
                    if (hasCache)
                        return new ImageResult(cached.Bytes, cachedType, MetadataFreshness.Stale, null);
                    // 304 不带正文，直接换成无条件重取。
                    transfer = await TransferAsync(uri, validators: null, ct).ConfigureAwait(false);
                }

                var bytes = await transfer.ReadBodyAsync(ct).ConfigureAwait(false);
                if (!transfer.Ok || bytes is null)
                {
                    return hasCache
                        ? CachedImage(cached, cachedType, transfer.Error ?? "读取图片失败")
                        : ColdImageFailure(uri, transfer.Error ?? "读取图片失败");
                }

                if (!TryImageContentType(uri, transfer.ContentType, out var contentType))
                {
                    return hasCache
                        ? CachedImage(cached, cachedType, "响应不是图片类型")
                        : ColdImageFailure(uri, "响应不是图片类型");
                }

                var digest = MetadataDiskCache.Sha256Hex(bytes);
                var same = hasCache && string.Equals(cached.Digest, digest, StringComparison.OrdinalIgnoreCase);
                if (!await TryCommitImageAsync(uri, bytes, transfer.Path, digest, contentType, transfer.ETag, transfer.LastModified, ct).ConfigureAwait(false))
                {
                    if (hasCache && same)
                        return new ImageResult(cached.Bytes, cachedType, MetadataFreshness.Stale, null);
                    return hasCache
                        ? CachedImage(cached, cachedType, "图片缓存无法写入")
                        : ColdImageFailure(uri, "图片缓存无法写入");
                }

                ClearImageReadBlock(uri.AbsoluteUri);
                if (!same)
                    RaiseImageUpdated(uri);
                return new ImageResult(
                    bytes,
                    contentType,
                    same ? MetadataFreshness.Stale : MetadataFreshness.Fresh,
                    null);
            }
            finally
            {
                transfer.DiscardStagedFile();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return hasCache
                ? CachedImage(cached, cachedType, ex.Message)
                : ColdImageFailure(uri, ex.Message);
        }
    }

    /// <summary>
    /// 把这次下载的图片写进缓存。下载方写到本地文件的正文就是内容来源：能直接搬走就不再重写一遍刚读回内存的字节；
    /// 只有来自内存的情况（本地 file:// 图片）才写字节。正式替换仍由 <see cref="MetadataDiskCache.Commit"/> 完成。
    /// </summary>
    private async Task<bool> TryCommitImageAsync(
        Uri uri,
        byte[] bytes,
        string? stagedPath,
        string digest,
        string contentType,
        string? etag,
        string? lastModified,
        CancellationToken ct)
    {
        if (_disk is null)
            return true;

        var path = _disk.ImageBodyPath(uri);
        try
        {
            if (stagedPath is not null)
                _disk.StageFile(path, uri.AbsoluteUri, stagedPath);
            else
                await _disk.StageAsync(path, uri.AbsoluteUri, bytes, ct).ConfigureAwait(false);
            _disk.Commit(path, new CacheStamp
            {
                Uri = uri.AbsoluteUri,
                ETag = etag,
                LastModified = lastModified,
                Sha256 = digest,
                ContentType = contentType,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _disk.Discard(path);
            return false;
        }
    }

    private ImageResult? TryCachedImage(Uri uri)
    {
        if (!TryReadUsableImage(uri, out var cached, out var contentType))
            return null;
        return new ImageResult(cached.Bytes, contentType, MetadataFreshness.Stale, null);
    }

    private bool TryReadUsableImage(Uri uri, out DiskBody cached, out string contentType)
    {
        cached = null!;
        contentType = "";
        if (_disk is null)
            return false;

        var body = _disk.TryReadImage(uri);
        if (body is null)
            return false;
        var type = body.Stamp?.ContentType;
        if (string.IsNullOrWhiteSpace(type) && uri.IsFile)
            type = MediaTypeForExtension(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(type))
            return false;

        cached = body;
        contentType = type;
        return true;
    }

    private static ImageResult CachedImage(DiskBody cached, string contentType, string error) =>
        new(cached.Bytes, contentType, MetadataFreshness.Stale, error);

    private ImageResult ColdImageFailure(Uri uri, string error)
    {
        BlockImageRead(uri.AbsoluteUri);
        return ImageResult.Unavailable(error);
    }

    private static ImageResult ImageReadBlocked() =>
        ImageResult.Unavailable("图片暂时不可用，等待下一次显式刷新");

    private bool IsImageReadBlocked(string key)
    {
        if (_disk is null || !_imageReadBlockedUntil.TryGetValue(key, out var until))
            return false;
        if (_clock.GetUtcNow() < until)
            return true;
        _imageReadBlockedUntil.Remove(key);
        return false;
    }

    private void BlockImageRead(string key)
    {
        if (_disk is null)
            return;
        lock (_gate)
            _imageReadBlockedUntil[key] = _clock.GetUtcNow() + ImageReadCooldown;
    }

    private void ClearImageReadBlock(string key)
    {
        lock (_gate)
            _imageReadBlockedUntil.Remove(key);
    }

    private void RememberLiveDigest(Uri uri, string digest)
    {
        if (_disk is not null)
            return;

        string? previous;
        lock (_gate)
        {
            _liveImageDigests.TryGetValue(uri.AbsoluteUri, out previous);
            _liveImageDigests[uri.AbsoluteUri] = digest;
        }

        if (previous is not null && !string.Equals(previous, digest, StringComparison.OrdinalIgnoreCase))
            RaiseImageUpdated(uri);
    }

    private string? ServedImageDigest(Uri uri)
    {
        if (_disk is not null)
            return StampDigest(uri);
        if (uri.IsFile)
            return FileDigest(uri);
        lock (_gate)
            return _liveImageDigests.TryGetValue(uri.AbsoluteUri, out var digest) ? digest : null;
    }

    private string? StampDigest(Uri uri)
    {
        if (!_disk!.TryReadImageDigest(uri, out var digest, out var contentType))
            return null;
        if (string.IsNullOrWhiteSpace(contentType) && uri.IsFile)
            contentType = MediaTypeForExtension(uri.LocalPath);
        return string.IsNullOrWhiteSpace(contentType) ? null : digest;
    }

    private static string? FileDigest(Uri uri)
    {
        try
        {
            return File.Exists(uri.LocalPath)
                ? MetadataDiskCache.Sha256Hex(File.ReadAllBytes(uri.LocalPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int StableRevision(string canonicalUri, string? digest)
    {
        var payload = Encoding.UTF8.GetBytes(canonicalUri + "\n" + (digest ?? ""));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        var value = BinaryPrimitives.ReadInt32LittleEndian(hash);
        return value == 0 ? 1 : value;
    }

    private void RaiseImageUpdated(Uri uri)
    {
        var handler = ImageUpdated;
        if (handler is null)
            return;
        var args = new ImageUpdatedEventArgs(uri.AbsoluteUri);
        foreach (var item in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<ImageUpdatedEventArgs>)item).Invoke(this, args);
            }
            catch (Exception)
            {
                // 订阅方失败不能中断刷新结果。
            }
        }
    }

    private static bool TryResolveImage(RootSnapshotResult snapshot, string imageId, out Uri uri, out string error)
    {
        uri = null!;
        if (snapshot.Document is null || snapshot.OriginUri is null)
        {
            error = "根快照没有可用文档";
            return false;
        }

        if (string.IsNullOrWhiteSpace(imageId))
        {
            error = "图片标识不能为空";
            return false;
        }

        var registration = snapshot.Document.Images()
            .FirstOrDefault(item => string.Equals(item.Id, imageId, StringComparison.OrdinalIgnoreCase));
        if (registration is null)
        {
            error = $"元数据里没有登记图片 {imageId}";
            return false;
        }

        try
        {
            uri = MetadataDiskCache.CanonicalizeImage(ImageAddress(snapshot.OriginUri, registration));
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            error = $"图片地址不可用：{ex.Message}";
            return false;
        }
    }

    /// <summary>图片地址：优先外链 Url，其次按入口目录解析相对 Source。</summary>
    private static Uri ImageAddress(Uri origin, ImageEntry registration)
    {
        if (!string.IsNullOrWhiteSpace(registration.Url))
        {
            if (!Uri.TryCreate(registration.Url, UriKind.Absolute, out var absolute))
                throw new ArgumentException($"图片 Url 不是绝对地址：{registration.Url}");
            return absolute;
        }

        if (string.IsNullOrWhiteSpace(registration.Source))
            throw new ArgumentException($"图片 {registration.Id} 既没有 Url 也没有 Source");
        return MetadataResourceUri.Resolve(origin.AbsoluteUri, registration.Source);
    }

    /// <summary>
    /// 图片类型以响应头为准；本地文件没有响应头，才按扩展名判断。
    /// 认不出类型一律拒绝，避免把 HTML 或脚本当图片交给网页。
    /// </summary>
    private static bool TryImageContentType(Uri uri, string? header, out string contentType)
    {
        contentType = "";
        if (!string.IsNullOrWhiteSpace(header))
        {
            var media = header.Trim();
            if (!media.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return false;
            contentType = media.ToLowerInvariant();
            return true;
        }

        if (uri.IsFile && MediaTypeForExtension(uri.LocalPath) is { } inferred)
        {
            contentType = inferred;
            return true;
        }

        return false;
    }

    private static string? MediaTypeForExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        ".avif" => "image/avif",
        _ => null,
    };

    private async Task<LeafDoc> FetchLeafDocumentAsync(Uri leaf, string sourceRoot, CancellationToken ct)
    {
        Dictionary<string, CapturedXml>? captured = null;
        try
        {
            var (read, documents) = await CaptureGraphAsync(leaf, ct, sourceRoot).ConfigureAwait(false);
            captured = documents;
            if (!TryAcceptLeaf(read.Root, out var error))
                return LeafDocFallback(leaf, sourceRoot, error ?? "Invalid manifest document");
            var digest = GraphDigest(read);
            await CommitGraphAsync(leaf, read, documents, digest, ct).ConfigureAwait(false);
            return StoreLeafDocument(leaf, sourceRoot, read.Root, digest, MetadataFreshness.Fresh, error: null);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or XmlException or InvalidOperationException or ArgumentException)
        {
            return LeafDocFallback(leaf, sourceRoot, ex.Message);
        }
        finally
        {
            DiscardGraph(captured);
        }
    }

    private sealed record CapturedXml(Uri Uri, byte[]? Bytes, string? Path, XDocument Document, CacheStamp Stamp);

    private sealed class CapturedSource(Uri entry, Dictionary<string, CapturedXml> documents) : IMetadataXmlSource
    {
        public bool Exists(string path) => documents.ContainsKey(GetFullPath(path));
        public XDocument LoadDocument(string path) => documents[GetFullPath(path)].Document;
        public string Combine(string basePath, string relativePath) =>
            new Uri(DirectoryUri(basePath), relativePath.Replace('\\', '/')).AbsoluteUri;
        public string? GetDirectoryName(string path) => new Uri(new Uri(GetFullPath(path)), ".").AbsoluteUri;
        public string GetRelativePath(string fromPath, string toPath)
        {
            var from = DirectoryUri(fromPath);
            var to = new Uri(GetFullPath(toPath));
            if (from.IsFile && to.IsFile)
                return Path.GetRelativePath(from.LocalPath, to.LocalPath).Replace('\\', '/');
            if (from.Scheme != to.Scheme || from.Authority != to.Authority)
                return to.AbsoluteUri;
            return from.MakeRelativeUri(to).ToString();
        }
        public string GetFullPath(string path) => new Uri(entry, path.Replace('\\', '/')).AbsoluteUri;
        public bool IsPathRooted(string path) => Uri.TryCreate(path, UriKind.Absolute, out _) || Path.IsPathRooted(path);
        private Uri DirectoryUri(string path)
        {
            var full = GetFullPath(path);
            return new Uri(full.EndsWith('/') ? full : full + "/");
        }
    }

    private sealed class OfflineSource(IMetadataXmlSource paths) : IMetadataXmlSource
    {
        public bool Exists(string path)
        {
            var uri = MetadataDiskCache.CanonicalizeEntry(paths.GetFullPath(path));
            return uri.IsFile && File.Exists(uri.LocalPath);
        }
        public XDocument LoadDocument(string path)
        {
            var uri = MetadataDiskCache.CanonicalizeEntry(paths.GetFullPath(path));
            if (!uri.IsFile)
                throw new IOException("Offline source only reads local XML documents");
            using var stream = File.OpenRead(uri.LocalPath);
            return ReadXml(stream);
        }
        public string Combine(string basePath, string relativePath) => paths.Combine(basePath, relativePath);
        public string? GetDirectoryName(string path) => paths.GetDirectoryName(path);
        public string GetRelativePath(string fromPath, string toPath) => paths.GetRelativePath(fromPath, toPath);
        public string GetFullPath(string path) => paths.GetFullPath(path);
        public bool IsPathRooted(string path) => paths.IsPathRooted(path);
    }


    private async Task<Transfer> TransferAsync(Uri uri, CacheStamp? validators, CancellationToken ct)
    {
        if (uri.IsFile)
            return await ReadFileAsync(uri, ct).ConfigureAwait(false);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Transfer.Fail("不支持的地址协议");

        // 全局并发下载限制：元数据 XML（根与叶子）、登记图片与元数据 Markdown 都在这里排队，注入的下载实现也一样。
        // 先拿到名额再起超时计时——排队等名额是客户端自己的并发控制，不算进单次请求超时。
        // 排队也受会话生命周期约束：Dispose 要放走排队的人，不让他们挂在再也放不出来的名额上。
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _transferSlots.WaitAsync(queued.Token).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_requestTimeout);
            var staging = NewStagingPath();
            var handedOff = false;
            try
            {
                var result = await _transfer
                    .FetchAsync(new MetadataTransferRequest(uri, staging, validators?.ETag, validators?.LastModified), timeout.Token)
                    .ConfigureAwait(false);

                if (result.StatusCode == (int)HttpStatusCode.NotModified)
                    return Transfer.NotModifiedResult();

                if (result.StatusCode != (int)HttpStatusCode.OK)
                    return Transfer.Fail($"HTTP {result.StatusCode}");

                if (!File.Exists(staging))
                    return Transfer.Fail("传输没有落盘正文");

                handedOff = true;
                return Transfer.ModifiedFromFile(staging, result.ETag, result.LastModified, result.ContentType);
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("元数据客户端已释放", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Transfer.Fail("请求超时");
            }
            finally
            {
                // 没有交出去的临时正文一律现在删掉：取消、释放和异常都不能留下半截文件。
                // 交出去的那份由调用方负责（校验通过搬进缓存，否则在它的 finally 里删掉）。
                if (!handedOff)
                    TryDeleteFile(staging);
            }
        }
        finally
        {
            // 名额只由真正拿到它的这次调用归还，取消或释放都不会多还或漏还。
            _transferSlots.Release();
        }
    }

    /// <summary>本次传输独占的临时正文路径。目录按需创建：本地来源永远不会碰它。</summary>
    private string NewStagingPath()
    {
        Directory.CreateDirectory(_stagingDirectory);
        return Path.Combine(_stagingDirectory, Path.GetRandomFileName() + ".part");
    }

    /// <summary>
    /// 删掉没有被搬进缓存的临时正文。删不掉不该盖过真正的结果：下次传输用新名字，
    /// 也不会互相干扰。
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<Transfer> ReadFileAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            var bytes = await File.ReadAllBytesAsync(uri.LocalPath, timeout.Token).ConfigureAwait(false);
            return Transfer.Modified(bytes, etag: null, lastModified: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Transfer.Fail("请求超时");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return Transfer.Fail(ex.Message);
        }
    }

    private RootSnapshotResult RootFallback(Uri origin, string error)
    {
        if (_disk?.HasForeignOwner(origin) == true)
            return RootUnavailable(origin, error);
        var parsed = ReadCompleteRoot(origin);
        if (parsed is not null)
            return RootOf(parsed, MetadataFreshness.Stale, error);

        lock (_gate)
        {
            if (_rootDocs.TryGetValue(origin.AbsoluteUri, out var remembered) && remembered.Digest.Length > 0)
                return RootOf(remembered, MetadataFreshness.Stale, error);
        }

        return RootUnavailable(origin, error);
    }

    private LeafDoc LeafDocFallback(Uri leaf, string sourceRoot, string error)
    {
        var disk = _disk?.TryReadSnapshot(leaf, sourceRoot);
        if (disk is not null)
        {
            var loaded = StoreParsedLeaf(leaf, sourceRoot, disk.Bytes, disk.Digest, MetadataFreshness.Stale, error);
            if (loaded is not null)
                return loaded;
        }

        lock (_gate)
        {
            if (_leaves.TryGetValue(LeafKey(leaf, sourceRoot), out var memory))
            {
                memory.Status = MetadataFreshness.Stale;
                memory.Error = error;
                return memory;
            }
        }

        return new LeafDoc { Source = leaf, Status = MetadataFreshness.Unavailable, Error = error };
    }


    private RootMemory? ParseRootBytes(Uri origin, byte[] bytes, string digest)
    {
        try
        {
            var document = ParseBytes(bytes);
            if (!IsCatalog(document))
                return null;
            return RememberRoot(origin, document, digest);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private RootMemory RememberRoot(Uri origin, Metadata document, string digest)
    {
        lock (_gate)
        {
            if (_rootDocs.TryGetValue(origin.AbsoluteUri, out var existing)
                && digest.Length > 0
                && string.Equals(existing.Digest, digest, StringComparison.OrdinalIgnoreCase))
                return existing;

            var memory = new RootMemory
            {
                Document = document,
                Catalog = document.Catalog(),
                Origin = origin,
                Digest = digest,
            };
            _rootDocs[origin.AbsoluteUri] = memory;
            return memory;
        }
    }

    private RootMemory? FindRoot(Uri origin, string digest)
    {
        lock (_gate)
        {
            return _rootDocs.TryGetValue(origin.AbsoluteUri, out var item)
                && string.Equals(item.Digest, digest, StringComparison.OrdinalIgnoreCase)
                ? item
                : null;
        }
    }

    private LeafDoc? StoreParsedLeaf(Uri leaf, string sourceRoot, byte[] bytes, string digest, MetadataFreshness status, string? error)
    {
        var existing = FindLeaf(LeafKey(leaf, sourceRoot), digest);
        if (existing is not null)
        {
            lock (_gate)
            {
                existing.Status = status;
                existing.Error = error;
            }

            return existing;
        }

        try
        {
            var document = ParseBytes(bytes);
            if (!TryAcceptLeaf(document, out _))
                return null;
            return StoreLeafDocument(leaf, sourceRoot, document, digest, status, error);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private LeafDoc StoreLeafDocument(Uri leaf, string sourceRoot, Metadata document, string digest, MetadataFreshness status, string? error)
    {
        lock (_gate)
        {
            if (_leaves.TryGetValue(LeafKey(leaf, sourceRoot), out var existing)
                && string.Equals(existing.Digest, digest, StringComparison.OrdinalIgnoreCase))
            {
                existing.Status = status;
                existing.Error = error;
                return existing;
            }

            var memory = new LeafDoc
            {
                Source = leaf,
                Document = document,
                Digest = digest,
                Status = status,
                Error = error,
            };
            _leaves[LeafKey(leaf, sourceRoot)] = memory;
            return memory;
        }
    }

    private LeafDoc? FindLeaf(string key, string digest)
    {
        lock (_gate)
        {
            return _leaves.TryGetValue(key, out var memory)
                && string.Equals(memory.Digest, digest, StringComparison.OrdinalIgnoreCase)
                ? memory
                : null;
        }
    }

    private static string SourceRoot(RootSnapshotResult snapshot) => new Uri(snapshot.OriginUri!, ".").AbsoluteUri;

    private static string LeafKey(Uri leaf, string sourceRoot) => sourceRoot + "\n" + leaf.AbsoluteUri;

    private Task<T> Share<T>(Dictionary<string, Task<T>> flights, string key, Func<CancellationToken, Task<T>> work, CancellationToken callerCt) =>
        Observe(StartShared(flights, key, work), callerCt);

    private Task<T> StartShared<T>(Dictionary<string, Task<T>> flights, string key, Func<CancellationToken, Task<T>> work)
    {
        Task<T> flight;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (flights.TryGetValue(key, out flight!))
                return flight;

            var box = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            flight = box.Task;
            flights[key] = flight;
            _ = Finish(box, flight);
        }

        return flight;

        async Task Finish(TaskCompletionSource<T> box, Task<T> published)
        {
            await Task.Yield();
            try
            {
                var result = await work(_lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (flights.TryGetValue(key, out var current) && ReferenceEquals(current, published))
                        flights.Remove(key);
                    box.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (flights.TryGetValue(key, out var current) && ReferenceEquals(current, published))
                        flights.Remove(key);
                    box.TrySetException(ex);
                }
            }
        }
    }

    private static async Task<T> Observe<T>(Task<T> flight, CancellationToken callerCt) =>
        await flight.WaitAsync(callerCt).ConfigureAwait(false);

    private static List<Uri> EnumerateLeaves(RootSnapshotResult snapshot)
    {
        var leaves = new List<Uri>();
        if (snapshot.Document is null || snapshot.OriginUri is null)
            return leaves;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in EnumeratePackages(snapshot.Document))
        {
            if (!TryResolvePackage(snapshot.Document, snapshot.OriginUri, package, out var canonical, out _))
                continue;
            if (seen.Add(canonical))
                leaves.Add(new Uri(canonical));
        }

        return leaves;
    }

    private static IEnumerable<PackageEntry> EnumeratePackages(Metadata document)
    {
        foreach (var app in document.Applications())
        {
            foreach (var package in app.Packages)
                yield return package;
        }

        foreach (var mod in document.Mods())
        {
            foreach (var package in mod.Packages)
                yield return package;
        }
    }


    private static bool TryResolvePackage(Metadata document, Uri origin, PackageEntry package, out string canonical, out string manifestId)
    {
        canonical = "";
        manifestId = package.ManifestId ?? "";
        if (string.IsNullOrWhiteSpace(package.ManifestId))
            return false;

        var registration = document.ManifestRegistration(package.ManifestId);
        var source = registration is not null && IsStub(registration) ? registration.Get("Source") : null;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        try
        {
            canonical = MetadataDiskCache.CanonicalText(MetadataResourceUri.Resolve(origin.AbsoluteUri, source));
            manifestId = package.ManifestId;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return false;
        }
    }

    private static bool TryReadLeaf(Metadata document, string expectedManifestId, out Metadata? node, out ManifestEntry? entry, out string? error)
    {
        var manifests = document.GetAllElements("Manifest").ToList();
        node = manifests.FirstOrDefault(item => string.Equals(item.Get("ID"), expectedManifestId, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            entry = null;
            error = "叶子清单缺少与所选版本匹配的 Manifest 节点";
            return false;
        }

        if (IsStub(node))
        {
            entry = null;
            error = "叶子清单是登记存根，没有文件表";
            return false;
        }

        try
        {
            entry = node.ToManifestEntry();
        }
        catch (InvalidOperationException ex)
        {
            entry = null;
            error = ex.Message;
            return false;
        }

        // HashAlgorithm=None 的清单不做内容哈希校验，文件名之外不再要求 Hash
        var requiresHash = !string.Equals(entry.HashAlgorithm, "NONE", StringComparison.OrdinalIgnoreCase);
        foreach (var file in entry.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName)
                || (requiresHash && string.IsNullOrWhiteSpace(file.Hash)))
            {
                error = requiresHash
                    ? $"清单 {entry.Id} 的文件缺少 FileName 或 Hash"
                    : $"清单 {entry.Id} 的文件缺少 FileName";
                entry = null;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool IsStub(Metadata manifest) => !string.IsNullOrWhiteSpace(manifest.Get("Source"));

    private static bool TryAcceptLeaf(Metadata document, out string? error)
    {
        var manifests = document.GetAllElements("Manifest").ToList();
        if (manifests.Count == 0)
        {
            error = "叶子清单缺少 Manifest 节点";
            return false;
        }

        foreach (var node in manifests)
        {
            if (IsStub(node))
            {
                error = "叶子清单是登记存根，没有文件表";
                return false;
            }

            if (!TryReadLeaf(document, node.Get("ID") ?? "", out _, out _, out var leafError))
            {
                error = leafError;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static string? CatalogError(Metadata document)
    {
        var schema = document.Get("SchemaVersion");
        if (string.IsNullOrWhiteSpace(schema))
            schema = document.Find("SchemaVersion")?.Value;
        if (string.IsNullOrWhiteSpace(schema))
            return "根清单缺少 SchemaVersion";
        if (!MetadataSchema.IsCompatible(schema))
            return $"元数据契约版本不兼容：{schema}（客户端支持 {MetadataSchema.Current}）";
        return null;
    }

    private static bool IsCatalog(Metadata document) => CatalogError(document) is null;

    private static Metadata ParseBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return MetadataParser.Load(stream);
    }


    private static Uri RequireEntry(string metadataUrl)
    {
        if (string.IsNullOrWhiteSpace(metadataUrl))
            throw new ArgumentException("入口地址不能为空", nameof(metadataUrl));
        return MetadataDiskCache.CanonicalizeEntry(metadataUrl);
    }


    private static RootSnapshotResult RootOf(RootMemory memory, MetadataFreshness status, string? error) =>
        new(memory.Document, memory.Catalog, memory.Origin, status, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, error);

    private static RootSnapshotResult RootUnavailable(Uri origin, string error) =>
        new(null, null, origin, MetadataFreshness.Unavailable, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, error);

    private static LeafResult LeafUnavailable(Uri? source, string error) =>
        new(null, null, null, source, MetadataFreshness.Unavailable, error);


    private readonly record struct Transfer(
        bool Ok,
        bool NotModified,
        byte[]? Bytes,
        string? Path,
        string? ETag,
        string? LastModified,
        string? ContentType,
        string? Error)
    {
        public static Transfer Modified(byte[] bytes, string? etag, string? lastModified, string? contentType = null) =>
            new(true, false, bytes, null, etag, lastModified, contentType, null);

        /// <summary>正文已经在本地临时文件上：校验通过后搬进缓存，否则由调用方删掉。</summary>
        public static Transfer ModifiedFromFile(string path, string? etag, string? lastModified, string? contentType = null) =>
            new(true, false, null, path, etag, lastModified, contentType, null);

        public static Transfer NotModifiedResult() => new(true, true, null, null, null, null, null, null);

        public static Transfer Fail(string error) => new(false, false, null, null, null, null, null, error);

        /// <summary>读回正文。内存与落盘两条来源对调用方没有区别（图片与 Markdown 用）。</summary>
        public async Task<byte[]?> ReadBodyAsync(CancellationToken ct) =>
            Bytes ?? (Path is null ? null : await File.ReadAllBytesAsync(Path, ct).ConfigureAwait(false));

        /// <summary>删掉这份传输没能搬进缓存的临时正文（304、失败、关了磁盘缓存）。</summary>
        public void DiscardStagedFile()
        {
            if (Path is not null)
                TryDeleteFile(Path);
        }
    }

    private sealed class RootMemory
    {
        public required Metadata Document { get; init; }

        public required MetadataCatalog Catalog { get; init; }

        public required Uri Origin { get; init; }

        public required string Digest { get; init; }
    }

    private sealed class LeafDoc
    {
        private readonly Dictionary<string, LeafProjection> _projections = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _projectionGate = new();

        public required Uri Source { get; init; }

        public Metadata? Document { get; init; }

        public string Digest { get; init; } = "";

        public MetadataFreshness Status { get; set; }

        public string? Error { get; set; }

        public LeafProjection Projection(string manifestId)
        {
            lock (_projectionGate)
            {
                if (_projections.TryGetValue(manifestId, out var cached))
                    return cached;

                LeafProjection created;
                if (Document is null)
                    created = new LeafProjection(false, null, null, "叶子清单不可用");
                else if (!TryReadLeaf(Document, manifestId, out var node, out var entry, out var error))
                    created = new LeafProjection(false, null, null, error ?? "叶子清单不可用");
                else
                    created = new LeafProjection(true, node, entry, null);
                _projections[manifestId] = created;
                return created;
            }
        }
    }

    private readonly record struct LeafProjection(bool Ok, Metadata? Node, ManifestEntry? Entry, string? Error);
}
