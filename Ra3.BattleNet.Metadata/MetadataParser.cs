using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 展平 metadata.xml 的纯反序列化：不展开 Include、不做构建期处理。
/// 构建期（展平/继承/变量/XSD）见 MetadataFlattener / MetadataInheritance / VariableResolver / SchemaValidator。
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

    /// <summary>
    /// 从已展平的 metadata.xml 加载元数据树。
    /// </summary>
    public static Metadata LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("文件路径不能为空", nameof(path));

        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(Environment.CurrentDirectory, path);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"找不到文件: {fullPath}");

        try
        {
            using var stream = File.OpenRead(fullPath);
            return Load(stream);
        }
        catch (XmlException ex)
        {
            throw new XmlException($"XML 解析失败: {fullPath}", ex);
        }
    }

    /// <summary>解析已读入的正文。DTD 禁止、不解析外部实体，Include 仍由节点解析拒绝。</summary>
    internal static Metadata Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, ReaderSettings);
        var doc = XDocument.Load(reader);
        return Load(doc);
    }

    /// <summary>
    /// 从内存中的 XML 文档解析元数据树。根必须是 <c>Metadata</c>；
    /// 与文件入口一样拒绝 Include/Includes/Module，只接受已展平的文档。
    /// 调用方可以先把内存节点包进新的 <c>Metadata</c> 根再解析，不需要写临时文件。
    /// </summary>
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
}
