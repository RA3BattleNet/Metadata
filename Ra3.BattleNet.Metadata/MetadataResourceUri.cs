namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 把登记节点的相对 <c>Source</c> 解析成发布物地址。
/// 发布物是静态文件树：资源地址 = 入口地址所在目录 + Source。
/// </summary>
public static class MetadataResourceUri
{
    /// <summary>
    /// 以 <c>metadata.xml</c> 的地址为基准解析相对 Source，反斜杠按斜杠处理。
    /// </summary>
    /// <param name="metadataXmlUrl">发布物入口地址，如 <c>https://metadata.ra3battle.net/metadata.xml</c>；也可以是本地文件路径。</param>
    /// <param name="relativeSource">登记节点的 Source，相对发布物根。</param>
    public static Uri Resolve(string metadataXmlUrl, string relativeSource)
    {
        if (string.IsNullOrWhiteSpace(metadataXmlUrl))
            throw new ArgumentException("入口地址不能为空", nameof(metadataXmlUrl));
        if (string.IsNullOrWhiteSpace(relativeSource))
            throw new ArgumentException("Source 不能为空", nameof(relativeSource));

        // 本地路径不是合法 Uri，先转成 file://；Path.IsPathRooted 对 file:/// 返回 false，故按前缀区分
        var entry = metadataXmlUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? new Uri(metadataXmlUrl, UriKind.Absolute)
            : Uri.TryCreate(metadataXmlUrl, UriKind.Absolute, out var absolute)
                && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
                ? absolute
                : new Uri(Path.GetFullPath(metadataXmlUrl));
        return new Uri(new Uri(entry, "."), relativeSource.Replace('\\', '/'));
    }
}
