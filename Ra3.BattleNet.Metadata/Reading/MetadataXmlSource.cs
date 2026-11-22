namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 按入口形态选择默认只读源：URL 走 <see cref="HttpXmlSource"/>，其余按本地文件。
/// Reader 之外的调用方用 <see cref="IsDefault"/> 判断手里的源是不是这类默认实现。
/// </summary>
internal static class MetadataXmlSource
{
    /// <summary>入口的 URL 形式按所在目录作为 HTTP 源根，本地形式不带状态。</summary>
    internal static IMetadataXmlSource For(string pathOrUrl) => MetadataReader.IsHttp(pathOrUrl)
        ? new HttpXmlSource(new Uri(new Uri(pathOrUrl), "."))
        : new PhysicalFileXmlSource();

    /// <summary>是否 Reader 自建的默认源：导出复用捕获图时不必把它当注入源再传一遍。</summary>
    internal static bool IsDefault(IMetadataXmlSource source) =>
        source is PhysicalFileXmlSource or HttpXmlSource;
}
