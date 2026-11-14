using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 处理展平 XML 中的构建期变量替换（${TIMESTAMP}、${ENV:}）。
/// 只作用于单个展平文件，不递归 Include（展平产物无 Include）。
/// 不再提供 ${MD5} 系列宏：XML 里只有 Manifest 与 Updater 记录带哈希，都由各自的生成方给出字面值。
/// </summary>
public partial class VariableResolver
{
    private readonly Dictionary<string, string> _envVars = new();

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

        ReplaceInElement(root);
        doc.Save(filePath);
    }

    /// <summary>
    /// 递归替换 XML 元素中的变量。
    /// </summary>
    public void ReplaceInElement(XElement element)
    {
        foreach (var attr in element.Attributes())
        {
            attr.Value = Resolve(attr.Value);
        }

        if (!element.HasElements && !string.IsNullOrEmpty(element.Value))
        {
            element.Value = Resolve(element.Value);
        }

        foreach (var child in element.Elements())
        {
            ReplaceInElement(child);
        }
    }

    /// <summary>
    /// 解析单个变量表达式。
    /// </summary>
    public string Resolve(string input)
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
                _ => match.Value
            };
        });
    }

    [GeneratedRegex(@"\$\{(.*?)\}")]
    private static partial Regex VariablePattern();
}
