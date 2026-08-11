using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 处理展平 XML 中的构建期变量替换（${TIMESTAMP}、${ENV:}、${MD5:}）。
/// 只作用于单个展平文件，不递归 Include（展平产物无 Include）。
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
        var doc = XDocument.Load(filePath);
        var root = doc.Root ?? throw new InvalidOperationException("无效的 XML 结构: 缺少根节点");
        var basePath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException($"无法确定文件目录: {filePath}");

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
            // Markdown/Image 的 Hash=${MD5::} 按 Source 资源文件计算
            if (attr.Name.LocalName == "Hash"
                && attr.Value.Contains("${MD5::}", StringComparison.Ordinal)
                && element.Attribute("Source") is { Value: { Length: > 0 } source })
            {
                var dir = Path.GetDirectoryName(currentFilePath)
                    ?? throw new InvalidOperationException($"无法确定文件目录: {currentFilePath}");
                var resourcePath = Path.GetFullPath(Path.Combine(dir, source.Replace('\\', '/')));
                if (!File.Exists(resourcePath))
                    throw new InvalidOperationException($"MD5 目标资源不存在: {source}");
                attr.Value = attr.Value.Replace("${MD5::}", ComputeFileHash(resourcePath), StringComparison.Ordinal);
            }

            attr.Value = Resolve(attr.Value, currentFilePath);
        }

        if (!element.HasElements && !string.IsNullOrEmpty(element.Value))
        {
            element.Value = Resolve(element.Value, currentFilePath);
        }

        foreach (var child in element.Elements())
        {
            ReplaceInElement(child, currentFilePath);
        }
    }

    /// <summary>
    /// 解析单个变量表达式。
    /// </summary>
    public string Resolve(string input, string currentFilePath)
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

    [GeneratedRegex(@"\$\{(.*?)\}")]
    private static partial Regex VariablePattern();
}
