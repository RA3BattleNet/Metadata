using System.Globalization;
using System.Net;
using System.Xml;
using Ra3.BattleNet.Metadata;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 根清单与包叶子的共享加载器。叶子身份只来自调用方传入的快照。
/// 相同 Source 的正文只拉一次，每个调用再按自己的 Manifest ID 投影。
/// </summary>
public sealed class MetadataClient : IDisposable
{
    private readonly MetadataDiskCache _disk;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _leafSlots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<RootSnapshotResult>> _openFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<RootSnapshotResult>> _refreshFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<LeafDoc>> _leafFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<LeafDoc>> _leafReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RootMemory> _rootDocs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LeafDoc> _leaves = new(StringComparer.Ordinal);
    private bool _disposed;

    public MetadataClient(string cacheDirectory, TimeSpan requestTimeout, int maxLeafConcurrency = 4, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("缓存目录不能为空", nameof(cacheDirectory));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero, nameof(requestTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLeafConcurrency);

        _requestTimeout = requestTimeout;
        _disk = new MetadataDiskCache(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = requestTimeout };
        _leafSlots = new SemaphoreSlim(maxLeafConcurrency, maxLeafConcurrency);
    }

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
        var disk = _disk.TryReadLeaf(leaf.AbsoluteUri);
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

    private Task<RootSnapshotResult> OpenCore(Uri origin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var disk = _disk.TryReadRoot(origin);
        if (disk is null)
            return Task.FromResult(RootUnavailable(origin, "本地没有可用的根清单缓存"));

        var memory = FindRoot(origin, disk.Digest) ?? ParseRootBytes(origin, disk.Bytes, disk.Digest);
        if (memory is null)
            return Task.FromResult(RootUnavailable(origin, "本地根清单缓存无法解析"));
        return Task.FromResult(RootOf(memory, MetadataFreshness.Stale, error: null));
    }

    private async Task<RootSnapshotResult> RefreshRootCore(Uri origin, CancellationToken ct)
    {
        try
        {
            var disk = _disk.TryReadRoot(origin);
            var transfer = await TransferAsync(origin, disk is { Verified: true } ? disk.Stamp : null, ct).ConfigureAwait(false);
            if (transfer.NotModified)
            {
                var again = _disk.TryReadRoot(origin);
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

    private async Task<LeafDoc> FetchLeafDocumentAsync(Uri leaf, CancellationToken ct)
    {
        var key = leaf.AbsoluteUri;
        try
        {
            var disk = _disk.TryReadLeaf(key);
            var transfer = await TransferAsync(leaf, disk is { Verified: true } ? disk.Stamp : null, ct).ConfigureAwait(false);
            if (transfer.NotModified)
            {
                var again = _disk.TryReadLeaf(key);
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
        var bodyPath = _disk.RootBodyPath(origin);
        try
        {
            await _disk.StageAsync(bodyPath, bytes, ct).ConfigureAwait(false);
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
        var bodyPath = _disk.LeafBodyPath(leaf.AbsoluteUri);
        try
        {
            await _disk.StageAsync(bodyPath, bytes, ct).ConfigureAwait(false);
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
            return Transfer.Modified(bytes, Header(response, "ETag"), Header(response, "Last-Modified"));
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
        var disk = _disk.TryReadRoot(origin);
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
        var disk = _disk.TryReadLeaf(leaf.AbsoluteUri);
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

    private readonly record struct Transfer(bool Ok, bool NotModified, byte[]? Bytes, string? ETag, string? LastModified, string? Error)
    {
        public static Transfer Modified(byte[] bytes, string? etag, string? lastModified) => new(true, false, bytes, etag, lastModified, null);

        public static Transfer NotModifiedResult() => new(true, true, null, null, null, null);

        public static Transfer Fail(string error) => new(false, false, null, null, null, error);
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
