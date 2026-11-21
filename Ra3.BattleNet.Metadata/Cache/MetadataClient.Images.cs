using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 登记图片的读取、缓存与更新通知。图片身份只来自调用方传入的快照，正文和 Markdown 走同一条传输路径。
/// </summary>
public sealed partial class MetadataClient
{
    private static readonly TimeSpan ImageReadCooldown = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Task<ImageResult>> _imageFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _imageReadBlockedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _liveImageDigests = new(StringComparer.Ordinal);

    /// <summary>
    /// 图片下载完成、或者内容发生真实变化后触发。订阅方收到的是图片完整 URL。
    /// 每次更新只触发一次；如果服务器返回 304、请求失败或者文件内容没变，都不会触发。
    /// 触发时新图片已写入本地缓存，事件回调中再次读取会直接命中缓存，不会重复发请求。
    /// </summary>
    public event EventHandler<ImageUpdatedEventArgs>? ImageUpdated;

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
}
