using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 把解析出来的 <see cref="Metadata"/> 节点还原成 XML，用于抄写清单原文或再交给
/// <see cref="MetadataParser"/> 解析。纯转换，不读写磁盘。
/// </summary>
public static class MetadataXml
{
    /// <summary>还原成 XML 文本。</summary>
    public static string Write(Metadata node) => new XElement(ToElement(node)).ToString();

    /// <summary>还原成 XML 节点。</summary>
    public static XElement ToElement(Metadata node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var element = new XElement(node.Name);
        foreach (var (key, value) in node.Variables)
            element.SetAttributeValue(key, value);

        if (node.Children.Count == 0)
        {
            if (node.Value is not null)
                element.Value = node.Value;
            return element;
        }

        foreach (var child in node.Children)
            element.Add(ToElement(child));
        return element;
    }
}
