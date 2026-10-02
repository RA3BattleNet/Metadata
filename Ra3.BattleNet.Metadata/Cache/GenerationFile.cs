namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 单文件状态的崩溃安全发布：写 <c>.pending</c> → 回读验证 → <c>current</c> 转 <c>.previous</c>
/// → <c>.pending</c> 转 <c>current</c>。
///
/// 读取方不假设哪一份是好的：在三份里挑**最高有效代次**，有效性由各自的文档类型定义。
/// 同目录改名是同卷操作，所以崩溃最多留下"多一份旧材料"，不会留下半截文件。
/// </summary>
internal static class GenerationFile
{
    public static string PendingPath(string currentPath) => currentPath + ".pending";

    public static string PreviousPath(string currentPath) => currentPath + ".previous";

    /// <summary>按"当前 → 上一版 → 待发布"的顺序给出候选路径。</summary>
    public static IEnumerable<string> Candidates(string currentPath)
    {
        yield return currentPath;
        yield return PreviousPath(currentPath);
        yield return PendingPath(currentPath);
    }

    /// <summary>
    /// 发布一份新状态。<paramref name="validates"/> 必须能独立判断"这个文件是不是一份可用状态"——
    /// 回读验证不通过就抛，绝不动 current。
    /// </summary>
    public static void Publish(string currentPath, byte[] payload, Func<string, bool> validates)
    {
        var directory = Path.GetDirectoryName(currentPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var pending = PendingPath(currentPath);
        CacheFile.WriteAtomic(pending, payload);

        if (!validates(pending))
            throw new InvalidDataException($"状态文件写入后回读验证失败，拒绝切换活动代次: {Path.GetFileName(currentPath)}");

        if (File.Exists(currentPath))
            File.Move(currentPath, PreviousPath(currentPath), overwrite: true);
        File.Move(pending, currentPath, overwrite: true);
    }
}
