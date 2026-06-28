using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 处理 XML 元数据中的变量替换（${TIMESTAMP}、${ENV:}、${MD5:}、${META:}、${this:}）。
/// </summary>
public partial class VariableResolver
{
    private readonly Dictionary<string, string> _envVars = new();
    private readonly Dictionary<string, string> _fileHashes = new();

    public VariableResolver()
    {
        foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
        {
            _envVars[env.Key.ToString()!] = env.Value?.ToString() ?? "";
        }
    }

    /// <summary>
    /// 递归替换 XML 文件中的变量，原地修改文件。
    /// </summary>
    public void ReplaceInFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        ReplaceInFileRecursive(fullPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private void ReplaceInFileRecursive(string filePath, HashSet<string> processingPaths)
    {
        if (!processingPaths.Add(filePath))
            throw new InvalidOperationException($"检测到循环引用: {filePath}");

        var doc = XDocument.Load(filePath);
        var root = doc.Root ?? throw new InvalidOperationException("无效的 XML 结构: 缺少根节点");
        var basePath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException($"无法确定文件目录: {filePath}");

        // 验证所有 Include/Module 引用的资源存在
        ValidateResources(root, basePath);

        // 递归处理所有引用的文件
        foreach (var include in root.Descendants().Where(e => e.Name.LocalName is "Include" or "Module"))
        {
            var path = include.Attribute("Path")?.Value ?? include.Attribute("Source")?.Value;
            if (string.IsNullOrWhiteSpace(path)) continue;

            var referencedFile = Path.GetFullPath(Path.Combine(basePath, path.Replace('\\', '/')));
            if (File.Exists(referencedFile))
                ReplaceInFileRecursive(referencedFile, processingPaths);
        }

        // 替换当前文件中的变量
        ReplaceInElement(root, filePath);
        doc.Save(filePath);
    }

    /// <summary>
    /// 递归替换 XML 元素中的变量。
    /// </summary>
    public void ReplaceInElement(XElement element, string currentFilePath)
    {
        foreach (var attr in element.Attributes())
        {
            attr.Value = Resolve(attr.Value, element, currentFilePath);
        }

        if (!element.HasElements && !string.IsNullOrEmpty(element.Value))
        {
            element.Value = Resolve(element.Value, element, currentFilePath);
        }

        foreach (var child in element.Elements())
        {
            ReplaceInElement(child, currentFilePath);
        }
    }

    /// <summary>
    /// 解析单个变量表达式。
    /// </summary>
    public string Resolve(string input, XElement context, string currentFilePath)
    {
        return VariablePattern().Replace(input, match =>
        {
            var expr = match.Groups[1].Value;
            var parts = expr.Split(':');
            if (parts.Length < 1) return match.Value;

            return parts[0] switch
            {
                "TIMESTAMP" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                "ENV" => parts.Length > 1 && _envVars.TryGetValue(parts[1], out var envVal)
                    ? envVal
                    : match.Value,
                "MD5" => ResolveMd5(parts, currentFilePath),
                "META" => ResolveMeta(parts, context),
                "this" => ResolveThis(parts, context),
                _ => match.Value
            };
        });
    }

    private string ResolveMd5(string[] parts, string currentFilePath)
    {
        var fileToHash = parts.Length > 1 ? parts[1] : "";
        if (string.IsNullOrEmpty(fileToHash))
            return ComputeFileHash(currentFilePath);

        var dir = Path.GetDirectoryName(currentFilePath)
            ?? throw new InvalidOperationException($"无法确定文件目录: {currentFilePath}");

        return ComputeFileHash(Path.Combine(dir, fileToHash));
    }

    private string ResolveMeta(string[] parts, XElement context)
    {
        if (parts.Length < 2) return string.Empty;

        var metaPath = string.Join(":", parts.Skip(1));
        var target = FindMetaReference(context.Document?.Root, metaPath);
        return target?.Value ?? throw new InvalidOperationException($"META 引用未找到: {metaPath}");
    }

    private string ResolveThis(string[] parts, XElement context)
    {
        if (parts.Length < 2) return string.Empty;

        var container = FindNearestContainer(context)
            ?? throw new InvalidOperationException($"this: 引用未找到容器: {parts[1]}");

        return FindInDefines(container, parts[1])
            ?? throw new InvalidOperationException($"this: 引用未找到: {parts[1]}");
    }

    /// <summary>
    /// 查找最近的包含 Defines 的容器元素（向上遍历到 Application、Mod、Module 等）。
    /// </summary>
    private static XElement? FindNearestContainer(XElement element)
    {
        var current = element;
        while (current != null)
        {
            // 这些元素类型都可以包含 Defines
            if (current.Name.LocalName is "Application" or "Mod" or "Module")
                return current;

            // 如果当前元素本身包含 Defines 子元素，也视为容器
            if (current.Element("Defines") != null)
                return current;

            current = current.Parent;
        }
        return null;
    }

    private static string? FindInDefines(XElement container, string key)
    {
        var defines = container.Element("Defines");
        if (defines == null) return null;

        var define = defines.Elements().FirstOrDefault(e => e.Name.LocalName == key);
        return define?.Value;
    }

    private static XElement? FindMetaReference(XElement? root, string path)
    {
        if (root == null) return null;

        var parts = path.Split(':');
        XElement? current = root;

        foreach (var part in parts)
        {
            current = current.Elements().FirstOrDefault(e => e.Name.LocalName == part);
            if (current == null) return null;
        }

        return current;
    }

    private string ComputeFileHash(string filePath)
    {
        if (_fileHashes.TryGetValue(filePath, out var hash))
            return hash;

        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = md5.ComputeHash(stream);
        hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        _fileHashes[filePath] = hash;
        return hash;
    }

    private static void ValidateResources(XElement element, string basePath)
    {
        foreach (var include in element.Elements("Include").Concat(element.Elements("Module")))
        {
            var path = include.Attribute("Path")?.Value ?? include.Attribute("Source")?.Value;
            if (string.IsNullOrEmpty(path)) continue;

            var fullPath = Path.Combine(basePath, path.Replace('\\', '/'));
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"引用的资源文件不存在: {fullPath} (来自元素: {include.Name.LocalName})");
        }

        foreach (var child in element.Elements())
        {
            ValidateResources(child, basePath);
        }
    }

    [GeneratedRegex(@"\$\{(.*?)\}")]
    private static partial Regex VariablePattern();
}
