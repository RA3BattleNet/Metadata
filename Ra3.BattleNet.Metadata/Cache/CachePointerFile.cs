using System.Globalization;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>当前指针：指向一个已验证快照，并记下这个快照对应的远端缓存验证器。</summary>
internal sealed record CachePointer
{
    /// <summary>单调递增代次。读取方在 current/previous/pending 里取**最高有效**的一份。</summary>
    public required int Generation { get; init; }

    /// <summary>快照 ID（根 XML 原文字节 SHA-256）。</summary>
    public required string SnapshotId { get; init; }

    /// <summary>快照取得时间（UTC）。</summary>
    public required DateTimeOffset RetrievedAtUtc { get; init; }

    /// <summary>强 ETag；没有时为空。弱 ETag 不写进来，因为它不能用于 If-Range 式的严格验证。</summary>
    public string? ETag { get; init; }

    /// <summary>Last-Modified 原文；只在协议允许时才用于严格验证。</summary>
    public string? LastModified { get; init; }

    /// <summary>发布者标签（<c>ContentRevision</c>）。</summary>
    public string? ContentRevision { get; init; }

    /// <summary>这个指针属于哪个入口身份，防止换了入口却复用旧指针。</summary>
    public required string OriginIdentity { get; init; }
}

/// <summary>
/// 指针文件的崩溃安全读写。
///
/// 一个快照的切换是三步：写 <c>.pending</c> → 回读验证 → <c>current</c> 改名为 <c>.previous</c>、
/// <c>.pending</c> 改名为 <c>current</c>。任何一步中断后，读取方在 current/previous/pending 里
/// 取**最高有效代次**；"有效"包括摘要自洽且指向的根快照确实存在、字节对得上。
/// </summary>
internal static class CachePointerFile
{
    public const int SchemaVersion = 1;

    private const string RootName = "CachePointer";

    /// <summary>读取最高有效代次的指针；一份都没有时返回 null。</summary>
    public static CachePointer? ReadHighest(CacheLayout layout)
    {
        CachePointer? best = null;
        foreach (var candidate in ReadAll(layout))
        {
            if (best is null || candidate.Generation > best.Generation) best = candidate;
        }
        return best;
    }

    /// <summary>读取所有有效代次（current/previous/pending 里通过校验的那些）。</summary>
    public static IReadOnlyList<CachePointer> ReadAll(CacheLayout layout)
    {
        var pointers = new List<CachePointer>();
        foreach (var path in GenerationFile.Candidates(layout.CurrentPointerPath))
        {
            var candidate = Read(layout, path, 0);
            if (candidate is null) continue;
            if (pointers.Any(p => p.Generation == candidate.Generation)) continue;
            pointers.Add(candidate);
        }
        return pointers;
    }

    /// <summary>下一次写入应使用的代次。</summary>
    public static int NextGeneration(CacheLayout layout) => (ReadHighest(layout)?.Generation ?? 0) + 1;

    /// <summary>发布一个指针：先写 pending 并回读验证，验证通过才动 current。</summary>
    public static void Publish(CacheLayout layout, CachePointer pointer)
    {
        layout.EnsureDirectories();
        GenerationFile.Publish(
            layout.CurrentPointerPath,
            Serialize(pointer),
            path => Read(layout, path, 0) is not null);
    }

    /// <summary>指针自洽性的规范化输入（不含 Digest 字段本身）。</summary>
    public static string Canonical(CachePointer pointer) => string.Join('\n',
        SchemaVersion.ToString(CultureInfo.InvariantCulture),
        pointer.Generation.ToString(CultureInfo.InvariantCulture),
        pointer.SnapshotId,
        pointer.RetrievedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        pointer.ETag ?? string.Empty,
        pointer.LastModified ?? string.Empty,
        pointer.ContentRevision ?? string.Empty,
        pointer.OriginIdentity);

    public static string DigestOf(CachePointer pointer) => CacheHash.Sha256Hex(Canonical(pointer));

    private static byte[] Serialize(CachePointer pointer)
    {
        var doc = new XDocument(
            new XElement(RootName,
                new XAttribute("SchemaVersion", SchemaVersion),
                new XAttribute("Generation", pointer.Generation),
                new XAttribute("SnapshotId", pointer.SnapshotId),
                new XAttribute("RetrievedAtUtc", pointer.RetrievedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                new XAttribute("ETag", pointer.ETag ?? string.Empty),
                new XAttribute("LastModified", pointer.LastModified ?? string.Empty),
                new XAttribute("ContentRevision", pointer.ContentRevision ?? string.Empty),
                new XAttribute("Origin", pointer.OriginIdentity),
                new XAttribute("Digest", DigestOf(pointer))));

        return CacheFile.Serialize(doc);
    }

    /// <summary>
    /// 读一份指针。任何一项不成立都返回 null（调用方据此回退到其他代次或报告不可用）：
    /// 结构、版本、代次、摘要、快照目录名合法性，以及"快照文件确实存在且字节与 SnapshotId 相符"。
    /// </summary>
    private static CachePointer? Read(CacheLayout layout, string path, int skipGenerationAtOrBelow)
    {
        if (!File.Exists(path)) return null;

        XDocument doc;
        try
        {
            doc = CacheFile.LoadSmallXml(path, CacheFile.MaxStateFileBytes);
        }
        catch
        {
            return null;
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != RootName) return null;
        if (!int.TryParse(root.Attribute("SchemaVersion")?.Value, out var version) || version != SchemaVersion) return null;
        if (!int.TryParse(root.Attribute("Generation")?.Value, out var generation) || generation <= 0) return null;
        if (generation <= skipGenerationAtOrBelow) return null;

        var snapshotId = root.Attribute("SnapshotId")?.Value;
        if (!CacheHash.IsLowerHex(snapshotId, CacheHash.Sha256HexLength)) return null;

        if (!DateTimeOffset.TryParse(root.Attribute("RetrievedAtUtc")?.Value,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var retrievedAt)) return null;

        var origin = root.Attribute("Origin")?.Value;
        if (string.IsNullOrWhiteSpace(origin)) return null;

        var pointer = new CachePointer
        {
            Generation = generation,
            SnapshotId = snapshotId!,
            RetrievedAtUtc = retrievedAt,
            ETag = Empty(root.Attribute("ETag")?.Value),
            LastModified = Empty(root.Attribute("LastModified")?.Value),
            ContentRevision = Empty(root.Attribute("ContentRevision")?.Value),
            OriginIdentity = origin!,
        };

        if (!string.Equals(root.Attribute("Digest")?.Value, DigestOf(pointer), StringComparison.Ordinal)) return null;

        // 指针必须引用一个真实存在、字节对得上的根：否则它跟没有一样
        var metadataPath = layout.SnapshotMetadataPath(pointer.SnapshotId);
        if (!File.Exists(metadataPath)) return null;
        try
        {
            if (!string.Equals(CacheHash.Sha256HexOfFile(metadataPath), pointer.SnapshotId, StringComparison.Ordinal)) return null;
        }
        catch
        {
            return null;
        }

        return pointer;
    }

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
