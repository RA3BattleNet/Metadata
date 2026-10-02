using System.Globalization;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>一个登记资源在某快照下的解析结果与本地对象身份。</summary>
internal sealed record ResourceEntry
{
    public required string RegistryId { get; init; }

    /// <summary>登记节点上的 Source 原文。</summary>
    public required string Source { get; init; }

    /// <summary>按原远端入口解析出来的地址。地址变了就不能复用旧对象。</summary>
    public required string ResolvedUrl { get; init; }

    public string? ExpectedHash { get; init; }

    public string? ExpectedHashAlgorithm { get; init; }

    public long? ExpectedSize { get; init; }

    /// <summary>本地对象摘要；没有对象时为 null。</summary>
    public string? ObjectDigest { get; init; }

    public long ObjectSize { get; init; }

    /// <summary><c>Ready</c> 或 <c>Failed</c>。</summary>
    public string Status { get; init; } = Ready;

    public ResourceBinding Binding { get; init; }

    /// <summary>远端强验证器；只有强 ETag 才写进来。</summary>
    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public DateTimeOffset? FetchedAtUtc { get; init; }

    /// <summary>最后一次失败原因（脱敏、单行）。</summary>
    public string? LastError { get; init; }

    public const string Ready = "Ready";

    public const string Failed = "Failed";
}

/// <summary>
/// 某个快照的资源映射：登记 ID → 已解析地址、期望摘要、本地对象与最后错误。
///
/// 它是**派生数据**：丢了能靠重新解析重建，所以不做"损坏就不能继续"的处理；
/// 但它仍然按 generation 崩溃安全保存，避免半截文件把已知对象变成未知。
/// </summary>
internal sealed class ResourceMap
{
    private readonly Dictionary<string, ResourceEntry> _entries = new(StringComparer.Ordinal);

    public int Generation { get; init; }

    public required string SnapshotId { get; init; }

    public IReadOnlyDictionary<string, ResourceEntry> Entries => _entries;

    public const int SchemaVersion = 1;

    internal const string RootName = "ResourceMap";

    public static ResourceMap Empty(string snapshotId) => new() { Generation = 0, SnapshotId = snapshotId };

    public ResourceEntry? Find(string registryId) => _entries.TryGetValue(registryId, out var entry) ? entry : null;

    public void Put(ResourceEntry entry) => _entries[entry.RegistryId] = entry;

    public byte[] Serialize()
    {
        var root = new XElement(RootName,
            new XAttribute("SchemaVersion", SchemaVersion),
            new XAttribute("Generation", Generation),
            new XAttribute("SnapshotId", SnapshotId));

        foreach (var entry in _entries.Values.OrderBy(e => e.RegistryId, StringComparer.Ordinal))
        {
            var element = new XElement("Resource",
                new XAttribute("RegistryId", entry.RegistryId),
                new XAttribute("Source", entry.Source),
                new XAttribute("ResolvedUrl", entry.ResolvedUrl),
                new XAttribute("ObjectSize", entry.ObjectSize),
                new XAttribute("Status", entry.Status),
                new XAttribute("Binding", entry.Binding.ToString()));

            Add(element, "ExpectedHash", entry.ExpectedHash);
            Add(element, "ExpectedHashAlgorithm", entry.ExpectedHashAlgorithm);
            if (entry.ExpectedSize is { } expectedSize) element.Add(new XAttribute("ExpectedSize", expectedSize));
            Add(element, "ObjectDigest", entry.ObjectDigest);
            Add(element, "ETag", entry.ETag);
            Add(element, "LastModified", entry.LastModified);
            if (entry.FetchedAtUtc is { } fetchedAt)
                element.Add(new XAttribute("FetchedAtUtc", fetchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            Add(element, "LastError", entry.LastError);

            root.Add(element);
        }

        return CacheFile.Serialize(new XDocument(root));
    }

    private static void Add(XElement element, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) element.Add(new XAttribute(name, value));
    }
}

/// <summary>资源映射的读写。每个快照一份，按 generation 崩溃安全发布。</summary>
internal sealed class ResourceMapStore
{
    private readonly CacheLayout _layout;
    private readonly string _snapshotId;

    public ResourceMapStore(CacheLayout layout, string snapshotId)
    {
        _layout = layout;
        _snapshotId = snapshotId;
    }

    public string MapPath => _layout.SnapshotResourcesPath(_snapshotId);

    /// <summary>
    /// 读最高有效代次的映射。
    /// 返回错误只有一种情形：盘上是本库不认识的格式版本 —— 那种情况下调用方必须停手，不能拿新内容盖掉它。
    /// 撕裂/损坏的代次直接跳过，退回更早的有效代次。
    /// </summary>
    public (ResourceMap? Map, CacheError? Error) Load()
    {
        ResourceMap? best = null;
        CacheError? unsupported = null;

        foreach (var path in GenerationFile.Candidates(MapPath))
        {
            if (!File.Exists(path)) continue;

            var (map, error) = TryParse(path);
            if (error is not null)
            {
                unsupported ??= error;
                continue;
            }
            if (map is null) continue;
            if (best is null || map.Generation > best.Generation) best = map;
        }

        return best is not null ? (best, null) : (null, unsupported);
    }

    /// <summary>发布一次映射变更（代次由本方法加一），返回落盘后的映射。</summary>
    public ResourceMap Save(ResourceMap map)
    {
        var next = new ResourceMap { Generation = map.Generation + 1, SnapshotId = map.SnapshotId };
        foreach (var entry in map.Entries.Values) next.Put(entry);

        GenerationFile.Publish(MapPath, next.Serialize(), path => TryParse(path).Map is not null);
        return next;
    }

    private (ResourceMap? Map, CacheError? Error) TryParse(string path)
    {
        XDocument doc;
        try
        {
            doc = CacheFile.LoadSmallXml(path, CacheFile.MaxStateFileBytes);
        }
        catch
        {
            return (null, null);
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != ResourceMap.RootName) return (null, null);

        if (!int.TryParse(root.Attribute("SchemaVersion")?.Value, out var version)) return (null, null);
        if (version != ResourceMap.SchemaVersion)
            return (null, new CacheError(CacheErrorCodes.UnsupportedFormat,
                $"资源映射格式版本 {version} 不是本库认识的 {ResourceMap.SchemaVersion}（保留原文件，不覆盖）"));

        if (!int.TryParse(root.Attribute("Generation")?.Value, out var generation) || generation <= 0) return (null, null);
        if (!string.Equals(root.Attribute("SnapshotId")?.Value, _snapshotId, StringComparison.Ordinal)) return (null, null);

        var map = new ResourceMap { Generation = generation, SnapshotId = _snapshotId };

        foreach (var element in root.Elements("Resource"))
        {
            var registryId = element.Attribute("RegistryId")?.Value;
            var source = element.Attribute("Source")?.Value;
            var resolvedUrl = element.Attribute("ResolvedUrl")?.Value;
            if (string.IsNullOrWhiteSpace(registryId) || string.IsNullOrEmpty(source) || string.IsNullOrWhiteSpace(resolvedUrl))
                return (null, null);
            if (map.Find(registryId!) is not null) return (null, null); // 重复键：整份不可信

            var objectDigest = element.Attribute("ObjectDigest")?.Value;
            if (!string.IsNullOrEmpty(objectDigest) && !CacheHash.IsLowerHex(objectDigest, CacheHash.Sha256HexLength))
                return (null, null);

            if (!long.TryParse(element.Attribute("ObjectSize")?.Value, out var objectSize)) return (null, null);

            map.Put(new ResourceEntry
            {
                RegistryId = registryId!,
                Source = source!,
                ResolvedUrl = resolvedUrl!,
                ExpectedHash = element.Attribute("ExpectedHash")?.Value,
                ExpectedHashAlgorithm = element.Attribute("ExpectedHashAlgorithm")?.Value,
                ExpectedSize = long.TryParse(element.Attribute("ExpectedSize")?.Value, out var expectedSize) ? expectedSize : null,
                ObjectDigest = objectDigest,
                ObjectSize = objectSize,
                Status = element.Attribute("Status")?.Value == ResourceEntry.Failed ? ResourceEntry.Failed : ResourceEntry.Ready,
                Binding = Enum.TryParse<ResourceBinding>(element.Attribute("Binding")?.Value, out var binding) ? binding : ResourceBinding.None,
                ETag = element.Attribute("ETag")?.Value,
                LastModified = element.Attribute("LastModified")?.Value,
                FetchedAtUtc = DateTimeOffset.TryParse(element.Attribute("FetchedAtUtc")?.Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetchedAt) ? fetchedAt : null,
                LastError = element.Attribute("LastError")?.Value,
            });
        }

        return (map, null);
    }
}
