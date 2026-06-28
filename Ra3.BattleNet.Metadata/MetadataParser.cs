using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 负责 XML 元数据的解析、验证和 Include 展开。
/// </summary>
public static class MetadataParser
{
    /// <summary>
    /// 从文件加载并验证 XML 元数据。
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

        var doc = LoadXDocument(fullPath);
        var metadata = new Metadata { _currentFilePath = fullPath };
        var loadingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullPath };
        metadata.ParseElement(doc.Root, fullPath, loadingPaths);
        return metadata;
    }

    /// <summary>
    /// 从文件加载并验证 XML 元数据，同时使用 XSD Schema 验证。
    /// </summary>
    public static Metadata LoadFromFileWithSchema(string path, string? schemaPath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("文件路径不能为空", nameof(path));

        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(Environment.CurrentDirectory, path);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"找不到文件: {fullPath}");

        // 尝试查找 XSD
        schemaPath ??= FindSchemaFile(fullPath);

        XDocument doc;
        if (schemaPath != null && File.Exists(schemaPath))
        {
            doc = LoadXDocumentWithSchema(fullPath, schemaPath);
        }
        else
        {
            doc = LoadXDocument(fullPath);
        }

        var metadata = new Metadata { _currentFilePath = fullPath };
        var loadingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullPath };
        metadata.ParseElement(doc.Root, fullPath, loadingPaths);
        return metadata;
    }

    /// <summary>
    /// 验证 XML 文件是否符合 XSD Schema。
    /// </summary>
    public static List<string> ValidateWithSchema(string xmlPath, string? schemaPath = null)
    {
        var errors = new List<string>();

        schemaPath ??= FindSchemaFile(xmlPath);
        if (schemaPath == null || !File.Exists(schemaPath))
        {
            errors.Add($"找不到 XSD Schema 文件");
            return errors;
        }

        try
        {
            var schemas = new XmlSchemaSet();
            using var schemaReader = XmlReader.Create(schemaPath);
            schemas.Add(null, schemaReader);

            var doc = XDocument.Load(xmlPath);
            doc.Validate(schemas, (_, e) =>
            {
                errors.Add(e.Message);
            });
        }
        catch (Exception ex)
        {
            errors.Add($"Schema 验证失败: {ex.Message}");
        }

        return errors;
    }

    /// <summary>
    /// 验证所有 XML 文件是否符合 XSD Schema。
    /// </summary>
    public static Dictionary<string, List<string>> ValidateAllXmlFiles(string directory, string? schemaPath = null)
    {
        var results = new Dictionary<string, List<string>>();
        var xmlFiles = Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories);

        foreach (var xmlFile in xmlFiles)
        {
            var errors = ValidateWithSchema(xmlFile, schemaPath);
            if (errors.Count > 0)
            {
                results[xmlFile] = errors;
            }
        }

        return results;
    }

    /// <summary>
    /// 内部加载方法，支持循环检测（供 Metadata.ParseElement 调用）。
    /// </summary>
    internal static Metadata LoadFromFileInternal(string fullPath, HashSet<string>? loadingPaths)
    {
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"找不到文件: {fullPath}");

        var doc = LoadXDocument(fullPath);
        var metadata = new Metadata { _currentFilePath = fullPath };

        var newLoadingPaths = loadingPaths != null
            ? new HashSet<string>(loadingPaths, StringComparer.OrdinalIgnoreCase) { fullPath }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullPath };

        metadata.ParseElement(doc.Root, fullPath, newLoadingPaths);
        return metadata;
    }

    private static XDocument LoadXDocument(string fullPath)
    {
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

            if (doc.Root == null || doc.Root.Name != "Metadata")
                throw new XmlException("无效的 XML 结构: 缺少根节点或根节点名称不是 'Metadata'");

            return doc;
        }
        catch (XmlException ex)
        {
            throw new XmlException($"XML 解析失败: {fullPath}", ex);
        }
    }

    private static XDocument LoadXDocumentWithSchema(string fullPath, string schemaPath)
    {
        var schemas = new XmlSchemaSet();
        using var schemaReader = XmlReader.Create(schemaPath);
        schemas.Add(null, schemaReader);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            Schemas = schemas,
            IgnoreWhitespace = true
        };

        var validationErrors = new List<string>();
        settings.ValidationEventHandler += (_, e) => validationErrors.Add(e.Message);

        try
        {
            using var reader = XmlReader.Create(fullPath, settings);
            var doc = XDocument.Load(reader);

            if (doc.Root == null || doc.Root.Name != "Metadata")
                throw new XmlException("无效的 XML 结构: 缺少根节点或根节点名称不是 'Metadata'");

            if (validationErrors.Count > 0)
            {
                Console.WriteLine($"  Schema 验证警告 ({Path.GetFileName(fullPath)}):");
                foreach (var err in validationErrors)
                    Console.WriteLine($"    - {err}");
            }

            return doc;
        }
        catch (XmlException ex)
        {
            throw new XmlException($"XML 解析失败: {fullPath}", ex);
        }
    }

    private static string? FindSchemaFile(string xmlPath)
    {
        // 在 XML 同级目录查找
        var dir = Path.GetDirectoryName(xmlPath);
        while (dir != null)
        {
            var schemaPath = Path.Combine(dir, "MetadataSchema.xsd");
            if (File.Exists(schemaPath))
                return schemaPath;

            // 向上查找
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        // 在项目根目录查找
        var rootSchema = Path.Combine(Environment.CurrentDirectory, "Metadata", "MetadataSchema.xsd");
        if (File.Exists(rootSchema))
            return rootSchema;

        return null;
    }
}
