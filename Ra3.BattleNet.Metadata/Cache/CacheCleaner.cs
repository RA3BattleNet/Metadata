namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>缓存回收策略。宿主决定什么时候清、留多少；库只负责"按引用关系安全地清"。</summary>
public sealed class CacheCleanupOptions
{
    /// <summary>
    /// 允许缓存占用的总字节上限；0 或负数表示不按容量裁剪，只回收没有任何保留快照引用的对象。
    /// 触到上限时按对象从大到小回收，且**只回收无引用的**。
    /// </summary>
    public long MaxTotalBytes { get; init; }

    /// <summary>除 current/previous 之外，额外按最近使用保留的快照数量。</summary>
    public int RetainSnapshots { get; init; }

    /// <summary>暂存目录里超过该年龄的残留才算垃圾（正在写的不能碰）。</summary>
    public TimeSpan StagingRetention { get; init; } = TimeSpan.FromDays(1);

    /// <summary>只算出该回收什么，不真删。</summary>
    public bool DryRun { get; init; }
}

/// <summary>一次回收的结果：删了什么、留了什么、为什么没清干净。</summary>
public sealed class CacheCleanupReport
{
    private readonly List<string> _removedSnapshots = [];
    private readonly List<string> _removedObjects = [];
    private readonly List<string> _removedStaging = [];
    private readonly List<string> _preserved = [];

    /// <summary>回收前的缓存占用字节数。</summary>
    public long BytesBefore { get; internal set; }

    /// <summary>回收后的缓存占用字节数（<see cref="CacheCleanupOptions.DryRun"/> 时为预计值）。</summary>
    public long BytesAfter { get; internal set; }

    /// <summary>被回收的快照 ID。</summary>
    public IReadOnlyList<string> RemovedSnapshots => _removedSnapshots;

    /// <summary>被回收的对象摘要。</summary>
    public IReadOnlyList<string> RemovedObjects => _removedObjects;

    /// <summary>被回收的暂存目录名。</summary>
    public IReadOnlyList<string> RemovedStaging => _removedStaging;

    /// <summary>因为被引用或正被读取而保留的条目。</summary>
    public IReadOnlyList<string> Preserved => _preserved;

    /// <summary>没能按策略完成时的原因（例如仍超上限、格式不认识）。</summary>
    public CacheError? Error { get; internal set; }

    internal List<string> RemovedSnapshotList => _removedSnapshots;

    internal List<string> RemovedObjectList => _removedObjects;

    internal List<string> RemovedStagingList => _removedStaging;

    internal List<string> PreservedList => _preserved;
}

/// <summary>
/// 缓存回收。
///
/// 三条不可越过的规则：
/// 一是**没有有效指针就什么都不删**（否则会先破坏唯一可用缓存）；
/// 二是只回收没有任何保留快照引用、也没有活动租约的对象；
/// 三是容量清不干净就报 <see cref="CacheErrorCodes.CacheFull"/>，而不是继续动被引用的东西。
/// </summary>
internal static class CacheCleaner
{
    public static CacheCleanupReport Run(
        CacheLayout layout, CacheCleanupOptions options, IReadOnlyCollection<SnapshotLease> leases, Action<string>? log)
    {
        var report = new CacheCleanupReport();
        if (!Directory.Exists(layout.Root)) return report;

        var liveLeases = leases.Where(l => !l.IsDisposed).ToArray();
        var leasedSnapshots = new HashSet<string>(liveLeases.Select(l => l.Snapshot.SnapshotId), StringComparer.Ordinal);
        var leasedObjects = new HashSet<string>(liveLeases.SelectMany(l => l.ReferencedObjects), StringComparer.Ordinal);

        // DryRun 时只登记"该回收什么"，一个字节都不动
        bool Remove(string parent, string target) => options.DryRun || TryDeleteDirectory(parent, target, log);

        var pointers = CachePointerFile.ReadAll(layout);
        if (pointers.Count == 0)
        {
            report.Error = new CacheError(CacheErrorCodes.NoCache, "没有有效的当前指针，本轮不做任何回收");
            Measure(layout, report);
            return report;
        }

        var keepSnapshots = new HashSet<string>(pointers.Select(p => p.SnapshotId), StringComparer.Ordinal);
        var snapshotIds = DigestDirectories(layout.SnapshotsRoot);

        if (options.RetainSnapshots > 0)
        {
            foreach (var id in snapshotIds
                         .OrderByDescending(id => Directory.GetLastWriteTimeUtc(layout.SnapshotRoot(id)))
                         .Take(options.RetainSnapshots))
                keepSnapshots.Add(id);
        }

        // 快照：保留 current/previous（以及被租约使用的），其余回收
        foreach (var id in snapshotIds)
        {
            if (keepSnapshots.Contains(id) || leasedSnapshots.Contains(id))
            {
                report.PreservedList.Add($"snapshot:{id}");
                continue;
            }

            if (!Remove(layout.SnapshotsRoot, layout.SnapshotRoot(id))) continue;
            report.RemovedSnapshotList.Add(id);
        }

        // 对象：只有"没有任何保留快照引用、也没有租约引用"的才是回收候选
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var referencedSourceUnreadable = false;
        foreach (var id in keepSnapshots.Concat(leasedSnapshots))
        {
            var (map, error) = new ResourceMapStore(layout, id).Load();
            if (error is not null)
            {
                report.Error = error;
                referencedSourceUnreadable = true;
                break;
            }
            if (map is null) continue;
            foreach (var entry in map.Entries.Values)
            {
                if (entry.ObjectDigest is { } digest) referenced.Add(digest);
            }
        }

        if (referencedSourceUnreadable)
        {
            log?.Invoke("资源映射格式不认识：本轮不回收任何对象，保留现场");
            Measure(layout, report);
            return report;
        }

        var objectSizes = DigestDirectories(layout.ObjectsRoot)
            .ToDictionary(id => id, id => DirectorySize(Path.GetDirectoryName(layout.ObjectPath(id))!), StringComparer.Ordinal);

        var candidates = objectSizes.Keys
            .Where(id => !referenced.Contains(id) && !leasedObjects.Contains(id))
            .OrderByDescending(id => objectSizes[id])
            .ToList();

        foreach (var id in objectSizes.Keys.Where(id => !candidates.Contains(id, StringComparer.Ordinal)))
            report.PreservedList.Add($"object:{id}");

        var remaining = objectSizes.Values.Sum();
        foreach (var id in candidates)
        {
            if (options.MaxTotalBytes > 0 && remaining <= options.MaxTotalBytes) break;

            if (!Remove(layout.ObjectsRoot, Path.GetDirectoryName(layout.ObjectPath(id))!)) continue;
            report.RemovedObjectList.Add(id);
            remaining -= objectSizes[id];
        }

        // 暂存残留：只清我们自己的命名格式、且超过保留期的
        foreach (var directory in SafeDirectories(layout.StagingRoot))
        {
            var name = Path.GetFileName(directory);
            if (!CacheHash.IsLowerHex(name, CacheHash.Sha256HexLength)) continue;
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < options.StagingRetention) continue;
            if (Remove(layout.StagingRoot, directory)) report.RemovedStagingList.Add(name);
        }

        Measure(layout, report);
        if (options.MaxTotalBytes > 0 && report.BytesAfter > options.MaxTotalBytes)
        {
            report.Error = new CacheError(CacheErrorCodes.CacheFull,
                $"回收后仍超出上限（{report.BytesAfter} > {options.MaxTotalBytes}）：被引用的对象一律保留");
        }

        return report;
    }

    private static void Measure(CacheLayout layout, CacheCleanupReport report)
    {
        long before = 0, removedBytes = 0;
        foreach (var id in DigestDirectories(layout.ObjectsRoot))
        {
            var size = DirectorySize(Path.GetDirectoryName(layout.ObjectPath(id))!);
            before += size;
            if (report.RemovedObjectList.Contains(id, StringComparer.Ordinal)) removedBytes += size;
        }

        // DryRun 时字节数按"预计结果"给，报告才反映策略而不是现状
        report.BytesBefore = before;
        report.BytesAfter = before - removedBytes;
    }

    /// <summary>只认我们自己的目录命名（定长摘要），别的东西一律不碰。</summary>
    private static IReadOnlyList<string> DigestDirectories(string parent)
    {
        if (!Directory.Exists(parent)) return [];
        return SafeDirectories(parent)
            .Select(Path.GetFileName)
            .Where(name => CacheHash.IsLowerHex(name, CacheHash.Sha256HexLength))
            .Select(name => name!)
            .ToArray();
    }

    private static IReadOnlyList<string> SafeDirectories(string parent)
    {
        try { return Directory.Exists(parent) ? Directory.GetDirectories(parent) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>删之前确认目标确实是该父目录的直接子目录，避免任何形式的越界递归删除。</summary>
    private static bool TryDeleteDirectory(string parent, string target, Action<string>? log)
    {
        try
        {
            var fullParent = Path.GetFullPath(parent);
            var fullTarget = Path.GetFullPath(target);
            if (!string.Equals(Path.GetDirectoryName(fullTarget), fullParent, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"拒绝删除不在 {fullParent} 之下的目录：{fullTarget}");
                return false;
            }

            if (Directory.Exists(fullTarget)) Directory.Delete(fullTarget, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"回收 {target} 失败：{ex.Message}");
            return false;
        }
    }
}
