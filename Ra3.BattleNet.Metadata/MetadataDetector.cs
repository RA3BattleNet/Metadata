using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 已读入的 XML 是作者源树还是发布物：源树带 <c>Include</c>/<c>Includes</c>，发布物没有。
/// </summary>
public static class MetadataDetector
{
    /// <summary>
    /// 根元素之下（任意层级）是否存在 <c>Include</c> 或 <c>Includes</c> 元素。
    /// 只按元素局部名判断，不区分命名空间前缀；发布物解析器同样拒绝这两种元素。
    /// </summary>
    public static bool HasInclude(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.Root?.Descendants().Any(e => e.Name.LocalName is "Include" or "Includes") == true;
    }
}
