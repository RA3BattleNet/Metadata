namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 内容寻址对象库：<c>objects/&lt;sha256&gt;/payload</c>。
///
/// 对象**不可原地修改**：同名对象一旦写下就不再改写，因此命中判断只需要
/// "目录名是摘要 + 长度一致"；真正的字节校验发生在写入那一刻。
/// 缓存清理只处理没有快照引用、也没有活动租约的对象（见 <see cref="SnapshotLease"/>）。
/// </summary>
internal static class CacheObjectStore
{
    /// <summary>已有可用对象时给出它的路径与长度。</summary>
    public static bool TryGet(CacheLayout layout, string digest, long expectedSize, out string path)
    {
        path = layout.ObjectPath(digest);
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        if (expectedSize >= 0 && info.Length != expectedSize) return false;
        return true;
    }

    /// <summary>
    /// 把暂存文件登记成对象。同摘要已经存在时以盘上那份为准（内容寻址意味着两份字节相同），
    /// 只把暂存文件回收掉。
    /// </summary>
    public static string Commit(CacheLayout layout, string digest, string stagedPath)
    {
        var target = layout.ObjectPath(digest);
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        try
        {
            if (!File.Exists(target)) File.Move(stagedPath, target, overwrite: false);
        }
        catch (IOException)
        {
            // 另一个 writer 抢先登记了同一个对象：内容相同，用它就行
            if (!File.Exists(target)) throw;
        }

        try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
        catch (IOException) { /* 暂存文件回收失败只是留下垃圾，不影响对象可用性 */ }

        return target;
    }
}
