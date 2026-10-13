using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>允许的主 Mod，或附属之间的兼容例外；版本是警告范围，不是启动门禁。</summary>
public sealed record ModCompatibilityTarget(string ModId, bool Compatible, IReadOnlyList<string> Versions);

/// <summary>缺省不兼容主 Mod，附属之间缺省兼容；原版兼容由 IsAddon 决定。</summary>
public sealed record ModCompatibilityRules(IReadOnlyList<ModCompatibilityTarget> Main, IReadOnlyList<ModCompatibilityTarget> Addons);

public static class ModCompatibility
{
    public const string VanillaId = "vanilla";

    /// <summary>版本包有 Compatibility 时完整覆盖，不逐条与 Mod 默认合并。</summary>
    public static ModCompatibilityRules LaunchCompatibility(this ModEntry mod, string version)
    {
        var node = mod.Package(version)?.Raw.Find("Compatibility") ?? mod.Raw.Find("Compatibility");
        return new(Read(node, "Main"), Read(node, "Addon"));
    }

    private static IReadOnlyList<ModCompatibilityTarget> Read(Metadata? node, string name) =>
        node?.Children.Where(c => c.Name == name).Select(c => new ModCompatibilityTarget(
            c.Get("Mod")?.Trim() ?? "", c.Get("Compatible")?.Trim() is not ("false" or "0"),
            c.Children.Where(v => v.Name == "Version").Select(v => v.Value?.Trim() ?? "").ToArray())).ToArray() ?? [];

    /// <summary>构建期在 Include/继承完成后检查全局实体引用，不给 Mod ID 添加资源路径前缀。</summary>
    internal static void Validate(XElement root)
    {
        var mods = root.Elements("Mod").ToDictionary(m => m.Attribute("ID")!.Value, StringComparer.OrdinalIgnoreCase);
        if (mods.ContainsKey(VanillaId)) throw new InvalidOperationException("Mod ID vanilla is reserved");
        foreach (var mod in mods.Values)
        {
            foreach (var node in mod.Descendants("Compatibility"))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in node.Elements())
                {
                    var id = target.Attribute("Mod")?.Value.Trim() ?? "";
                    var key = target.Name.LocalName + ":" + id;
                    if (!seen.Add(key)) throw new InvalidOperationException($"Duplicate compatibility target: {key}");
                    if (!mods.TryGetValue(id, out var referenced) || string.Equals(id, mod.Attribute("ID")!.Value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Invalid compatibility Mod reference: {id}");
                    var compatible = target.Attribute("Compatible")?.Value.Trim() is not ("false" or "0");
                    if (target.Name == "Main" && !compatible)
                        throw new InvalidOperationException("Main lists allowed Mods only");
                    if (target.Name == "Addon" && referenced.Attribute("IsAddon")?.Value.Trim() is not ("true" or "1"))
                        throw new InvalidOperationException($"Compatibility Addon target is not an addon: {id}");
                    var versions = target.Elements("Version").Select(v => v.Value.Trim()).ToArray();
                    if (versions.Any(string.IsNullOrWhiteSpace) || versions.Distinct(StringComparer.Ordinal).Count() != versions.Length)
                        throw new InvalidOperationException($"Empty or duplicate compatibility Version: {id}");
                    if (!compatible && versions.Length > 0)
                        throw new InvalidOperationException("An incompatible Addon rule cannot have advisory Versions");
                }
            }
        }
        if (root.Elements("Application").Any(a => a.Descendants("Compatibility").Any()))
            throw new InvalidOperationException("Compatibility is only supported for Mod packages");
    }
}
