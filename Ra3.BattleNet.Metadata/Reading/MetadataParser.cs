using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// XML 节点的纯模型投影；路径、组合与资源身份由 MetadataReader 解释。
/// </summary>
public static class MetadataParser
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        ValidationFlags = XmlSchemaValidationFlags.None,
        IgnoreWhitespace = true
    };

    /// <summary>解析 XML 正文，禁止 DTD 和外部实体。</summary>
    internal static Metadata Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, ReaderSettings);
        var doc = XDocument.Load(reader);
        return Load(doc);
    }

    /// <summary>将 Metadata 根文档投影为模型，不执行读取或组合处理。</summary>
    public static Metadata Load(XDocument document)
    {
        if (document == null)
            throw new ArgumentNullException(nameof(document));

        if (document.Root == null || document.Root.Name.LocalName != "Metadata")
            throw new XmlException("无效的 XML 结构: 缺少根节点或根节点名称不是 'Metadata'");

        var metadata = new Metadata();
        metadata.ParseElement(document.Root);
        return metadata;
    }

    /// <summary>
    /// 解析一个独立的 <c>Manifest</c> 节点。清单是可以单独流转的一等载体
    /// （安装记录里的冻结快照、单独读取的叶子文件），不需要先套一层假的 <c>Metadata</c> 根。
    /// </summary>
    /// <param name="manifest">清单节点，必须是 <c>Manifest</c>。</param>
    public static ManifestEntry ParseManifest(XElement manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Name.LocalName != "Manifest")
            throw new ArgumentException("清单根节点必须是 Manifest", nameof(manifest));

        var node = new Metadata();
        node.ParseElement(manifest);
        return node.ToManifestEntry();
    }

    /// <summary>
    /// 解析清单 XML 文本：既可以只是 <c>&lt;Manifest&gt;</c>，也可以是包含单个清单的文档。
    /// </summary>
    /// <param name="xml">清单 XML 文本。</param>
    public static ManifestEntry ParseManifest(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ArgumentException("清单 XML 不能为空", nameof(xml));

        var element = XElement.Parse(xml);
        var manifest = element.Name.LocalName == "Manifest"
            ? element
            : element.Descendants("Manifest").SingleOrDefault()
              ?? throw new ArgumentException("XML 里没有 Manifest 节点", nameof(xml));
        return ParseManifest(manifest);
    }
}
