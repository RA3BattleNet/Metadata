using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 清单节点与正文的公共规则：完整清单自带正文（不带 <c>Source</c>），
/// 订阅方按登记 ID 在候选节点里取唯一命中。
/// </summary>
internal static class MetadataManifestBody
{
    /// <summary>完整清单：带 ID 且没有 <c>Source</c>，正文就在本节点里。</summary>
    internal static bool IsFull(XElement element) => element.Name.LocalName == "Manifest"
        && element.Attribute("ID") != null
        && string.IsNullOrWhiteSpace(element.Attribute("Source")?.Value);

    /// <summary>
    /// 按业务 ID 取唯一命中，比较不区分大小写。没有命中或同一 ID 出现多次都返回 false，
    /// 具体报错文案由调用方决定。
    /// </summary>
    internal static bool TrySelect(IEnumerable<XElement> manifests, string id, out XElement? match)
    {
        match = null;
        foreach (var candidate in manifests)
        {
            if (!string.Equals(candidate.Attribute("ID")?.Value, id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null)
            {
                match = null;
                return false;
            }
            match = candidate;
        }
        return match is not null;
    }
}
