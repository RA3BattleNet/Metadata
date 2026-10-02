using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// Metadata 解析库的运行期本地缓存：打开上次验证过的快照、按条件请求刷新根目录、把根原文按
/// 内容寻址落到宿主给的缓存目录里。库不硬编码宿主路径，也不下载 Mod/Content/安装器二进制
/// （那是下载系统的活）。
///
/// 典型用法：启动时先 <see cref="OpenAsync"/> 读本地（不联网，阻塞本地安装列表加载），
/// 再单独调用一次 <see cref="RefreshAsync"/>；刷新失败只让目录变成 Stale，
/// 已安装版本仍按本地资料工作。
/// </summary>
public sealed class MetadataCache : IDisposable
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<CatalogRefreshResult>>> SharedRefreshes =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Lazy<Task<ResourceResolveResult>>> SharedResources =
        new(StringComparer.Ordinal);

    private readonly object _leaseGate = new();
    private readonly HashSet<SnapshotLease> _leases = [];

    private readonly CacheOptions _options;
    private readonly CacheLayout _layout;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public MetadataCache(CacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _layout = new CacheLayout(options.CacheRoot);
        _ownsHttpClient = options.HttpClient is null;
        _httpClient = options.HttpClient ?? new HttpClient();
    }

    /// <summary>缓存根（绝对路径）。</summary>
    public string CacheRoot => _layout.Root;

    /// <summary>入口地址。</summary>
    public Uri EntryUri => _options.EntryUri;

    /// <summary>
    /// 只读本地：必要时恢复缓存指针并校验快照，**不做任何网络请求**。
    ///
    /// 返回的 <see cref="CatalogFreshness.Stale"/> 是"还不知道远端是否变了"，不是"远端已变"；
    /// 刷新成功后由 <see cref="RefreshAsync"/> 给出 Unchanged/Updated。
    /// </summary>
    public async Task<CatalogLoadResult> OpenAsync(CancellationToken token = default)
    {
        var bound = CacheOrigin.EnsureBound(_layout, _options.EntryUri);
        if (bound is not null)
            return Unavailable(bound);

        var pointer = CachePointerFile.ReadHighest(_layout);
        if (pointer is null)
            return Unavailable(new CacheError(CacheErrorCodes.NoCache, "本地没有可用的快照"));

        var snapshot = TryLoadSnapshot(pointer, out var loadError);
        if (snapshot is null) return Unavailable(loadError!);

        _options.Log?.Invoke($"缓存打开：snapshot={pointer.SnapshotId} generation={pointer.Generation}");
        return new CatalogLoadResult { Status = CatalogFreshness.Stale, Snapshot = snapshot };
    }

    /// <summary>
    /// 发起一次有界条件刷新。同一进程、同一缓存根的并发刷新共享一次网络请求
    /// （App/Mod/Updater 三个读者不会各拉一次根）；调用方自己的取消只取消**本次等待**，
    /// 不打断其他读者共享的那次刷新。
    /// </summary>
    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken token = default)
    {
        var key = _layout.Root;
        var lazy = SharedRefreshes.GetOrAdd(key,
            _ => new Lazy<Task<CatalogRefreshResult>>(
                () => Task.Run(() => RefreshCoreAsync(CancellationToken.None)),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                SharedRefreshes.TryRemove(new KeyValuePair<string, Lazy<Task<CatalogRefreshResult>>>(key, lazy));
        }
    }

    /// <summary>本类自建的 <see cref="HttpClient"/> 由本类释放；宿主注入的那个不碰。</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    // ------------------------------------------------------------------ 资源解析

    /// <summary>
    /// 为某个快照取一个读取租约。持有期间解析出来的对象不进回收候选。
    /// 租约只能用于它所属的那个缓存实例；换实例、换缓存根都会在解析时被拒绝。
    /// </summary>
    public SnapshotLease AcquireLease(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lease = new SnapshotLease(this, snapshot);
        lock (_leaseGate) _leases.Add(lease);
        return lease;
    }

    /// <summary>当前活动租约（缓存清理按它们避让）。</summary>
    public IReadOnlyCollection<SnapshotLease> ActiveLeases
    {
        get { lock (_leaseGate) return _leases.ToArray(); }
    }

    internal void ReleaseLease(SnapshotLease lease)
    {
        lock (_leaseGate) _leases.Remove(lease);
    }

    /// <summary>
    /// 解析一个登记资源，按需把对象落到 <c>objects/&lt;sha256&gt;/payload</c>。
    ///
    /// 两类资源的规则不同，这不是优化而是语义：
    /// <list type="bullet">
    /// <item><b>叶子清单</b>：每次解析都向服务端核对（有条件验证器就带条件请求）。绝不拿本地旧字节冒充"当前发布"。</item>
    /// <item><b>展示媒体</b>：本地已有对象就直接用，不为刷新一张图去联网；取不到只是占位。</item>
    /// </list>
    /// </summary>
    public async Task<ResourceResolveResult> ResolveResourceAsync(
        SnapshotLease lease, ResourceRef resource, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(resource);

        if (lease.IsDisposed) return Fail(CacheErrorCodes.ForeignSnapshot, "租约已释放");
        if (!ReferenceEquals(lease.Owner, this)) return Fail(CacheErrorCodes.ForeignSnapshot, "租约属于另一个缓存实例");
        if (!string.Equals(lease.CacheRoot, _layout.Root, StringComparison.OrdinalIgnoreCase))
            return Fail(CacheErrorCodes.ForeignSnapshot, "快照不属于这个缓存根");
        if (!string.Equals(CacheOrigin.IdentityOf(lease.Snapshot.OriginEntryUri), CacheOrigin.IdentityOf(_options.EntryUri),
                StringComparison.Ordinal))
            return Fail(CacheErrorCodes.ForeignSnapshot, "快照的来源与本缓存根的入口不一致");

        if (!CacheResourceResolver.TryResolve(lease.Snapshot.OriginEntryUri, resource.Source, out var sourceUri, out var resolveError))
            return new ResourceResolveResult { Error = resolveError };

        var algorithm = string.IsNullOrWhiteSpace(resource.ExpectedHashAlgorithm)
            ? null
            : resource.ExpectedHashAlgorithm.Trim().ToUpperInvariant();

        if (algorithm is not null)
        {
            var hexLength = CacheHash.HexLengthOf(algorithm);
            if (hexLength == 0)
                return Fail(CacheErrorCodes.UnsupportedHashAlgorithm, $"登记节点声明了本库不认识的摘要算法：{resource.ExpectedHashAlgorithm}");
            if (!CacheHash.IsHex(resource.ExpectedHash, hexLength))
                return Fail(CacheErrorCodes.InvalidSource, "登记节点声明的摘要长度或编码不合法");
        }
        else if (!string.IsNullOrWhiteSpace(resource.ExpectedHash))
        {
            return Fail(CacheErrorCodes.InvalidSource, "登记节点声明了摘要却没有给算法，无法判断该怎么校验");
        }

        var key = $"{_layout.Root}\n{lease.Snapshot.SnapshotId}\n{resource.RegistryId}";
        var lazy = SharedResources.GetOrAdd(key,
            _ => new Lazy<Task<ResourceResolveResult>>(
                () => Task.Run(() => ResolveCoreAsync(lease.Snapshot, resource, sourceUri!, algorithm, CancellationToken.None)),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var result = await lazy.Value.WaitAsync(token).ConfigureAwait(false);
            if (result.Resource is { } cached) lease.Track(cached.Sha256);
            return result;
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                SharedResources.TryRemove(new KeyValuePair<string, Lazy<Task<ResourceResolveResult>>>(key, lazy));
        }
    }

    private async Task<ResourceResolveResult> ResolveCoreAsync(
        CatalogSnapshot snapshot, ResourceRef resource, Uri sourceUri, string? algorithm, CancellationToken token)
    {
        var store = new ResourceMapStore(_layout, snapshot.SnapshotId);
        var (map, mapError) = store.Load();
        if (mapError is not null) return new ResourceResolveResult { Error = mapError };
        map ??= ResourceMap.Empty(snapshot.SnapshotId);

        var known = map.Find(resource.RegistryId);
        var knownUsable = false;
        string? existingPath = null;
        if (known is not null
            && string.Equals(known.ResolvedUrl, sourceUri.AbsoluteUri, StringComparison.Ordinal)
            && known.ObjectDigest is { } knownDigest
            && CacheObjectStore.TryGet(_layout, knownDigest, known.ObjectSize, out var foundPath))
        {
            knownUsable = true;
            existingPath = foundPath;
        }

        // 媒体：本地有就用，不为一张图联网
        if (knownUsable && resource.Kind == ResourceKind.Media)
            return new ResourceResolveResult
            {
                Resource = Describe(resource, sourceUri, existingPath!, known!, fromCache: true),
            };

        var needMd5 = string.Equals(algorithm, "MD5", StringComparison.Ordinal);
        var maxBytes = resource.Kind == ResourceKind.Leaf ? _options.LeafMaxBytes : _options.MediaMaxBytes;
        var conditional = knownUsable && resource.Kind == ResourceKind.Leaf ? known : null;

        ResourceAttempt attempt;
        var tries = 0;
        while (true)
        {
            try
            {
                attempt = await FetchResourceAsync(sourceUri, maxBytes, conditional, needMd5, token).ConfigureAwait(false);
                break;
            }
            catch (CacheParseException ex)
            {
                return RecordFailure(store, map, resource, sourceUri, known, ex.Code, ex.Message);
            }
            catch (Exception ex) when (IsTransient(ex) && tries < _options.RefreshRetryCount)
            {
                tries++;
                await DelayBeforeRetryAsync(tries, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return RecordFailure(store, map, resource, sourceUri, known,
                    CacheErrorCodes.NetworkUnavailable, OneLine(ex.Message));
            }
        }

        if (attempt.Status == HttpStatusCode.NotModified)
        {
            if (!knownUsable)
                return RecordFailure(store, map, resource, sourceUri, known,
                    CacheErrorCodes.HttpStatus, "服务端对没有验证器的资源请求返回了 304");

            if (algorithm is not null && !VerifyFile(existingPath!, algorithm, resource.ExpectedHash!, out var verifyError))
                return RecordFailure(store, map, resource, sourceUri, known, CacheErrorCodes.DigestMismatch, verifyError);

            var refreshed = BuildEntry(known, resource, sourceUri, null, 0, BindingOf(algorithm),
                ResourceEntry.Ready, null, null, _options.TimeProvider.GetUtcNow(), null);
            map.Put(refreshed);
            store.Save(map);
            return new ResourceResolveResult
            {
                Resource = Describe(resource, sourceUri, existingPath!, refreshed, fromCache: true),
            };
        }

        if (!attempt.IsSuccess)
        {
            var code = attempt.Status == HttpStatusCode.NotFound
                ? CacheErrorCodes.ResourceUnavailable
                : CacheErrorCodes.HttpStatus;
            return RecordFailure(store, map, resource, sourceUri, known, code,
                $"资源请求返回 {(int)attempt.Status} {attempt.Status}");
        }

        if (resource.ExpectedSize is { } declaredSize && attempt.Size != declaredSize)
        {
            DiscardStaged(attempt.StagedPath);
            return RecordFailure(store, map, resource, sourceUri, known, CacheErrorCodes.DigestMismatch,
                $"字节数 {attempt.Size} 与登记节点声明的 {declaredSize} 不符");
        }

        if (algorithm is not null)
        {
            var actual = algorithm == "SHA256" ? attempt.Sha256 : attempt.Md5;
            if (!string.Equals(actual, resource.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                DiscardStaged(attempt.StagedPath);
                return RecordFailure(store, map, resource, sourceUri, known, CacheErrorCodes.DigestMismatch,
                    $"资源摘要与登记节点声明的 {algorithm} 不符");
            }
        }

        var objectPath = CacheObjectStore.Commit(_layout, attempt.Sha256, attempt.StagedPath!);
        var entry = BuildEntry(known, resource, sourceUri, attempt.Sha256, attempt.Size, BindingOf(algorithm),
            ResourceEntry.Ready, attempt.StrongETag, attempt.LastModified, _options.TimeProvider.GetUtcNow(), null);
        map.Put(entry);
        store.Save(map);

        _options.Log?.Invoke($"缓存资源就绪：{resource.RegistryId} → {attempt.Sha256}");
        return new ResourceResolveResult
        {
            Resource = Describe(resource, sourceUri, objectPath, entry, fromCache: false),
        };
    }

    private ResourceResolveResult RecordFailure(
        ResourceMapStore store, ResourceMap map, ResourceRef resource, Uri sourceUri,
        ResourceEntry? known, string code, string message)
    {
        var oneLine = OneLine(message);
        var entry = BuildEntry(known, resource, sourceUri, null, 0, BindingOf(resource.ExpectedHashAlgorithm),
            ResourceEntry.Failed, null, null, _options.TimeProvider.GetUtcNow(), oneLine);

        try
        {
            map.Put(entry);
            store.Save(map);
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"记录资源失败状态时出错（不影响本次结论）：{OneLine(ex.Message)}");
        }

        return new ResourceResolveResult { Error = new CacheError(code, oneLine) };
    }

    /// <summary>把这次解析结果并进上一条记录：地址变了就不再沿用旧对象身份。</summary>
    private static ResourceEntry BuildEntry(
        ResourceEntry? previous, ResourceRef resource, Uri sourceUri,
        string? objectDigest, long objectSize, ResourceBinding binding,
        string status, string? etag, string? lastModified, DateTimeOffset? fetchedAt, string? lastError)
    {
        var sameSource = previous is not null
                         && string.Equals(previous.ResolvedUrl, sourceUri.AbsoluteUri, StringComparison.Ordinal);

        return new ResourceEntry
        {
            RegistryId = resource.RegistryId,
            Source = resource.Source,
            ResolvedUrl = sourceUri.AbsoluteUri,
            ExpectedHash = resource.ExpectedHash,
            ExpectedHashAlgorithm = resource.ExpectedHashAlgorithm,
            ExpectedSize = resource.ExpectedSize,
            ObjectDigest = objectDigest ?? (sameSource ? previous!.ObjectDigest : null),
            ObjectSize = objectDigest is not null ? objectSize : sameSource ? previous!.ObjectSize : 0,
            Status = status,
            Binding = binding,
            ETag = etag ?? (sameSource ? previous!.ETag : null),
            LastModified = lastModified ?? (sameSource ? previous!.LastModified : null),
            FetchedAtUtc = fetchedAt,
            LastError = lastError,
        };
    }

    private static CachedResource Describe(
        ResourceRef resource, Uri sourceUri, string localPath, ResourceEntry entry, bool fromCache) => new()
    {
        RegistryId = resource.RegistryId,
        SourceUri = sourceUri,
        LocalPath = localPath,
        Sha256 = entry.ObjectDigest!,
        Size = entry.ObjectSize,
        Binding = entry.Binding,
        FromCache = fromCache,
    };

    private static ResourceBinding BindingOf(string? algorithm) => algorithm?.ToUpperInvariant() switch
    {
        "SHA256" => ResourceBinding.Strong,
        null or "" => ResourceBinding.None,
        _ => ResourceBinding.Legacy,
    };

    private static ResourceResolveResult Fail(string code, string message) =>
        new() { Error = new CacheError(code, message) };

    // ------------------------------------------------------------------ 刷新

    private async Task<CatalogRefreshResult> RefreshCoreAsync(CancellationToken token)
    {
        var bound = CacheOrigin.EnsureBound(_layout, _options.EntryUri);
        if (bound is not null) return new CatalogRefreshResult { Outcome = CatalogRefreshOutcome.Unavailable, Error = bound };

        var pointer = CachePointerFile.ReadHighest(_layout);
        var current = pointer is null ? null : TryLoadSnapshot(pointer, out _);

        var unconditional = current is null;
        var attempt = 0;

        while (true)
        {
            RootAttempt attemptResult;
            try
            {
                attemptResult = await FetchRootAsync(unconditional ? null : pointer, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (CacheParseException ex)
            {
                return Degrade(current, ex.Code, ex.Message);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _options.RefreshRetryCount)
            {
                attempt++;
                await DelayBeforeRetryAsync(attempt, token).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                return Degrade(current, CacheErrorCodes.NetworkUnavailable, OneLine(ex.Message));
            }

            if (attemptResult.Status == HttpStatusCode.NotModified)
            {
                // 只有"带着已验证对象"的条件请求拿到 304 才算一致
                if (!unconditional) return new CatalogRefreshResult { Outcome = CatalogRefreshOutcome.Unchanged, Snapshot = current };

                // 本地对象已经不可用却收到 304：304 不能当成功，换成无条件请求再问一次
                if (attempt < _options.RefreshRetryCount)
                {
                    attempt++;
                    continue;
                }
                return Degrade(current, CacheErrorCodes.HttpStatus, "本地快照不可用，服务端仍对无条件请求返回 304");
            }

            if (!attemptResult.IsSuccess)
            {
                if (IsTransientStatus(attemptResult.Status) && attempt < _options.RefreshRetryCount)
                {
                    attempt++;
                    await DelayBeforeRetryAsync(attempt, token).ConfigureAwait(false);
                    continue;
                }
                return Degrade(current, CacheErrorCodes.HttpStatus,
                    $"根请求返回 {(int)attemptResult.Status} {attemptResult.Status}");
            }

            return Commit(attemptResult, current);
        }
    }

    private CatalogRefreshResult Commit(RootAttempt response, CatalogSnapshot? current)
    {
        Metadata parsed;
        try
        {
            parsed = CacheSnapshotStore.ParseRoot(response.Body!, _options.RootMaxBytes);
        }
        catch (CacheParseException ex)
        {
            return Degrade(current, ex.Code, ex.Message);
        }

        var schemaVersion = parsed.Get("SchemaVersion");
        if (!MetadataSchema.IsCompatible(schemaVersion))
        {
            return Degrade(current, CacheErrorCodes.SchemaMismatch,
                $"发布物 SchemaVersion={schemaVersion ?? "(缺失)"} 与本库 {MetadataSchema.Current} 不兼容");
        }

        try
        {
            var snapshotId = CacheSnapshotStore.SaveRoot(_layout, response.Body!);
            var next = new CachePointer
            {
                Generation = CachePointerFile.NextGeneration(_layout),
                SnapshotId = snapshotId,
                RetrievedAtUtc = _options.TimeProvider.GetUtcNow(),
                ETag = response.StrongETag,
                LastModified = response.LastModified,
                ContentRevision = parsed.Get("ContentRevision"),
                OriginIdentity = CacheOrigin.IdentityOf(_options.EntryUri),
            };
            CachePointerFile.Publish(_layout, next);

            var snapshot = TryLoadSnapshot(next, out var loadError);
            if (snapshot is null)
                return Degrade(current, loadError!.Code, loadError.Message);

            _options.Log?.Invoke($"缓存刷新：snapshot={snapshotId} generation={next.Generation}");
            return new CatalogRefreshResult { Outcome = CatalogRefreshOutcome.Updated, Snapshot = snapshot };
        }
        catch (Exception ex)
        {
            return Degrade(current, CacheErrorCodes.CacheCorrupt, OneLine(ex.Message));
        }
    }

    /// <summary>可恢复类失败：刷新结果保留最后有效快照，并如实报告失败原因。</summary>
    private static CatalogRefreshResult Degrade(CatalogSnapshot? current, string code, string message) => new()
    {
        Outcome = current is null ? CatalogRefreshOutcome.Unavailable : CatalogRefreshOutcome.Stale,
        Snapshot = current,
        Error = new CacheError(code, message),
    };

    // ------------------------------------------------------------------ 资源取数

    private async Task<ResourceAttempt> FetchResourceAsync(
        Uri uri, long maxBytes, ResourceEntry? conditionalOn, bool needMd5, CancellationToken token)
    {
        // 本机开发入口：file:// 不走 HTTP 栈
        if (uri.IsFile) return await FetchLocalFileAsync(uri, maxBytes, needMd5, token).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_options.RootRequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (conditionalOn is not null)
        {
            if (!string.IsNullOrWhiteSpace(conditionalOn.ETag))
                request.Headers.TryAddWithoutValidation("If-None-Match", conditionalOn.ETag);
            if (!string.IsNullOrWhiteSpace(conditionalOn.LastModified))
                request.Headers.TryAddWithoutValidation("If-Modified-Since", conditionalOn.LastModified);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        var etag = response.Headers.ETag;
        var strong = etag is { IsWeak: false } ? etag.Tag : null;
        var lastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);

        if (response.StatusCode == HttpStatusCode.NotModified)
            return ResourceAttempt.NotModified(lastModified);
        if (!response.IsSuccessStatusCode)
            return ResourceAttempt.StatusOnly(response.StatusCode);

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new CacheParseException(CacheErrorCodes.InvalidXml, "资源响应是 HTML（挑战页或错误页）");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var staged = NewStagingPath();
        var (sha256, md5, size) = await CopyBoundedAsync(stream, staged, maxBytes, needMd5, timeout.Token).ConfigureAwait(false);
        return ResourceAttempt.Ok(staged, sha256, md5, size, strong, lastModified);
    }

    private async Task<ResourceAttempt> FetchLocalFileAsync(Uri uri, long maxBytes, bool needMd5, CancellationToken token)
    {
        var info = new FileInfo(uri.LocalPath);
        if (!info.Exists) return ResourceAttempt.StatusOnly(HttpStatusCode.NotFound);
        if (info.Length > maxBytes)
            throw new CacheParseException(CacheErrorCodes.TooLarge, $"资源超过 {maxBytes} 字节上限");

        await using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var staged = NewStagingPath();
        var (sha256, md5, size) = await CopyBoundedAsync(stream, staged, maxBytes, needMd5, token).ConfigureAwait(false);
        return ResourceAttempt.Ok(staged, sha256, md5, size, null,
            info.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 一次读入同时算 SHA-256（对象身份）与可选的 MD5（历史发布物的兼容摘要），避免读完再读一遍。
    /// 超过上限立刻放弃并清掉半截暂存文件。
    /// </summary>
    private async Task<(string Sha256, string? Md5, long Size)> CopyBoundedAsync(
        Stream source, string stagedPath, long maxBytes, bool needMd5, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(stagedPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = needMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;

        long total = 0;
        try
        {
            await using var target = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0) break;

                total += read;
                if (total > maxBytes)
                    throw new CacheParseException(CacheErrorCodes.TooLarge, $"资源超过 {maxBytes} 字节上限");

                sha.AppendData(buffer, 0, read);
                md5?.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            target.Flush(flushToDisk: true);
        }
        catch
        {
            DiscardStaged(stagedPath);
            throw;
        }

        return (Convert.ToHexStringLower(sha.GetHashAndReset()),
                md5 is null ? null : Convert.ToHexStringLower(md5.GetHashAndReset()),
                total);
    }

    private string NewStagingPath() =>
        Path.Combine(_layout.StagingAttempt(Guid.NewGuid().ToString("N")), "resource.part");

    /// <summary>回收我们自己刚写的暂存文件（不是用户文件，也不是已登记对象）。</summary>
    private static void DiscardStaged(string? stagedPath)
    {
        if (string.IsNullOrEmpty(stagedPath)) return;
        try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
        catch (IOException) { /* 回收失败只是留下垃圾，不影响结论 */ }
    }

    private static bool VerifyFile(string path, string algorithm, string expectedHash, out string error)
    {
        error = string.Empty;
        string actual;
        try
        {
            actual = CacheHash.HexOfFile(algorithm, path);
        }
        catch (Exception ex)
        {
            error = OneLine(ex.Message);
            return false;
        }

        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            error = "本地对象与登记节点声明的摘要不符";
            return false;
        }
        return true;
    }

    private async Task<RootAttempt> FetchRootAsync(CachePointer? conditionalOn, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_options.RootRequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, _options.EntryUri);
        // 表示转换会让 Range/长度语义含糊；根目录只要原字节
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (conditionalOn is not null)
        {
            if (!string.IsNullOrWhiteSpace(conditionalOn.ETag))
                request.Headers.TryAddWithoutValidation("If-None-Match", conditionalOn.ETag);
            if (!string.IsNullOrWhiteSpace(conditionalOn.LastModified))
                request.Headers.TryAddWithoutValidation("If-Modified-Since", conditionalOn.LastModified);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        var etag = response.Headers.ETag;
        var lastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);

        if (response.StatusCode == HttpStatusCode.NotModified)
            return new RootAttempt(response.StatusCode, null, null, lastModified);

        if (!response.IsSuccessStatusCode)
            return new RootAttempt(response.StatusCode, null, null, lastModified);

        var body = await ReadBoundedAsync(response, _options.RootMaxBytes, timeout.Token).ConfigureAwait(false);
        // 弱 ETag 不能用于严格验证，不记进指针
        var strong = etag is { IsWeak: false } ? etag.Tag : null;
        return new RootAttempt(response.StatusCode, body, strong, lastModified);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long maxBytes, CancellationToken token)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new CacheParseException(CacheErrorCodes.InvalidXml, "根响应是 HTML（挑战页或错误页）");

        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new CacheParseException(CacheErrorCodes.TooLarge, $"根响应声明 {declared} 字节，超过 {maxBytes} 上限");

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes)
                throw new CacheParseException(CacheErrorCodes.TooLarge, $"根响应超过 {maxBytes} 字节上限");
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        if (!LooksLikeXml(bytes))
            throw new CacheParseException(CacheErrorCodes.InvalidXml, "根响应不是 XML（可能被替换成了错误页）");
        return bytes;
    }

    /// <summary>跳过 BOM 与空白后必须以 <c>&lt;</c> 开头。</summary>
    private static bool LooksLikeXml(byte[] bytes)
    {
        var i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) i = 3;
        while (i < bytes.Length && (bytes[i] == (byte)' ' || bytes[i] == (byte)'\t' || bytes[i] == (byte)'\r' || bytes[i] == (byte)'\n')) i++;
        return i < bytes.Length && bytes[i] == (byte)'<';
    }

    private Task DelayBeforeRetryAsync(int attempt, CancellationToken token)
    {
        var backoff = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 120));
        _options.Log?.Invoke($"缓存刷新第 {attempt} 次重试，{backoff.TotalMilliseconds:0} ms 后继续");
        return Task.Delay(backoff, _options.TimeProvider, token);
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException
        || ex is TaskCanceledException
        || ex is OperationCanceledException;

    private static bool IsTransientStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static string OneLine(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private CatalogSnapshot? TryLoadSnapshot(CachePointer pointer, out CacheError? error)
    {
        error = null;
        try
        {
            var bytes = CacheSnapshotStore.ReadRoot(_layout, pointer.SnapshotId);
            var parsed = CacheSnapshotStore.ParseRoot(bytes, _options.RootMaxBytes);
            var schemaVersion = parsed.Get("SchemaVersion");
            if (!MetadataSchema.IsCompatible(schemaVersion))
            {
                error = new CacheError(CacheErrorCodes.SchemaMismatch,
                    $"快照 SchemaVersion={schemaVersion ?? "(缺失)"} 与本库 {MetadataSchema.Current} 不兼容");
                return null;
            }

            return new CatalogSnapshot
            {
                SnapshotId = pointer.SnapshotId,
                CacheRoot = _layout.Root,
                OriginEntryUri = _options.EntryUri,
                RetrievedAtUtc = pointer.RetrievedAtUtc,
                MetadataPath = _layout.SnapshotMetadataPath(pointer.SnapshotId),
                Root = parsed,
                SchemaVersion = schemaVersion,
                ContentRevision = parsed.Get("ContentRevision"),
            };
        }
        catch (CacheParseException ex)
        {
            error = new CacheError(ex.Code, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            error = new CacheError(CacheErrorCodes.SnapshotMissing, OneLine(ex.Message));
            return null;
        }
    }

    private static CatalogLoadResult Unavailable(CacheError error) => new()
    {
        Status = CatalogFreshness.Unavailable,
        Snapshot = null,
        Error = error,
    };

    /// <summary>一次根请求的扁平结果；响应对象在方法内就释放掉。</summary>
    private sealed record RootAttempt(
        HttpStatusCode Status,
        byte[]? Body,
        string? StrongETag,
        string? LastModified)
    {
        public bool IsSuccess => Status == HttpStatusCode.OK;
    }

    /// <summary>一次资源请求的扁平结果；响应对象在方法内就释放掉，正文落在暂存文件里。</summary>
    private sealed record ResourceAttempt(
        HttpStatusCode Status,
        string? StagedPath,
        string Sha256,
        string? Md5,
        long Size,
        string? StrongETag,
        string? LastModified)
    {
        public bool IsSuccess => Status == HttpStatusCode.OK;

        public static ResourceAttempt StatusOnly(HttpStatusCode status) =>
            new(status, null, string.Empty, null, 0, null, null);

        public static ResourceAttempt NotModified(string? lastModified) =>
            new(HttpStatusCode.NotModified, null, string.Empty, null, 0, null, lastModified);

        public static ResourceAttempt Ok(
            string stagedPath, string sha256, string? md5, long size, string? strongETag, string? lastModified) =>
            new(HttpStatusCode.OK, stagedPath, sha256, md5, size, strongETag, lastModified);
    }
}
