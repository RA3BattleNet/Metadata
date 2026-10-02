using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

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
}
