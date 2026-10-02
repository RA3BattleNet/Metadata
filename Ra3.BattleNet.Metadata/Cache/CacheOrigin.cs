using System.Xml;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 缓存根与入口来源的绑定。
///
/// 一个 <c>CacheRoot</c> 固定服务一个规范化入口，首次使用时写 <c>origin.xml</c>；
/// 之后入口不匹配就拒绝复用该根。理由不是洁癖：相对 <c>Source</c> 的含义依赖 origin，
/// 两个入口即使根 XML 字节相同，解析出来的资源地址也可能指向不同东西。
/// </summary>
internal static class CacheOrigin
{
    public const int SchemaVersion = 1;

    private const string RootName = "CacheOrigin";

    /// <summary>
    /// 入口身份：只取 scheme + host + port + path（丢掉 query/fragment 与 userinfo）。
    /// 带凭据的查询串因此不进身份、也不落盘；它只影响请求级缓存键。
    /// </summary>
    public static string IdentityOf(Uri entry)
    {
        if (entry.IsFile) return new Uri(Path.GetFullPath(entry.LocalPath)).AbsoluteUri;
        return entry.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    /// <summary>确保缓存根绑定在本入口上；返回 null 表示可以继续使用，否则给出拒绝原因。</summary>
    public static CacheError? EnsureBound(CacheLayout layout, Uri entry)
    {
        var identity = IdentityOf(entry);
        Directory.CreateDirectory(layout.Root);

        if (File.Exists(layout.OriginPath))
        {
            var read = Read(layout.OriginPath);
            if (read.Error is { } error) return error;
            if (!string.Equals(read.Identity, identity, StringComparison.Ordinal))
                return new CacheError(CacheErrorCodes.OriginMismatch,
                    $"缓存根已绑定另一个入口来源（{read.Identity}），请为 {identity} 另选一个缓存目录");
            return null;
        }

        // 没有绑定文件：只认领真正空的缓存根。已有指针却没有绑定文件，说明这个根来路不明，
        // 不能把里面的资源映射当成当前入口的。
        if (File.Exists(layout.CurrentPointerPath)
            || File.Exists(layout.PreviousPointerPath)
            || File.Exists(layout.PendingPointerPath))
        {
            return new CacheError(CacheErrorCodes.OriginMismatch,
                "缓存根里已有快照却没有来源绑定文件，无法确认它属于哪个入口；请另选一个缓存目录");
        }

        Write(layout.OriginPath, identity);
        return null;
    }

    private static void Write(string path, string identity)
    {
        var payload = $"1\n{identity}";
        var doc = new XDocument(
            new XElement(RootName,
                new XAttribute("SchemaVersion", SchemaVersion),
                new XAttribute("Identity", identity),
                new XAttribute("Digest", CacheHash.Sha256Hex(payload))));

        CacheFile.WriteAtomic(path, CacheFile.Serialize(doc));
    }

    private static (string? Identity, CacheError? Error) Read(string path)
    {
        XDocument doc;
        try
        {
            doc = CacheFile.LoadSmallXml(path, CacheFile.MaxStateFileBytes);
        }
        catch (Exception ex)
        {
            return (null, new CacheError(CacheErrorCodes.CacheCorrupt, $"来源绑定文件无法解析：{ex.Message}"));
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != RootName)
            return (null, new CacheError(CacheErrorCodes.CacheCorrupt, "来源绑定文件根节点不是 CacheOrigin"));

        var versionText = root.Attribute("SchemaVersion")?.Value;
        if (!int.TryParse(versionText, out var version))
            return (null, new CacheError(CacheErrorCodes.CacheCorrupt, "来源绑定文件缺少 SchemaVersion"));
        if (version != SchemaVersion)
            return (null, new CacheError(CacheErrorCodes.CacheCorrupt,
                $"来源绑定文件版本 {version} 不是本库认识的 {SchemaVersion}（保留原文件，不覆盖）"));

        var identity = root.Attribute("Identity")?.Value;
        if (string.IsNullOrWhiteSpace(identity))
            return (null, new CacheError(CacheErrorCodes.CacheCorrupt, "来源绑定文件缺少 Identity"));

        var expected = CacheHash.Sha256Hex($"1\n{identity}");
        if (!string.Equals(root.Attribute("Digest")?.Value, expected, StringComparison.Ordinal))
            return (null, new CacheError(CacheErrorCodes.CacheCorrupt, "来源绑定文件摘要不符"));

        return (identity, null);
    }
}
