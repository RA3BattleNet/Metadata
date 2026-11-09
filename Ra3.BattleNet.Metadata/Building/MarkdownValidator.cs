namespace Ra3.BattleNet.Metadata;

/// <summary>
/// Markdown 文件内容验证工具。
/// </summary>
public static class MarkdownValidator
{
    /// <summary>
    /// 验证结果。
    /// </summary>
    public sealed record ValidationResult(bool IsValid, List<string> Errors);

    /// <summary>
    /// 验证元数据中引用的所有 Markdown 文件。
    /// 由于 Source 路径是相对于引用它的 XML 文件的，而 Metadata 树不保留每个节点的来源文件，
    /// 此方法在 baseDir 下递归搜索所有 Markdown 文件。
    /// </summary>
    /// <param name="metadata">已加载的元数据根节点。</param>
    /// <param name="baseDir">Markdown 文件所在的基础目录。</param>
    /// <returns>验证结果，包含所有错误信息。</returns>
    public static ValidationResult ValidateAll(Metadata metadata, string baseDir)
    {
        var errors = new List<string>();
        var markdowns = metadata.GetAllElements("Markdown");

        foreach (var md in markdowns)
        {
            var id = md.Get("ID") ?? "<unknown>";
            var source = md.Get("Source");

            if (string.IsNullOrWhiteSpace(source))
            {
                errors.Add($"Markdown '{id}' 缺少 Source 属性");
                continue;
            }

            // 在 baseDir 下递归搜索文件
            var foundPath = FindFileRecursive(baseDir, source.Replace('\\', '/'));
            if (foundPath == null)
            {
                errors.Add($"Markdown 文件不存在: {source} (ID: {id})");
                continue;
            }

            var content = File.ReadAllText(foundPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                errors.Add($"Markdown 文件为空: {foundPath} (ID: {id})");
                continue;
            }

            // 检查是否有基本的内容结构
            if (content.Trim().Length < 10)
            {
                errors.Add($"Markdown 文件内容过短: {foundPath} (ID: {id})");
            }
        }

        return new ValidationResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// 在目录中递归搜索文件名。
    /// </summary>
    private static string? FindFileRecursive(string directory, string fileName)
    {
        // 先尝试直接拼接
        var directPath = Path.Combine(directory, fileName);
        if (File.Exists(directPath))
            return directPath;

        // 递归搜索
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories))
            {
                return file;
            }
        }
        catch
        {
            // 忽略搜索中的权限错误
        }

        return null;
    }
}
