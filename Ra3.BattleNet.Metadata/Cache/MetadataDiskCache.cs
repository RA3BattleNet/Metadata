using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 最后一份校验合法的 XML。每个入口和每个叶子 Source 各占一个目录，发布一个源不会拆掉另一个源。
/// 条件请求只使用摘要匹配的校验器；正文在校验器缺失时仍可离线读取。未完成的 .tmp 不读取。
/// </summary>
/// <remarks>
/// <code>
/// cacheDir/roots/&lt;sha256 of canonical entry URI&gt;/metadata.xml
/// cacheDir/leaves/&lt;sha256 of canonical source URI&gt;/leaf.xml
/// </code>
/// 同目录还有 .etag 与写入中的 .tmp。正文用替换发布，旧正文一直留到替换成功。
/// </remarks>
internal sealed class MetadataDiskCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly string _root;

    public MetadataDiskCache(string cacheDirectory)
    {
        _root = cacheDirectory;
    }

    public static string CanonicalText(Uri uri) => Canonicalize(uri).AbsoluteUri;

    public static Uri CanonicalizeEntry(string metadataUrl)
    {
        if (Uri.TryCreate(metadataUrl, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
                return CanonicalHttp(absolute);
            if (absolute.IsFile)
                return new Uri(Path.GetFullPath(absolute.LocalPath));
            throw new ArgumentException("仅支持 http(s)、file:// 或本地路径", nameof(metadataUrl));
        }

        return new Uri(Path.GetFullPath(metadataUrl));
    }

    public static Uri Canonicalize(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("地址必须是绝对地址", nameof(uri));
        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            return CanonicalHttp(uri);
        if (uri.IsFile)
            return new Uri(Path.GetFullPath(uri.LocalPath));
        throw new ArgumentException("仅支持 http(s) 与 file", nameof(uri));
    }

    public string RootBodyPath(Uri origin) => SlotPath("roots", CanonicalText(origin), "metadata.xml");

    public string LeafBodyPath(string canonicalLeaf) => SlotPath("leaves", canonicalLeaf, "leaf.xml");

    public DiskBody? TryReadRoot(Uri origin) => TryReadBody(RootBodyPath(origin), CanonicalText(origin));

    public DiskBody? TryReadLeaf(string canonicalLeaf) => TryReadBody(LeafBodyPath(canonicalLeaf), canonicalLeaf);

    public async Task StageAsync(string bodyPath, byte[] bytes, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(bodyPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(bodyPath + ".tmp", bytes, ct).ConfigureAwait(false);
    }

    public void Discard(string bodyPath)
    {
        var tmp = bodyPath + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);
    }

    /// <summary>
    /// 只在临时正文已经校验通过后调用。先原子替换正式正文，成功后再替换校验器。
    /// 替换失败时旧正文和旧校验器都还在。
    /// </summary>
    public void Commit(string bodyPath, CacheStamp stamp)
    {
        var tmp = bodyPath + ".tmp";
        if (!File.Exists(tmp))
            throw new IOException("待发布的临时文件不存在");

        var directory = Path.GetDirectoryName(bodyPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(bodyPath))
            File.Replace(tmp, bodyPath, destinationBackupFileName: null);
        else
            File.Move(tmp, bodyPath);

        WriteJson(bodyPath + ".etag", stamp);
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private string SlotPath(string kind, string canonicalUri, string fileName)
    {
        var hash = Sha256Hex(Encoding.UTF8.GetBytes(canonicalUri));
        return Path.Combine(_root, kind, hash, fileName);
    }

    private static DiskBody? TryReadBody(string path, string expectedUri)
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
        var verified = stamp is not null
            && string.Equals(stamp.Uri, expectedUri, StringComparison.Ordinal)
            && string.Equals(stamp.Sha256, digest, StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(stamp.ETag) || !string.IsNullOrWhiteSpace(stamp.LastModified));
        return new DiskBody(path, bytes, digest, verified ? stamp : null, verified);
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
}

internal sealed class CacheStamp
{
    public string Uri { get; set; } = "";

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public string Sha256 { get; set; } = "";
}

internal sealed record DiskBody(string Path, byte[] Bytes, string Digest, CacheStamp? Stamp, bool Verified);
