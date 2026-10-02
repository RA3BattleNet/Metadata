namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 缓存根内的固定布局。路径只有这一处真源，调用方不得自己拼 <c>cacheRoot + 远端 Source</c>。
///
/// <code>
/// current.xml / current.xml.pending / current.xml.previous
/// origin.xml
/// snapshots/&lt;root-digest&gt;/metadata.xml
/// snapshots/&lt;root-digest&gt;/resources.xml
/// objects/&lt;sha256&gt;/payload
/// requests/&lt;safe-request-key&gt;.xml
/// staging/&lt;attempt-id&gt;/...
/// </code>
/// </summary>
internal sealed class CacheLayout
{
    public CacheLayout(string cacheRoot)
    {
        Root = Path.GetFullPath(cacheRoot);
    }

    /// <summary>缓存根（绝对路径）。</summary>
    public string Root { get; }

    public string CurrentPointerPath => Path.Combine(Root, "current.xml");

    public string PendingPointerPath => Path.Combine(Root, "current.xml.pending");

    public string PreviousPointerPath => Path.Combine(Root, "current.xml.previous");

    /// <summary>来源绑定文件。一个缓存根只服务一个入口来源。</summary>
    public string OriginPath => Path.Combine(Root, "origin.xml");

    public string SnapshotsRoot => Path.Combine(Root, "snapshots");

    public string SnapshotRoot(string snapshotId) => Path.Combine(SnapshotsRoot, RequireDigest(snapshotId, nameof(snapshotId)));

    public string SnapshotMetadataPath(string snapshotId) => Path.Combine(SnapshotRoot(snapshotId), "metadata.xml");

    public string SnapshotResourcesPath(string snapshotId) => Path.Combine(SnapshotRoot(snapshotId), "resources.xml");

    public string ObjectsRoot => Path.Combine(Root, "objects");

    /// <summary>内容寻址对象：不可原地修改，同内容可复用普通文件读取。</summary>
    public string ObjectPath(string digest) => Path.Combine(ObjectsRoot, RequireDigest(digest, nameof(digest)), "payload");

    public string RequestsRoot => Path.Combine(Root, "requests");

    public string RequestPath(string requestKey) => Path.Combine(RequestsRoot, RequireDigest(requestKey, nameof(requestKey)) + ".xml");

    public string StagingRoot => Path.Combine(Root, "staging");

    public string StagingAttempt(string attemptId) => Path.Combine(StagingRoot, CacheHash.Sha256Hex(attemptId));

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SnapshotsRoot);
        Directory.CreateDirectory(ObjectsRoot);
        Directory.CreateDirectory(RequestsRoot);
        Directory.CreateDirectory(StagingRoot);
    }

    /// <summary>
    /// 目录名只接受固定长度的 SHA-256 十六进制：远端数据不能借摘要字段把路径带出缓存根。
    /// </summary>
    private static string RequireDigest(string value, string what)
    {
        if (!CacheHash.IsLowerHex(value, CacheHash.Sha256HexLength))
            throw new ArgumentException($"不是合法的 SHA-256 十六进制摘要: {what}", what);
        return value;
    }
}
