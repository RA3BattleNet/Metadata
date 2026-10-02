namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 把登记节点的 <c>Source</c> 解析成"该去哪儿取"的地址。
///
/// 关键点只有一个：**一律按原远端入口解析**，绝不用缓存目录当远端基准。
/// 缓存根是本地布局，把它当基准会让 <c>../</c>、盘符和大小写碰撞都变成"看起来合法"的本地路径。
/// </summary>
internal static class CacheResourceResolver
{
    /// <summary>
    /// 解析并做安全边界检查。失败的三种情形在这里就分好了：
    /// 没有可用 Source、协议不支持、解析后跑出入口目录。
    /// </summary>
    public static bool TryResolve(Uri entryUri, string? source, out Uri? resolved, out CacheError? error)
    {
        resolved = null;
        error = null;

        var raw = source?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            error = new CacheError(CacheErrorCodes.InvalidSource, "登记节点没有可用的 Source");
            return false;
        }

        if (raw.IndexOfAny(['\0', '\r', '\n', '\t']) >= 0)
        {
            error = new CacheError(CacheErrorCodes.InvalidSource, "Source 含控制字符");
            return false;
        }

        return entryUri.IsFile
            ? TryResolveLocal(entryUri, raw, out resolved, out error)
            : TryResolveRemote(entryUri, raw, out resolved, out error);
    }

    /// <summary>远端入口：绝对 http(s) 来源放行，相对来源必须落在入口目录下。</summary>
    private static bool TryResolveRemote(Uri entryUri, string raw, out Uri? resolved, out CacheError? error)
    {
        resolved = null;
        error = null;

        var baseUri = new Uri(entryUri, ".");

        // "C:\x" 与 "\\server\share\x" 在 URL 语境里没有意义：先按本机路径判掉，再谈协议。
        // "/x" 是主机根路径，属于正常写法，留给下面的相对解析。
        if (Path.IsPathRooted(raw) && raw[0] != '/')
        {
            error = new CacheError(CacheErrorCodes.PathEscape, $"Source 是绝对路径: {raw}");
            return false;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            switch (absolute.Scheme)
            {
                case "http":
                case "https":
                    // 显式登记的远端来源：允许（跨域的显式来源是发布者的选择，不是路径逃逸）
                    resolved = absolute;
                    return true;
                case "file":
                    error = new CacheError(CacheErrorCodes.UnsupportedScheme, "远端文档不能指向本机文件");
                    return false;
                default:
                    error = new CacheError(CacheErrorCodes.UnsupportedScheme, $"不支持的来源协议: {absolute.Scheme}");
                    return false;
            }
        }

        // 相对 Source：只允许落在入口目录下
        Uri candidate;
        try
        {
            candidate = new Uri(baseUri, raw.Replace('\\', '/'));
        }
        catch (UriFormatException ex)
        {
            error = new CacheError(CacheErrorCodes.InvalidSource, $"Source 无法解析为地址：{ex.Message}");
            return false;
        }

        if (!string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != baseUri.Port)
        {
            error = new CacheError(CacheErrorCodes.PathEscape, "相对 Source 解析后跑到了另一个来源");
            return false;
        }

        if (!candidate.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            error = new CacheError(CacheErrorCodes.PathEscape, "相对 Source 解析后跑出了入口目录");
            return false;
        }

        resolved = candidate;
        return true;
    }

    /// <summary>本机开发入口（<c>file://</c>）：相对与绝对都只允许落在入口目录内。</summary>
    private static bool TryResolveLocal(Uri entryUri, string raw, out Uri? resolved, out CacheError? error)
    {
        resolved = null;
        error = null;

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(entryUri.LocalPath));
        if (string.IsNullOrEmpty(baseDir))
        {
            error = new CacheError(CacheErrorCodes.InvalidSource, "本地入口没有可用的父目录");
            return false;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            resolved = absolute;
            return true;
        }

        var local = absolute is { IsFile: true }
            ? absolute.LocalPath
            : raw.Replace('\\', '/');

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(local) ? local : Path.Combine(baseDir, local));
        }
        catch (Exception ex)
        {
            error = new CacheError(CacheErrorCodes.InvalidSource, $"Source 无法解析为本机路径：{ex.Message}");
            return false;
        }

        if (!IsInsideDirectory(baseDir, full))
        {
            error = new CacheError(CacheErrorCodes.PathEscape, "本地 Source 跑出了入口目录");
            return false;
        }

        resolved = new Uri(full);
        return true;
    }

    /// <summary>按目录边界（带分隔符）判断包含关系，避免 <c>Root</c> 与 <c>RootOther</c> 的裸前缀误判。</summary>
    private static bool IsInsideDirectory(string directory, string fullPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
