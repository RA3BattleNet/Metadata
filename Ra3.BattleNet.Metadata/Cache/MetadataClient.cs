using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Ra3.BattleNet.Metadata;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 根清单、包叶子与登记图片的共享加载器。叶子与图片身份只来自调用方传入的快照。
/// 相同 Source 的正文只拉一次，每个调用再按自己的 Manifest ID 投影。
/// 已有缓存的展示读取不发 HTTP；<c>GetLeafAsync</c> 和冷图片读取仍会访问来源。
/// </summary>
public sealed class MetadataClient : IDisposable
{
    private static readonly TimeSpan ImageReadCooldown = TimeSpan.FromMinutes(10);

    private readonly MetadataDiskCache? _disk;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _leafSlots;
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
    /// <param name="requestTimeout">单次请求超时。</param>
    /// <param name="maxLeafConcurrency">叶子并发上限。</param>
    /// <param name="httpClient">外部传输实例；为 null 时自己建一个并负责释放。</param>
    /// <param name="enableDiskCache">
    /// false 表示这次会话完全不碰磁盘：不建缓存目录、不读已有 XML 与图片、不写临时文件或旁挂文件、
    /// 不发送由缓存产生的条件请求，也不把生产缓存当作失败回退。本地与测试来源用这个模式，
    /// 保证改完本地源立刻能看到新内容。
    /// </param>
    /// <param name="clock">冷图片失败抑制使用的时钟。省略时用系统 UTC，测试可注入以免等待墙钟。</param>
    public MetadataClient(
        string cacheDirectory,
        TimeSpan requestTimeout,
        int maxLeafConcurrency = 4,
        HttpClient? httpClient = null,
        bool enableDiskCache = true,
        TimeProvider? clock = null)
    {
        if (enableDiskCache && string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("缓存目录不能为空", nameof(cacheDirectory));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero, nameof(requestTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLeafConcurrency);

        _requestTimeout = requestTimeout;
        _clock = clock ?? TimeProvider.System;
        if (enableDiskCache)
        {
            _disk = new MetadataDiskCache(cacheDirectory);
            Directory.CreateDirectory(cacheDirectory);
        }

        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = requestTimeout };
        _leafSlots = new SemaphoreSlim(maxLeafConcurrency, maxLeafConcurrency);
    }

    /// <summary>
    /// 图片从不可用变为可展示，或已有正文被不同字节替换后触发。订阅方拿到的是地址，不是登记 ID。
    /// 同一次变化只触发一次。304、失败和字节未变都不触发。
    /// 通知发生在正文已经提交之后；回调里再读同一张图只命中缓存，不会另开传输。
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

    /// <summary>取共享叶子文档，再按这份快照里的版本登记投影 Manifest。</summary>
    public async Task<LeafResult> GetLeafAsync(RootSnapshotResult snapshot, string version, Uri leafSourceUri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TrySelect(snapshot, version, leafSourceUri, out var leaf, out var manifestId, out var error))
            return LeafUnavailable(leaf, error);
        return Project(await EnsureLeafAsync(leaf, force: false, ct).ConfigureAwait(false), manifestId);
    }

    /// <summary>强制重新读取该 Source。身份只看传入的快照，不改去追以后的根。</summary>
    public async Task<LeafResult> RefreshLeafAsync(RootSnapshotResult snapshot, string version, Uri leafSourceUri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TrySelect(snapshot, version, leafSourceUri, out var leaf, out var manifestId, out var error))
            return LeafUnavailable(leaf, error);
        return Project(await EnsureLeafAsync(leaf, force: true, ct).ConfigureAwait(false), manifestId);
    }

    /// <summary>
    /// 只读这份快照已有的内存或磁盘叶子，不发 HTTP，也不等待别人正在进行的网络请求。
    /// 缺失返回 Unavailable。关闭磁盘缓存时，file:// 读取当前源文件并投影为 Stale，不落盘。
    /// </summary>
    public Task<LeafResult> GetCachedLeafAsync(RootSnapshotResult snapshot, string version, Uri leafSourceUri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TrySelect(snapshot, version, leafSourceUri, out var leaf, out var manifestId, out var error))
            return Task.FromResult(LeafUnavailable(leaf, error));
        if (TryMemoryLeaf(leaf) is { } memory && (_disk is not null || !leaf.IsFile))
            return Task.FromResult(ProjectCached(memory, manifestId));
        return LoadCachedLeafAsync(leaf, manifestId, ct);
    }

    /// <summary>
    /// 刷新快照里全部 Application 与 Mod 的当前和历史包叶子。相同 Source 只拉一次。
    /// 不读取图片、Markdown 或 Updater 清单。单个叶子的网络失败不影响其余叶子。
    /// </summary>
    public async Task PreloadLeavesAsync(RootSnapshotResult rootSnapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootSnapshot);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var leaves = EnumerateLeaves(rootSnapshot);
        if (leaves.Count == 0)
            return;

        var waits = new List<Task>(leaves.Count);
        foreach (var leaf in leaves)
        {
            await _leafSlots.WaitAsync(ct).ConfigureAwait(false);
            Task<LeafDoc> flight;
            try
            {
                flight = StartShared(_leafFlights, leaf.AbsoluteUri, token => FetchLeafDocumentAsync(leaf, token));
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

        await Task.WhenAll(waits).ConfigureAwait(false);
    }

    /// <summary>
    /// 按已捕获快照读取一张已登记图片。启用磁盘缓存且正文完整时直接返回，不发 HTTP，也不做后台校验。
    /// 冷缓存只允许一次共享下载；失败后十分钟内普通读取不再请求。禁用磁盘缓存时每次都读当前来源。
    /// 不缓存 Markdown 正文里未登记的图片，也不读取 XML 的 Image Hash。
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
    /// 显式条件刷新一张已登记图片。与冷读取共享同一 URI 传输；不受冷失败抑制。
    /// 调用方取消只取消等待。首次可展示或正文被替换时触发 <see cref="ImageUpdated"/>；304 或字节未变不通知。
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
    /// 前端缓存破坏版本。已登记图片用规范化 URI 与已记录摘要做稳定混合。
    /// Source 或摘要变化会改变这个值。混合结果是 32 位，碰撞时仍可能复用地址，不能当成唯一证明。
    /// 不可解析时返回 0。摘要未变则版本不变。
    /// </summary>
    public int ImageRevision(RootSnapshotResult snapshot, string imageId)
    {
        if (snapshot is null || !TryResolveImage(snapshot, imageId, out var uri, out _))
            return 0;
        return StableRevision(uri.AbsoluteUri, ServedImageDigest(uri));
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
        if (!transfer.Ok || transfer.Bytes is null)
            return null;
        var text = Encoding.UTF8.GetString(transfer.Bytes);
        return string.IsNullOrWhiteSpace(text) ? null : text;
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

        if (_ownsHttp)
            _http.Dispose();
    }

    private Task<LeafDoc> EnsureLeafAsync(Uri leaf, bool force, CancellationToken ct)
    {
        var key = leaf.AbsoluteUri;
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

            return Share(_leafReads, key, token => ReadOrFetchLeafAsync(leaf, token), ct);
        }

        return Share(_leafFlights, key, token => FetchLeafDocumentAsync(leaf, token), ct);
    }

    private async Task<LeafDoc> ReadOrFetchLeafAsync(Uri leaf, CancellationToken ct)
    {
        var disk = _disk?.TryReadLeaf(leaf);
        if (disk is not null)
        {
            var loaded = StoreParsedLeaf(leaf, disk.Bytes, disk.Digest, MetadataFreshness.Stale, error: null);
            if (loaded is not null)
                return loaded;
        }

        return await Share(_leafFlights, leaf.AbsoluteUri, token => FetchLeafDocumentAsync(leaf, token), CancellationToken.None)
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

    private LeafDoc? TryMemoryLeaf(Uri leaf)
    {
        lock (_gate)
        {
            return _leaves.TryGetValue(leaf.AbsoluteUri, out var memory) && memory.Document is not null
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

    private async Task<LeafResult> LoadCachedLeafAsync(Uri leaf, string manifestId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_disk is null)
        {
            if (!leaf.IsFile)
                return LeafUnavailable(leaf, "本地没有可用的叶子清单缓存");
            var live = await ReadLiveFileLeafAsync(leaf, ct).ConfigureAwait(false);
            return live is null
                ? LeafUnavailable(leaf, "本地没有可用的叶子清单缓存")
                : ProjectCached(live, manifestId);
        }

        var disk = _disk.TryReadLeaf(leaf);
        if (disk is null)
            return LeafUnavailable(leaf, "本地没有可用的叶子清单缓存");
        var loaded = RememberCachedLeaf(leaf, disk.Bytes, disk.Digest);
        return loaded is null
            ? LeafUnavailable(leaf, "本地叶子清单缓存无法解析")
            : ProjectCached(loaded, manifestId);
    }

    private async Task<LeafDoc?> ReadLiveFileLeafAsync(Uri leaf, CancellationToken ct)
    {
        var transfer = await ReadFileAsync(leaf, ct).ConfigureAwait(false);
        if (!transfer.Ok || transfer.Bytes is null)
            return null;
        return ParseLeafDocument(leaf, transfer.Bytes, MetadataDiskCache.Sha256Hex(transfer.Bytes));
    }

    private LeafDoc? RememberCachedLeaf(Uri leaf, byte[] bytes, string digest)
    {
        if (TryMemoryLeaf(leaf) is { } existing)
            return existing;

        var parsed = ParseLeafDocument(leaf, bytes, digest);
        if (parsed is null)
            return null;

        lock (_gate)
        {
            if (_leaves.TryGetValue(leaf.AbsoluteUri, out var current) && current.Document is not null)
                return current;
            _leaves[leaf.AbsoluteUri] = parsed;
            return parsed;
        }
    }

    private static LeafDoc? ParseLeafDocument(Uri leaf, byte[] bytes, string digest)
    {
        try
        {
            var document = ParseBytes(bytes);
            if (!TryAcceptLeaf(document, out _))
                return null;
            return new LeafDoc
            {
                Source = leaf,
                Document = document,
                Digest = digest,
                Status = MetadataFreshness.Stale,
                Error = null,
            };
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private Task<RootSnapshotResult> OpenCore(Uri origin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _disk?.BindPrimary(origin);
        var disk = _disk?.TryReadRoot(origin);
        if (disk is null)
            return Task.FromResult(RootUnavailable(origin, "本地没有可用的根清单缓存"));

        var memory = FindRoot(origin, disk.Digest) ?? ParseRootBytes(origin, disk.Bytes, disk.Digest);
        if (memory is null)
            return Task.FromResult(RootUnavailable(origin, "本地根清单缓存无法解析"));
        return Task.FromResult(RootOf(memory, MetadataFreshness.Stale, error: null));
    }

    private async Task<RootSnapshotResult> RefreshRootCore(Uri origin, CancellationToken ct)
    {
        _disk?.BindPrimary(origin);
        try
        {
            var disk = _disk?.TryReadRoot(origin);
            var transfer = await TransferAsync(origin, disk is { Verified: true } ? disk.Stamp : null, ct).ConfigureAwait(false);
            if (transfer.NotModified)
            {
                var again = _disk?.TryReadRoot(origin);
                var memory = again is { Verified: true }
                    ? FindRoot(origin, again.Digest) ?? ParseRootBytes(origin, again.Bytes, again.Digest)
                    : null;
                if (memory is null)
                {
                    transfer = await TransferAsync(origin, validators: null, ct).ConfigureAwait(false);
                    if (transfer.NotModified)
                        return RootFallback(origin, "HTTP 304 但本地根清单无法解析");
                }
                else
                {
                    return RootOf(memory, MetadataFreshness.Fresh, error: null);
                }
            }

            if (!transfer.Ok || transfer.Bytes is null)
                return RootFallback(origin, transfer.Error ?? "刷新根清单失败");
            return await PublishRootAsync(origin, transfer.Bytes, transfer.ETag, transfer.LastModified, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return RootFallback(origin, ex.Message);
        }
    }

    private async Task<ImageResult> FetchImageAsync(Uri uri, CancellationToken ct)
    {
        if (TryCachedImage(uri) is { } ready)
            return ready;

        try
        {
            var transfer = await TransferAsync(uri, validators: null, ct).ConfigureAwait(false);
            if (!transfer.Ok || transfer.Bytes is null)
                return ColdImageFailure(uri, transfer.Error ?? "读取图片失败");
            if (!TryImageContentType(uri, transfer.ContentType, out var contentType))
                return ColdImageFailure(uri, "响应不是图片类型");

            var digest = MetadataDiskCache.Sha256Hex(transfer.Bytes);
            if (_disk is not null
                && !await TryCommitImageAsync(uri, transfer.Bytes, digest, contentType, transfer.ETag, transfer.LastModified, ct).ConfigureAwait(false))
                return ColdImageFailure(uri, "图片缓存无法写入");

            ClearImageReadBlock(uri.AbsoluteUri);
            RememberLiveDigest(uri, digest);
            if (_disk is not null)
                RaiseImageUpdated(uri);
            return new ImageResult(transfer.Bytes, contentType, MetadataFreshness.Fresh, null);
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
            if (transfer.NotModified)
            {
                if (hasCache)
                    return new ImageResult(cached.Bytes, cachedType, MetadataFreshness.Stale, null);
                transfer = await TransferAsync(uri, validators: null, ct).ConfigureAwait(false);
            }

            if (!transfer.Ok || transfer.Bytes is null)
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

            var digest = MetadataDiskCache.Sha256Hex(transfer.Bytes);
            var same = hasCache && string.Equals(cached.Digest, digest, StringComparison.OrdinalIgnoreCase);
            if (!await TryCommitImageAsync(uri, transfer.Bytes, digest, contentType, transfer.ETag, transfer.LastModified, ct).ConfigureAwait(false))
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
                transfer.Bytes,
                contentType,
                same ? MetadataFreshness.Stale : MetadataFreshness.Fresh,
                null);
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

    private async Task<bool> TryCommitImageAsync(
        Uri uri,
        byte[] bytes,
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

    private async Task<LeafDoc> FetchLeafDocumentAsync(Uri leaf, CancellationToken ct)
    {
        var key = leaf.AbsoluteUri;
        try
        {
            var disk = _disk?.TryReadLeaf(leaf);
            var transfer = await TransferAsync(leaf, disk is { Verified: true } ? disk.Stamp : null, ct).ConfigureAwait(false);
            if (transfer.NotModified)
            {
                var again = _disk?.TryReadLeaf(leaf);
                var reused = again is { Verified: true }
                    ? FindLeaf(key, again.Digest) ?? StoreParsedLeaf(leaf, again.Bytes, again.Digest, MetadataFreshness.Fresh, null)
                    : null;
                if (reused is null)
                {
                    transfer = await TransferAsync(leaf, validators: null, ct).ConfigureAwait(false);
                    if (transfer.NotModified)
                        return LeafDocFallback(leaf, "HTTP 304 但本地叶子清单无法解析");
                }
                else
                {
                    lock (_gate)
                    {
                        reused.Status = MetadataFreshness.Fresh;
                        reused.Error = null;
                    }

                    return reused;
                }
            }

            if (!transfer.Ok || transfer.Bytes is null)
                return LeafDocFallback(leaf, transfer.Error ?? "读取叶子清单失败");
            return await PublishLeafDocumentAsync(leaf, transfer.Bytes, transfer.ETag, transfer.LastModified, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return LeafDocFallback(leaf, ex.Message);
        }
    }

    private async Task<RootSnapshotResult> PublishRootAsync(Uri origin, byte[] bytes, string? etag, string? lastModified, CancellationToken ct)
    {
        Metadata document;
        try
        {
            document = ParseBytes(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RootFallback(origin, "根清单解析失败：" + ex.Message);
        }

        var catalogError = CatalogError(document);
        if (catalogError is not null)
            return RootFallback(origin, catalogError);

        var digest = MetadataDiskCache.Sha256Hex(bytes);
        if (_disk is not null)
        {
            var bodyPath = _disk.RootBodyPath(origin);
            try
            {
                await _disk.StageAsync(bodyPath, origin.AbsoluteUri, bytes, ct).ConfigureAwait(false);
                _disk.Commit(bodyPath, new CacheStamp
                {
                    Uri = origin.AbsoluteUri,
                    ETag = etag,
                    LastModified = lastModified,
                    Sha256 = digest,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disk.Discard(bodyPath);
                return RootFallback(origin, "根清单写入缓存失败：" + ex.Message);
            }
        }

        return RootOf(RememberRoot(origin, document, digest), MetadataFreshness.Fresh, error: null);
    }

    private async Task<LeafDoc> PublishLeafDocumentAsync(Uri leaf, byte[] bytes, string? etag, string? lastModified, CancellationToken ct)
    {
        Metadata document;
        try
        {
            document = ParseBytes(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LeafDocFallback(leaf, "叶子清单解析失败：" + ex.Message);
        }

        if (!TryAcceptLeaf(document, out var reject))
            return LeafDocFallback(leaf, reject ?? "叶子清单无效");

        var digest = MetadataDiskCache.Sha256Hex(bytes);
        if (_disk is not null)
        {
            var bodyPath = _disk.LeafBodyPath(leaf);
            try
            {
                await _disk.StageAsync(bodyPath, leaf.AbsoluteUri, bytes, ct).ConfigureAwait(false);
                _disk.Commit(bodyPath, new CacheStamp
                {
                    Uri = leaf.AbsoluteUri,
                    ETag = etag,
                    LastModified = lastModified,
                    Sha256 = digest,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disk.Discard(bodyPath);
                return LeafDocFallback(leaf, "叶子清单写入缓存失败：" + ex.Message);
            }
        }

        return StoreLeafDocument(leaf, document, digest, MetadataFreshness.Fresh, error: null);
    }

    private async Task<Transfer> TransferAsync(Uri uri, CacheStamp? validators, CancellationToken ct)
    {
        if (uri.IsFile)
            return await ReadFileAsync(uri, ct).ConfigureAwait(false);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Transfer.Fail("不支持的地址协议");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            AddValidators(request, validators);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return Transfer.NotModifiedResult();
            if (response.StatusCode != HttpStatusCode.OK)
                return Transfer.Fail($"HTTP {(int)response.StatusCode}");

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return Transfer.Modified(
                bytes,
                Header(response, "ETag"),
                Header(response, "Last-Modified"),
                response.Content.Headers.ContentType?.MediaType);
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("元数据客户端已释放", ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Transfer.Fail("请求超时");
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
        var disk = _disk?.TryReadRoot(origin);
        if (disk is not null)
        {
            var parsed = FindRoot(origin, disk.Digest) ?? ParseRootBytes(origin, disk.Bytes, disk.Digest);
            if (parsed is not null)
                return RootOf(parsed, MetadataFreshness.Stale, error);
        }

        lock (_gate)
        {
            if (_rootDocs.TryGetValue(origin.AbsoluteUri, out var remembered) && remembered.Digest.Length > 0)
                return RootOf(remembered, MetadataFreshness.Stale, error);
        }

        return RootUnavailable(origin, error);
    }

    private LeafDoc LeafDocFallback(Uri leaf, string error)
    {
        var disk = _disk?.TryReadLeaf(leaf);
        if (disk is not null)
        {
            var loaded = StoreParsedLeaf(leaf, disk.Bytes, disk.Digest, MetadataFreshness.Stale, error);
            if (loaded is not null)
                return loaded;
        }

        lock (_gate)
        {
            if (_leaves.TryGetValue(leaf.AbsoluteUri, out var memory))
            {
                memory.Status = MetadataFreshness.Stale;
                memory.Error = error;
                return memory;
            }
        }

        return new LeafDoc { Source = leaf, Status = MetadataFreshness.Unavailable, Error = error };
    }

    private static bool TrySelect(RootSnapshotResult snapshot, string version, Uri leafSourceUri, out Uri leaf, out string manifestId, out string error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("版本不能为空", nameof(version));

        leaf = RequireLeaf(leafSourceUri);
        manifestId = "";
        if (snapshot.Document is null || snapshot.OriginUri is null)
        {
            error = "根快照没有可用文档";
            return false;
        }

        var match = Match(snapshot.Document, snapshot.OriginUri, version, leaf.AbsoluteUri, out manifestId);
        error = match switch
        {
            MatchKind.One => "",
            MatchKind.Ambiguous => $"版本 {version} 在这份快照里登记了多份不同清单",
            _ => $"版本 {version} 在这份快照里没有登记清单地址 {leaf.AbsoluteUri}",
        };
        return match == MatchKind.One;
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

    private LeafDoc? StoreParsedLeaf(Uri leaf, byte[] bytes, string digest, MetadataFreshness status, string? error)
    {
        var existing = FindLeaf(leaf.AbsoluteUri, digest);
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
            return StoreLeafDocument(leaf, document, digest, status, error);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private LeafDoc StoreLeafDocument(Uri leaf, Metadata document, string digest, MetadataFreshness status, string? error)
    {
        lock (_gate)
        {
            if (_leaves.TryGetValue(leaf.AbsoluteUri, out var existing)
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
            _leaves[leaf.AbsoluteUri] = memory;
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

    private static MatchKind Match(Metadata document, Uri origin, string version, string leafText, out string manifestId)
    {
        string? found = null;
        foreach (var package in EnumeratePackages(document))
        {
            if (!string.Equals(package.Version, version, StringComparison.Ordinal))
                continue;
            if (!TryResolvePackage(document, origin, package, out var canonical, out var id))
                continue;
            if (!string.Equals(canonical, leafText, StringComparison.Ordinal))
                continue;
            if (found is not null && !string.Equals(found, id, StringComparison.OrdinalIgnoreCase))
            {
                manifestId = "";
                return MatchKind.Ambiguous;
            }

            found = id;
        }

        manifestId = found ?? "";
        return found is null ? MatchKind.None : MatchKind.One;
    }

    private static bool TryResolvePackage(Metadata document, Uri origin, PackageEntry package, out string canonical, out string manifestId)
    {
        canonical = "";
        manifestId = package.ManifestId ?? "";
        if (string.IsNullOrWhiteSpace(package.ManifestId))
            return false;

        var source = document.ManifestRegistration(package.ManifestId)?.Get("Source");
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
        node = document.GetAllElements("Manifest")
            .FirstOrDefault(item => string.Equals(item.Get("ID"), expectedManifestId, StringComparison.OrdinalIgnoreCase));
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

        foreach (var file in entry.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName) || string.IsNullOrWhiteSpace(file.Hash))
            {
                error = $"清单 {entry.Id} 的文件缺少 FileName 或 Hash";
                entry = null;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool IsStub(Metadata manifest)
    {
        var hasSource = !string.IsNullOrWhiteSpace(manifest.Get("Source"));
        var hasBody = manifest.Children.Any(child => child.Name is "File" or "Skudef" or "Dependencies" or "SubManifest");
        return hasSource && !hasBody;
    }

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

    private static void AddValidators(HttpRequestMessage request, CacheStamp? validators)
    {
        if (validators is null)
            return;
        if (!string.IsNullOrWhiteSpace(validators.ETag))
            request.Headers.TryAddWithoutValidation("If-None-Match", validators.ETag);
        if (!string.IsNullOrWhiteSpace(validators.LastModified)
            && DateTimeOffset.TryParse(
                validators.LastModified,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var modified))
        {
            request.Headers.IfModifiedSince = modified;
        }
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();
        if (response.Content.Headers.TryGetValues(name, out values))
            return values.FirstOrDefault();
        return null;
    }

    private static Uri RequireEntry(string metadataUrl)
    {
        if (string.IsNullOrWhiteSpace(metadataUrl))
            throw new ArgumentException("入口地址不能为空", nameof(metadataUrl));
        return MetadataDiskCache.CanonicalizeEntry(metadataUrl);
    }

    private static Uri RequireLeaf(Uri leafSourceUri)
    {
        ArgumentNullException.ThrowIfNull(leafSourceUri);
        if (!leafSourceUri.IsAbsoluteUri)
            throw new ArgumentException("叶子地址必须是绝对地址", nameof(leafSourceUri));
        return MetadataDiskCache.Canonicalize(leafSourceUri);
    }

    private static RootSnapshotResult RootOf(RootMemory memory, MetadataFreshness status, string? error) =>
        new(memory.Document, memory.Catalog, memory.Origin, status, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, error);

    private static RootSnapshotResult RootUnavailable(Uri origin, string error) =>
        new(null, null, origin, MetadataFreshness.Unavailable, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, error);

    private static LeafResult LeafUnavailable(Uri source, string error) =>
        new(null, null, null, source, MetadataFreshness.Unavailable, error);

    private enum MatchKind
    {
        None,
        One,
        Ambiguous,
    }

    private readonly record struct Transfer(bool Ok, bool NotModified, byte[]? Bytes, string? ETag, string? LastModified, string? ContentType, string? Error)
    {
        public static Transfer Modified(byte[] bytes, string? etag, string? lastModified, string? contentType = null) =>
            new(true, false, bytes, etag, lastModified, contentType, null);

        public static Transfer NotModifiedResult() => new(true, true, null, null, null, null, null);

        public static Transfer Fail(string error) => new(false, false, null, null, null, null, error);
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
