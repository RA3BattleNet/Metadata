using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// XSD 硬校验。源树用 MetadataSchema.xsd，发布展平物用 MetadataPublishSchema.xsd。
/// </summary>
public static class SchemaValidator
{
    public const string SourceSchemaFileName = "MetadataSchema.xsd";
    public const string PublishSchemaFileName = "MetadataPublishSchema.xsd";

    /// <summary>
    /// 校验单个 XML 文件；返回错误列表（空表示通过）。
    /// </summary>
    public static IReadOnlyList<string> ValidateFile(string xmlPath, string schemaPath)
    {
        if (!File.Exists(xmlPath))
            return [$"找不到 XML: {xmlPath}"];
        if (!File.Exists(schemaPath))
            return [$"找不到 XSD: {schemaPath}"];

        var errors = new List<string>();
        try
        {
            var schemas = new XmlSchemaSet();
            using (var schemaReader = XmlReader.Create(schemaPath))
                schemas.Add(null, schemaReader);
            schemas.Compile();

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                ValidationType = ValidationType.Schema,
                Schemas = schemas,
                IgnoreWhitespace = true
            };
            settings.ValidationEventHandler += (_, e) =>
            {
                errors.Add($"{Path.GetFileName(xmlPath)}: {e.Message}");
            };

            using var reader = XmlReader.Create(xmlPath, settings);
            while (reader.Read()) { }
        }
        catch (Exception ex)
        {
            errors.Add($"{Path.GetFileName(xmlPath)}: Schema 校验异常: {ex.Message}");
        }

        return errors;
    }

    /// <summary>
    /// 校验目录下全部 .xml（跳过 .xsd）。
    /// </summary>
    public static IReadOnlyList<string> ValidateDirectory(string directory, string schemaPath)
    {
        var errors = new List<string>();
        foreach (var xml in Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories))
        {
            errors.AddRange(ValidateFile(xml, schemaPath));
        }
        return errors;
    }

    /// <summary>
    /// 在源目录或输出目录中查找 schema 文件。
    /// </summary>
    public static string? FindSchema(string startDir, string fileName)
    {
        var dir = Path.GetFullPath(startDir);
        while (true)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;

            var nested = Path.Combine(dir, "Metadata", fileName);
            if (File.Exists(nested))
                return nested;

            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir)
                break;
            dir = parent;
        }

        var cwd = Path.Combine(Environment.CurrentDirectory, "Metadata", fileName);
        return File.Exists(cwd) ? cwd : null;
    }

    /// <summary>
    /// 有错误则抛 InvalidOperationException。
    /// </summary>
    public static void EnsureValid(string xmlPath, string schemaPath, string context)
    {
        var errors = ValidateFile(xmlPath, schemaPath);
        if (errors.Count > 0)
            throw new InvalidOperationException($"{context} XSD 校验失败:\n- " + string.Join("\n- ", errors));
    }

    public static void EnsureDirectoryValid(string directory, string schemaPath, string context)
    {
        var errors = ValidateDirectory(directory, schemaPath);
        if (errors.Count > 0)
            throw new InvalidOperationException($"{context} XSD 校验失败:\n- " + string.Join("\n- ", errors));
    }
}
