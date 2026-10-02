namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 一个快照的读取租约。
///
/// 它存在的意义只有一条：**缓存清理不能动正在被读的东西**。
/// 持有租约期间，从它解析出来的对象都记在 <see cref="ReferencedObjects"/> 里；
/// 回收只看"有没有 current/previous 快照引用、有没有活动租约引用"，不按时间猜。
/// </summary>
public sealed class SnapshotLease : IDisposable
{
    private readonly HashSet<string> _objects = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal SnapshotLease(MetadataCache owner, CatalogSnapshot snapshot)
    {
        Owner = owner;
        Snapshot = snapshot;
        CacheRoot = snapshot.CacheRoot;
    }

    /// <summary>租约对应的快照。</summary>
    public CatalogSnapshot Snapshot { get; }

    /// <summary>所属缓存根。</summary>
    public string CacheRoot { get; }

    /// <summary>已经释放的租约不能再用来解析资源。</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>本租约使用过的对象摘要（清理要避开它们）。</summary>
    public IReadOnlyCollection<string> ReferencedObjects
    {
        get { lock (_gate) return _objects.ToArray(); }
    }

    internal MetadataCache Owner { get; }

    internal void Track(string objectDigest)
    {
        lock (_gate) _objects.Add(objectDigest);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Owner.ReleaseLease(this);
    }
}
