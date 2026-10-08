using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 最后一份校验合法的 XML 与图片。正文按**发布文件树**落盘：第一次绑定的主发布基直接映射到缓存目录，
/// 其余发布基放在可读的 <c>.sources</c> 子树，带查询串的地址放进 <c>.requests</c> 保留区。每个正文旁的
/// <c>.etag</c> 记录规范化 URI、正文 SHA256、图片响应类型与条件请求校验器。校验器缺失时正文仍可离线读取；
/// 路径归属写在一次成型的 <c>.uri</c> 旁挂文件里，所以同一磁盘路径上属于**另一个地址**的正文既不会被误读，
/// 也不会被覆盖。
/// </summary>
/// <remarks>
/// <code>
/// cacheDir/origin.json                                  一次性绑定主发布基，刷新不会改写
/// cacheDir/metadata.xml                                 主发布基的根清单
/// cacheDir/&lt;相对 Source&gt;                             主发布基的叶子与图片（与发布文件树同名）
/// cacheDir/.sources/&lt;scheme&gt;/&lt;host_port&gt;/&lt;path&gt;       其他发布基的正文
/// cacheDir/.requests/&lt;请求键&gt;/body                     带查询串的图片正文（键 = 完整 URI 的 SHA256）
/// </code>
/// 同目录还有 <c>.etag</c>（校验器、正文 SHA256 与图片类型）、<c>.uri</c>（一次成型的路径归属地址）和写入中的
/// <c>.tmp</c>。正文用替换发布，旧正文一直留到替换成功。只接受静态发布文件树：跳转/空路径段、
/// 编码的斜杠或反斜杠、Windows 歧义名（保留设备名、结尾空格或点）、旁挂后缀（<c>.etag</c>/<c>.tmp</c>/<c>.uri</c>）、
/// 带用户信息或非 DNS/IPv4 主机的地址，以及会占用 <c>origin.json</c>、<c>.sources</c>、<c>.requests</c> 命名空间的
/// 主发布基相对路径，都在写入前直接拒绝；查询串只对图片地址开放，并且一定落在 <c>.requests</c> 里。
/// 主发布基相对路径不会与保留子树交叉，路径身份由 <c>origin.json</c> 绑定加上每个路径的归属地址唯一决定。
/// </remarks>
internal sealed class MetadataDiskCache
{
    private const string OriginFileName = "origin.json";
    private const string SourcesDirectoryName = ".sources";
    private const string RequestsDirectoryName = ".requests";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly object _originGate = new();
    private bool _primaryLoaded;
    private bool _primaryExists;
    private Uri? _primaryBase;

    public MetadataDiskCache(string cacheDirectory)
    {
        _root = Path.GetFullPath(cacheDirectory);
        _rootPrefix = _root.TrimEnd(Separators) + Path.DirectorySeparatorChar;
    }

    public static string CanonicalText(Uri uri) => Canonicalize(uri).AbsoluteUri;

    public static Uri CanonicalizeEntry(string metadataUrl)
    {
        if (string.IsNullOrWhiteSpace(metadataUrl))
            throw new ArgumentException("入口地址不能为空", nameof(metadataUrl));

        if (Uri.TryCreate(metadataUrl, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps || absolute.IsFile)
                return Canonicalize(absolute);
            throw new ArgumentException("仅支持 http(s)、file:// 或本地路径", nameof(metadataUrl));
        }

        return Canonicalize(new Uri(Path.GetFullPath(metadataUrl)));
    }

    public static Uri Canonicalize(Uri uri)
    {
        var canonical = CanonicalizeCore(uri);
        if (Segments(canonical).Count == 0 || EndsWithSeparator(canonical))
            throw new ArgumentException("地址缺少文件名");
        return canonical;
    }

    /// <summary>
    /// 图片地址的规范化：与 <see cref="Canonicalize"/> 同一条规则，但**允许查询串**——
    /// 外链图片合法地带查询参数，查询串参与缓存身份而不是被丢掉。
    /// </summary>
    public static Uri CanonicalizeImage(Uri uri)
    {
        var canonical = CanonicalizeCore(uri, allowQuery: true);
        if (Segments(canonical).Count == 0 || EndsWithSeparator(canonical))
            throw new ArgumentException("地址缺少文件名");
        return canonical;
    }

    /// <summary>图片请求键：去掉 fragment 的完整规范化 URI 的 SHA-256，用来隔离带查询串的地址。</summary>
    public static string RequestKey(Uri canonical) => Sha256Hex(Encoding.UTF8.GetBytes(canonical.AbsoluteUri));

    private static bool EndsWithSeparator(Uri canonical)
    {
        if (canonical.IsFile)
            return canonical.LocalPath.EndsWith(Path.DirectorySeparatorChar)
                || canonical.LocalPath.EndsWith(Path.AltDirectorySeparatorChar);
        return canonical.AbsolutePath.EndsWith('/');
    }

    private static Uri CanonicalizeCore(Uri uri, bool allowQuery = false)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("地址必须是绝对地址", nameof(uri));
        if (uri.Scheme is not ("http" or "https") && !uri.IsFile)
            throw new ArgumentException("仅支持 http(s) 与 file", nameof(uri));

        Uri canonical;
        if (uri.IsFile)
        {
            canonical = new Uri(Path.GetFullPath(uri.LocalPath));
        }
        else
        {
            ValidateHttpAuthority(uri);
            canonical = CanonicalHttp(uri);
        }

        if (!allowQuery && !string.IsNullOrEmpty(canonical.Query))
            throw new ArgumentException("缓存只接受静态发布文件地址，不接受查询串");
        _ = Segments(canonical);
        return canonical;
    }

    /// <summary>主机必须是严格的 DNS 名或 IPv4 字面量：地址段不能靠有损替换来编码。</summary>
    private static void ValidateHttpAuthority(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("发布地址不能带用户信息");
        if (Uri.CheckHostName(uri.Host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            throw new ArgumentException($"不支持的发布地址主机：{uri.Host}");
    }

    /// <summary>入口地址所在的发布基目录（目录 URI，带结尾斜杠）。</summary>
    public static Uri PublishingBase(Uri entry) => new(Canonicalize(entry), ".");

    /// <summary>
    /// 一次性绑定主发布基。已经绑定过（本进程或磁盘上的 origin.json）就永不改写，
    /// 这样路径映射跨进程稳定，打开别的入口也不会动到主发布基的正文。
    /// </summary>
    public void BindPrimary(Uri entry)
    {
        var baseUri = PublishingBase(entry);
        lock (_originGate)
        {
            LoadPrimaryLocked();
            if (_primaryExists)
            {
                if (_primaryBase is null)
                    throw new IOException($"缓存主发布基绑定无法解析：{Path.Combine(_root, OriginFileName)}");
                return;
            }

            var path = Path.Combine(_root, OriginFileName);
            try
            {
                Directory.CreateDirectory(_root);
                WriteAtomically(path, JsonSerializer.Serialize(new CacheOrigin { PublishingBase = baseUri.AbsoluteUri }, JsonOptions));
            }
            catch (IOException) when (File.Exists(path))
            {
                // 另一个进程先完成了绑定，读回赢家；其余 IO 失败不受影响，原样抛出。
            }

            var (exists, value) = ReadOriginFile();
            _primaryLoaded = true;
            _primaryExists = exists;
            _primaryBase = value;
            if (!exists || value is null)
                throw new IOException($"无法建立缓存主发布基绑定：{path}");
        }
    }

    public string RootBodyPath(Uri origin) => BodyPath(Canonicalize(origin));

    public string LeafBodyPath(Uri leaf) => BodyPath(Canonicalize(leaf));

    /// <summary>
    /// 图片正文路径。相对 Source 图片与 XML 共用发布文件树；带查询串的地址按完整请求 URI 的
    /// SHA-256 落到保留目录 <c>.requests/&lt;key&gt;/body</c>，不同查询串不会撞到同一份正文。
    /// </summary>
    public string ImageBodyPath(Uri image)
    {
        var canonical = CanonicalizeImage(image);
        return string.IsNullOrEmpty(canonical.Query)
            ? BodyPath(canonical)
            : Combine([RequestsDirectoryName, RequestKey(canonical), "body"]);
    }

    public DiskBody? TryReadRoot(Uri origin)
    {
        var canonical = Canonicalize(origin);
        return TryReadBody(BodyPath(canonical), canonical.AbsoluteUri, requireValidators: true);
    }

    public DiskBody? TryReadLeaf(Uri leaf)
    {
        var canonical = Canonicalize(leaf);
        return TryReadBody(BodyPath(canonical), canonical.AbsoluteUri, requireValidators: true);
    }

    /// <summary>
    /// 读图片正文：只要正文 SHA-256 与旁挂摘要一致即可离线使用，不要求存在条件请求校验器。
    /// 旁挂缺失或摘要对不上都返回 null，由调用方重新下载。
    /// </summary>
    public DiskBody? TryReadImage(Uri image)
    {
        var canonical = CanonicalizeImage(image);
        return TryReadBody(ImageBodyPath(canonical), canonical.AbsoluteUri, requireValidators: false);
    }

    public async Task StageAsync(string bodyPath, string canonicalUri, byte[] bytes, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(bodyPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        EnsureOwner(bodyPath, canonicalUri);
        await File.WriteAllBytesAsync(bodyPath + ".tmp", bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 每个磁盘路径的归属地址写在一次成型的 <c>.uri</c> 旁挂文件里，先于正文落盘。
    /// 即使之后 <c>.etag</c> 丢失，也能证明这份正文属于哪个地址；别的地址一律不能占用同一路径。
    /// </summary>
    private static void EnsureOwner(string bodyPath, string canonicalUri)
    {
        var ownerPath = bodyPath + ".uri";
        if (File.Exists(ownerPath))
        {
            var (_, existing) = ReadOwner(ownerPath);
            if (existing is null || !string.Equals(existing, canonicalUri, StringComparison.Ordinal))
                throw new IOException($"缓存路径已被另一个地址占用：{existing ?? ownerPath}");
            return;
        }

        var tmp = ownerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, canonicalUri, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, ownerPath);
        }
        catch (IOException) when (File.Exists(ownerPath))
        {
            TryDelete(tmp);
        }
        catch (IOException)
        {
            TryDelete(tmp);
            throw;
        }

        var (exists, owner) = ReadOwner(ownerPath);
        if (!exists || owner is null)
            throw new IOException($"无法写入缓存路径归属：{ownerPath}");
        if (!string.Equals(owner, canonicalUri, StringComparison.Ordinal))
            throw new IOException($"缓存路径已被另一个地址占用：{owner}");
    }

    private static (bool Exists, string? Owner) ReadOwner(string path)
    {
        if (!File.Exists(path))
            return (false, null);
        try
        {
            return (true, File.ReadAllText(path).Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, null);
        }
    }

    public void Discard(string bodyPath)
    {
        var tmp = bodyPath + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);
    }

    /// <summary>
    /// 只在临时正文已经校验通过后调用。先原子替换正式正文，成功后再替换校验器。
    /// 若该磁盘路径上已有属于**另一个 URI** 的正文，直接失败，绝不覆盖别人。
    /// </summary>
    public void Commit(string bodyPath, CacheStamp stamp)
    {
        var tmp = bodyPath + ".tmp";
        if (!File.Exists(tmp))
            throw new IOException("待发布的临时文件不存在");

        var stampPath = bodyPath + ".etag";
        var existing = ReadStamp(stampPath);
        if (existing is not null && !string.Equals(existing.Uri, stamp.Uri, StringComparison.Ordinal))
            throw new IOException($"缓存路径已被另一个地址占用：{existing.Uri}");

        var (ownerExists, owner) = ReadOwner(bodyPath + ".uri");
        if (ownerExists && !string.Equals(owner, stamp.Uri, StringComparison.Ordinal))
            throw new IOException($"缓存路径已被另一个地址占用：{owner ?? bodyPath + ".uri"}");

        var directory = Path.GetDirectoryName(bodyPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(bodyPath))
            File.Replace(tmp, bodyPath, destinationBackupFileName: null);
        else
            File.Move(tmp, bodyPath);

        WriteJson(stampPath, stamp);
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private string BodyPath(Uri canonical)
    {
        var primary = CurrentPrimary();
        if (primary is not null && TryRelative(canonical, primary, out var relative))
        {
            RejectPrimaryReserved(relative);
            RejectSidecarName(relative[^1]);
            return Combine(relative);
        }

        var segments = new List<string>
        {
            SourcesDirectoryName,
            canonical.Scheme.ToLowerInvariant(),
            AuthoritySegment(canonical),
        };
        segments.AddRange(Segments(canonical));
        RejectSidecarName(segments[^1]);
        return Combine(segments);
    }

    private static void RejectSidecarName(string fileName)
    {
        if (fileName.EndsWith(".etag", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".uri", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"缓存正文文件名不能占用旁挂文件后缀：{fileName}");
    }

    private static void RejectPrimaryReserved(List<string> relative)
    {
        if (relative.Count == 0)
            return;
        var first = relative[0];
        if (string.Equals(first, OriginFileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"相对 Source 不能占用缓存绑定文件 {OriginFileName}");
        if (string.Equals(first, SourcesDirectoryName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"相对 Source 不能占用缓存保留目录 {SourcesDirectoryName}");
        if (string.Equals(first, RequestsDirectoryName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"相对 Source 不能占用缓存保留目录 {RequestsDirectoryName}");
    }

    private string Combine(List<string> segments)
    {
        var path = _root;
        foreach (var segment in segments)
            path = Path.Combine(path, segment);

        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_rootPrefix, PathComparison) && !string.Equals(full, _root, PathComparison))
            throw new InvalidOperationException("缓存路径越界");
        return full;
    }

    private static bool TryRelative(Uri canonical, Uri primary, out List<string> relative)
    {
        relative = [];
        if (canonical.IsFile != primary.IsFile)
            return false;

        if (canonical.IsFile)
        {
            // 文件发布基按序号比较：大小写不同的路径是不同地址，落到 .sources，
            // 由此不会在 Windows 上悄悄合并成同一份正文。
            if (!string.Equals(Path.GetPathRoot(canonical.LocalPath), Path.GetPathRoot(primary.LocalPath), StringComparison.Ordinal))
                return false;
        }
        else if (!string.Equals(canonical.GetLeftPart(UriPartial.Authority), primary.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
        {
            return false;
        }

        var head = Segments(primary);
        var tail = Segments(canonical);
        if (tail.Count <= head.Count)
            return false;
        for (var i = 0; i < head.Count; i++)
        {
            if (!string.Equals(head[i], tail[i], StringComparison.Ordinal))
                return false;
        }

        relative = tail.GetRange(head.Count, tail.Count - head.Count);
        return true;
    }

    private Uri? CurrentPrimary()
    {
        lock (_originGate)
        {
            LoadPrimaryLocked();
            return _primaryBase;
        }
    }

    private void LoadPrimaryLocked()
    {
        if (_primaryLoaded)
            return;
        var (exists, value) = ReadOriginFile();
        _primaryLoaded = true;
        _primaryExists = exists;
        _primaryBase = value;
    }

    private (bool Exists, Uri? Base) ReadOriginFile()
    {
        var path = Path.Combine(_root, OriginFileName);
        if (!File.Exists(path))
            return (false, null);

        try
        {
            var origin = JsonSerializer.Deserialize<CacheOrigin>(File.ReadAllText(path), JsonOptions);
            if (origin is null || string.IsNullOrWhiteSpace(origin.PublishingBase))
                return (true, null);
            return (true, CanonicalizeCore(new Uri(origin.PublishingBase, UriKind.Absolute)));
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or UriFormatException or UnauthorizedAccessException)
        {
            return (true, null);
        }
    }

    private static void WriteAtomically(string path, string text)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不改变调用方的结论。
        }
    }

    private static DiskBody? TryReadBody(string path, string expectedUri, bool requireValidators)
    {
        if (!File.Exists(path))
            return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }

        if (bytes.Length == 0)
            return null;

        var digest = Sha256Hex(bytes);
        var stamp = ReadStamp(path + ".etag");
        var stampUri = stamp is not null && !string.IsNullOrEmpty(stamp.Uri) ? stamp.Uri : null;
        var ownerKnown = stampUri is not null;
        string? owner = stampUri;
        if (!ownerKnown)
        {
            var (ownerExists, ownerUri) = ReadOwner(path + ".uri");
            ownerKnown = ownerExists;
            owner = ownerUri;
        }

        if (ownerKnown && !string.Equals(owner, expectedUri, StringComparison.Ordinal))
            return null;

        var integrity = stamp is not null && string.Equals(stamp.Sha256, digest, StringComparison.OrdinalIgnoreCase);
        var validators = integrity
            && (!string.IsNullOrWhiteSpace(stamp!.ETag) || !string.IsNullOrWhiteSpace(stamp.LastModified));
        if (requireValidators)
            return new DiskBody(path, bytes, digest, validators ? stamp : null, validators);

        // 图片离线优先：摘要对得上就能用，校验器只决定能不能做条件请求。
        return integrity ? new DiskBody(path, bytes, digest, stamp, validators) : null;
    }

    private static CacheStamp? ReadStamp(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<CacheStamp>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }

    private static Uri CanonicalHttp(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty,
        };
        if (uri.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri;
    }

    private static string AuthoritySegment(Uri canonical)
    {
        if (canonical.IsFile)
            return "local";

        // 主机已在 CanonicalizeCore 里限定为 DNS 名或 IPv4，小写后天然是安全的路径段。
        var host = canonical.Host.ToLowerInvariant();
        return canonical.IsDefaultPort
            ? host
            : host + "_" + canonical.Port.ToString(CultureInfo.InvariantCulture);
    }

    private static List<string> Segments(Uri canonical)
    {
        var segments = new List<string>();
        if (canonical.IsFile)
        {
            var parts = canonical.LocalPath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].Length == 2 && parts[0][1] == ':')
                parts[0] = parts[0][..1];
            foreach (var part in parts)
                segments.Add(ValidateSegment(part));
            return segments;
        }

        var raw = canonical.AbsolutePath.Split('/');
        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i].Length == 0)
            {
                if (i == raw.Length - 1)
                    break;
                throw new ArgumentException("地址含有空的路径段");
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(raw[i]);
            }
            catch (UriFormatException)
            {
                throw new ArgumentException("地址含有无法解码的路径段");
            }

            segments.Add(ValidateSegment(decoded));
        }

        return segments;
    }

    private static string ValidateSegment(string segment)
    {
        if (segment.Length == 0)
            throw new ArgumentException("地址含有空的路径段");
        if (segment is "." or "..")
            throw new ArgumentException("地址含有跳转路径段");

        foreach (var ch in segment)
        {
            if (ch < ' ' || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
                throw new ArgumentException($"地址含有不安全的路径段字符：{segment}");
        }

        if (segment[^1] is ' ' or '.')
            throw new ArgumentException($"地址含有 Windows 会改写的路径段结尾：{segment}");
        if (IsReservedDeviceName(segment))
            throw new ArgumentException($"地址含有 Windows 保留设备名：{segment}");
        return segment;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];
        if (stem.Length == 0)
            return false;
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9';
    }
}

internal sealed class CacheOrigin
{
    public string PublishingBase { get; set; } = "";
}

internal sealed class CacheStamp
{
    public string Uri { get; set; } = "";

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public string Sha256 { get; set; } = "";

    /// <summary>图片响应类型。XML 正文不写这个字段，缺失时按旧缓存处理。</summary>
    public string? ContentType { get; set; }
}

internal sealed record DiskBody(string Path, byte[] Bytes, string Digest, CacheStamp? Stamp, bool Verified);
