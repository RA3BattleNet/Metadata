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

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationFlags = XmlSchemaValidationFlags.None,
            IgnoreWhitespace = true
        };

        try
        {
            using var reader = XmlReader.Create(fullPath, settings);
            var doc = XDocument.Load(reader);

            if (doc.Root == null || doc.Root.Name.LocalName != "Metadata")
                throw new XmlException("无效的 XML 结构: 缺少根节点或根节点名称不是 'Metadata'");

            var metadata = new Metadata();
            metadata.ParseElement(doc.Root);
            return metadata;
        }
        catch (XmlException ex)
        {
            throw new XmlException($"XML 解析失败: {fullPath}", ex);
        }
    }
}
