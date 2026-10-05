using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 通用模组设置契约：<c>Mod/Settings</c> 定义与 <c>Manifest/Settings</c> 绑定的解析、
/// 快照序列化/回读，以及所属 Mod 与叶子清单之间的跨节点硬校验。
/// 设置 ID 为 Mod 内局部 ID，所有引用节点一律用 <c>Ref</c> 且不参与全根资源 ID 前缀。
/// </summary>
public static class ModSettingsContract
{
    /// <summary>已支持的注入协议（注入目标必须声明其中之一）。</summary>
    public static readonly IReadOnlyList<string> KnownProtocols = ["lyi-create-process", "easyhook"];

    /// <summary>Desktop 已实现的 LuaBridge 协议适配器。</summary>
    public static readonly IReadOnlyList<string> KnownAdapters =
        ["audio-fix", "desync-debug", "debug-overlay", "enhancer-logger", "always-enable-engine-fix"];

    private const string LuaBridgeProtocol = "lyi-create-process";
    private const string LogFileRuntimeValue = "log-file";
    private const string Utf8Encoding = "utf8";

    /// <summary>
    /// 解析 Mod 级设置定义（节点为 <c>Mod/Settings</c> 或快照的 <c>Definitions</c>）。
    /// 子元素 <c>Boolean</c>/<c>Choice</c> 的声明顺序即定义顺序。
    /// </summary>
    public static IReadOnlyList<ModSettingDefinition> ParseDefinitions(Metadata node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var result = new List<ModSettingDefinition>();
        foreach (var child in node.Children)
        {
            var kind = child.Name switch
            {
                "Boolean" => (ModSettingKind?)ModSettingKind.Boolean,
                "Choice" => (ModSettingKind?)ModSettingKind.Choice,
                _ => (ModSettingKind?)null,
            };
            if (kind is null)
                throw new InvalidOperationException($"设置定义未知节点: {child.Name}");

            var id = child.Get("ID");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"设置定义 {child.Name} 缺少 ID");

            result.Add(new ModSettingDefinition(
                Id: id!,
                Kind: kind.Value,
                Default: child.Get("Default") ?? string.Empty,
                DisplayNames: ReadLocalized(child, "DisplayName"),
                Descriptions: ReadLocalized(child, "Description"),
                Options: kind.Value == ModSettingKind.Choice
                    ? child.Children.Where(c => c.Name == "Option")
                        .Select(option => new ModSettingOption(
                            Value: option.Get("Value") ?? string.Empty,
                            DisplayNames: ReadLocalized(option, "DisplayName"),
                            Descriptions: ReadLocalized(option, "Description")))
                        .ToList()
                    : []));
        }
        return result;
    }

    /// <summary>
    /// 解析版本清单设置绑定（节点为 <c>Manifest/Settings</c> 或快照的 <c>Bindings</c>）。
    /// <c>SettingRef</c> 的声明顺序即绑定顺序。
    /// </summary>
    public static IReadOnlyList<ModSettingBinding> ParseBindings(Metadata node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var result = new List<ModSettingBinding>();
        foreach (var child in node.Children)
        {
            if (child.Name != "SettingRef")
                throw new InvalidOperationException($"设置绑定未知节点: {child.Name}");

            var reference = child.Get("Ref");
            if (string.IsNullOrWhiteSpace(reference))
                throw new InvalidOperationException("SettingRef 缺少 Ref");

            result.Add(new ModSettingBinding(
                Ref: reference!,
                Actions: ReadActions(child),
                Cases: child.Children.Where(c => c.Name == "Case")
                    .Select(@case => new ModSettingCase(@case.Get("Value") ?? string.Empty, ReadActions(@case)))
                    .ToList()));
        }
        return result;
    }

    /// <summary>
    /// 序列化设置快照为 <c>&lt;SettingsSnapshot&gt;</c> 元素字符串（不含 XML 声明，便于嵌入外层 XML）。
    /// </summary>
    public static string SerializeSnapshot(ModSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = new XElement("SettingsSnapshot",
            new XElement("Definitions", snapshot.Definitions.Select(SerializeDefinition)),
            new XElement("Bindings", snapshot.Bindings.Select(SerializeBinding)));
        return root.ToString();
    }

    /// <summary>
    /// 回读 <see cref="SerializeSnapshot"/> 产物；与解析后再次序列化保持一致（roundtrip）。
    /// </summary>
    public static ModSettingsSnapshot ParseSnapshot(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ArgumentException("设置快照 XML 不能为空", nameof(xml));

        XElement element;
        try
        {
            element = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException($"设置快照 XML 无效: {ex.Message}", ex);
        }

        if (element.Name.LocalName != "SettingsSnapshot")
            throw new InvalidOperationException($"设置快照根节点必须是 SettingsSnapshot: {element.Name.LocalName}");

        var parsed = new Metadata();
        parsed.ParseElement(element);

        var definitions = parsed.Find("Definitions")
            ?? throw new InvalidOperationException("设置快照缺少 Definitions");
        var bindings = parsed.Find("Bindings")
            ?? throw new InvalidOperationException("设置快照缺少 Bindings");

        return new ModSettingsSnapshot(ParseDefinitions(definitions), ParseBindings(bindings));
    }

    /// <summary>
    /// 跨节点硬校验：设置定义合法性，以及叶子清单绑定/注入对定义与依赖的引用一致性。
    /// 失败抛 <see cref="InvalidOperationException"/>，消息列出全部问题。
    /// </summary>
    /// <param name="definitions">所属 Mod 的设置定义（无声明时传空列表）。</param>
    /// <param name="manifest">叶子清单实体。</param>
    public static void Validate(IReadOnlyList<ModSettingDefinition> definitions, ManifestEntry manifest)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(manifest);

        var errors = new List<string>();
        var defs = ValidateDefinitions(definitions, errors);
        ValidateManifest(manifest, defs, errors);

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"设置契约校验失败 (Manifest {manifest.Id}):\n- " + string.Join("\n- ", errors));
    }

    private static Dictionary<string, ModSettingDefinition> ValidateDefinitions(
        IReadOnlyList<ModSettingDefinition> definitions, List<string> errors)
    {
        var map = new Dictionary<string, ModSettingDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in definitions)
        {
            if (string.IsNullOrWhiteSpace(def.Id))
            {
                errors.Add("设置定义缺少 ID");
                continue;
            }
            if (!map.TryAdd(def.Id, def))
                errors.Add($"设置 ID 重复: {def.Id}");
            if (def.Id.Contains(':', StringComparison.Ordinal))
                errors.Add($"设置 ID 不能含 ':'（局部 ID）: {def.Id}");

            RequireLanguages(def.DisplayNames, $"设置 '{def.Id}' DisplayName", errors);
            RequireLanguages(def.Descriptions, $"设置 '{def.Id}' Description", errors);

            switch (def.Kind)
            {
                case ModSettingKind.Boolean:
                    if (!IsBooleanLiteral(def.Default))
                        errors.Add($"Boolean 设置 '{def.Id}' 的 Default 必须是 true 或 false: {def.Default}");
                    if (def.Options.Count > 0)
                        errors.Add($"Boolean 设置 '{def.Id}' 不能声明 Option");
                    break;

                case ModSettingKind.Choice:
                    if (def.Options.Count == 0)
                    {
                        errors.Add($"Choice 设置 '{def.Id}' 至少需要一个 Option");
                        break;
                    }
                    var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var option in def.Options)
                    {
                        if (string.IsNullOrWhiteSpace(option.Value))
                            errors.Add($"Choice 设置 '{def.Id}' 的 Option 缺少 Value");
                        else if (!values.Add(option.Value))
                            errors.Add($"Choice 设置 '{def.Id}' 的 Option Value 重复: {option.Value}");
                        RequireLanguages(option.DisplayNames, $"Choice '{def.Id}' Option '{option.Value}' DisplayName", errors);
                        RequireLanguages(option.Descriptions, $"Choice '{def.Id}' Option '{option.Value}' Description", errors);
                    }
                    if (!values.Contains(def.Default))
                        errors.Add($"Choice 设置 '{def.Id}' 的 Default 不在 Option 列表中: {def.Default}");
                    break;

                default:
                    errors.Add($"未知设置类型: {def.Kind}");
                    break;
            }
        }
        return map;
    }

    /// <summary>设置文案的 Language 必须是存在的标准主语言族标签（如 zh／en），不接受地区标签。</summary>
    private static readonly Regex LanguageFamilyPattern = new(@"^[A-Za-z]{2,3}$", RegexOptions.Compiled);

    private static void RequireLanguages(IReadOnlyList<LocalizedTextEntry> texts, string where, List<string> errors)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text.Language))
                errors.Add($"{where}: 缺少 Language 语言族标签");
            else if (!LanguageFamilyPattern.IsMatch(text.Language))
                errors.Add($"{where}: Language 必须是主语言族（如 zh／en）: {text.Language}");
        }
    }

    private static void ValidateManifest(
        ManifestEntry manifest,
        Dictionary<string, ModSettingDefinition> defs,
        List<string> errors)
    {
        var dlls = new Dictionary<string, ManifestDllEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in manifest.Dependencies)
        {
            if (dll.Id is null)
                continue;
            if (string.IsNullOrWhiteSpace(dll.Id))
            {
                errors.Add($"Dll '{dll.Name}' 的 ID 不能为空");
                continue;
            }
            if (!dlls.TryAdd(dll.Id, dll))
                errors.Add($"Dll ID 重复: {dll.Id}");

            if (dll.CustomData is { } custom)
            {
                if (!string.Equals(custom.Encoding, Utf8Encoding, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Dll '{dll.Name}' 的 CustomData Encoding 仅支持 utf8: {custom.Encoding}");
                if (!string.Equals(custom.RuntimeValue, LogFileRuntimeValue, StringComparison.Ordinal))
                    errors.Add($"Dll '{dll.Name}' 的 CustomData RuntimeValue 仅支持 {LogFileRuntimeValue}: {custom.RuntimeValue}");
            }

            if (dll.Protocol != null && !KnownProtocols.Contains(dll.Protocol, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Dll '{dll.Name}' 未知 Protocol: {dll.Protocol}");
        }

        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Skudef != null)
        {
            foreach (var command in manifest.Skudef.Commands)
            {
                if (!string.IsNullOrWhiteSpace(command.Package))
                    packages.Add(command.Package!);
            }
        }

        var injected = CollectInjectedRefs(manifest);

        var hasNewIds = manifest.Dependencies.Any(d => !string.IsNullOrWhiteSpace(d.Id));
        if ((hasNewIds || manifest.Settings.Count > 0) && manifest.Injection == null)
        {
            errors.Add("声明了 Dll ID 或版本设置绑定的清单必须显式声明 <Injection> 节点（无无条件动作时写空节点 <Injection />）");
        }

        if (manifest.Injection != null)
        {
            foreach (var action in manifest.Injection)
            {
                var dll = ResolveDll(dlls, action.Ref, errors, action.Inject ? "InjectDll" : "RequireDll");
                if (dll != null && action.Inject)
                    RequireKnownProtocol(dll, errors, "InjectDll");
            }
        }

        var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mountLanguageCount = 0;
        foreach (var binding in manifest.Settings)
        {
            if (string.IsNullOrWhiteSpace(binding.Ref))
            {
                errors.Add("SettingRef 缺少 Ref");
                continue;
            }
            if (!seenRefs.Add(binding.Ref))
                errors.Add($"SettingRef 重复: {binding.Ref}");
            if (!defs.TryGetValue(binding.Ref, out var def))
            {
                errors.Add($"SettingRef 引用了不存在的设置定义: {binding.Ref}");
                continue;
            }

            var context = $"SettingRef '{def.Id}'";

            if (def.Kind == ModSettingKind.Boolean)
            {
                if (binding.Cases.Count > 0)
                    errors.Add($"{context}: Boolean 设置不能声明 Case");
                foreach (var action in binding.Actions)
                {
                    if (action.Kind == ModSettingActionKind.MountLanguage)
                        errors.Add($"{context}: Boolean 设置不能使用 MountLanguage");
                    ValidateAction(action, context, dlls, packages, injected, errors);
                }
                continue;
            }

            // Choice 根 Actions 只允许 MountLanguage（枚举原值直传）；其余动作必须写在 Case 中。
            foreach (var action in binding.Actions)
            {
                if (action.Kind == ModSettingActionKind.MountLanguage)
                    mountLanguageCount++;
                else
                    errors.Add($"{context}: Choice 的 {action.Kind} 必须写在 Case 中");
                ValidateAction(action, context, dlls, packages, injected, errors);
            }

            var optionValues = new HashSet<string>(def.Options.Select(o => o.Value), StringComparer.OrdinalIgnoreCase);
            var seenCaseValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var @case in binding.Cases)
            {
                if (!seenCaseValues.Add(@case.Value))
                    errors.Add($"{context}: Case 值重复: {@case.Value}");
                if (!optionValues.Contains(@case.Value))
                    errors.Add($"{context}: Case 值不在 Option 列表中: {@case.Value}");

                var caseContext = $"{context} Case '{@case.Value}'";
                foreach (var action in @case.Actions)
                {
                    if (action.Kind == ModSettingActionKind.MountLanguage)
                        errors.Add($"{caseContext}: Case 不能声明 MountLanguage");
                    ValidateAction(action, caseContext, dlls, packages, injected, errors);
                }
            }
        }

        if (mountLanguageCount > 1)
            errors.Add($"MountLanguage 只能声明一次（语言上下文唯一），实际 {mountLanguageCount} 处");
    }

    private static void ValidateAction(
        ModSettingAction action,
        string context,
        Dictionary<string, ManifestDllEntry> dlls,
        HashSet<string> packages,
        HashSet<string> injected,
        List<string> errors)
    {
        switch (action.Kind)
        {
            case ModSettingActionKind.MountPackage:
                if (string.IsNullOrWhiteSpace(action.Target))
                    errors.Add($"{context}: MountPackage 缺少 Name");
                else if (!packages.Contains(action.Target))
                    errors.Add($"{context}: MountPackage 引用了 Skudef 未声明的 Package: {action.Target}");
                break;

            case ModSettingActionKind.MountLanguage:
                if (action.Target != null)
                    errors.Add($"{context}: MountLanguage 不能带 Target");
                break;

            case ModSettingActionKind.InjectDll:
                var injectTarget = ResolveDll(dlls, action.Target, errors, context);
                if (injectTarget != null)
                    RequireKnownProtocol(injectTarget, errors, context);
                break;

            case ModSettingActionKind.ConfigureLuaBridge:
                var bridgeTarget = ResolveDll(dlls, action.Target, errors, context);
                if (bridgeTarget != null)
                {
                    if (!string.Equals(bridgeTarget.Protocol, LuaBridgeProtocol, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"{context}: ConfigureLuaBridge 目标协议必须为 {LuaBridgeProtocol}，实际 {bridgeTarget.Protocol ?? "(未声明)"}: {bridgeTarget.Name}");
                    if (bridgeTarget.Id != null && !injected.Contains(bridgeTarget.Id))
                        errors.Add($"{context}: ConfigureLuaBridge 目标未处于注入动作集合: {bridgeTarget.Id}");
                }
                if (string.IsNullOrWhiteSpace(action.Adapter) || !KnownAdapters.Contains(action.Adapter, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"{context}: ConfigureLuaBridge 未知 Adapter: {action.Adapter}");
                break;
        }
    }

    private static HashSet<string> CollectInjectedRefs(ManifestEntry manifest)
    {
        var injected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Injection != null)
        {
            foreach (var action in manifest.Injection)
            {
                if (action.Inject && !string.IsNullOrWhiteSpace(action.Ref))
                    injected.Add(action.Ref);
            }
        }
        foreach (var binding in manifest.Settings)
            injected.UnionWith(CollectInjectedRefs(binding));
        return injected;
    }

    private static HashSet<string> CollectInjectedRefs(ModSettingBinding binding)
    {
        var injected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in binding.Actions)
        {
            if (action.Kind == ModSettingActionKind.InjectDll && !string.IsNullOrWhiteSpace(action.Target))
                injected.Add(action.Target!);
        }
        foreach (var @case in binding.Cases)
        {
            foreach (var action in @case.Actions)
            {
                if (action.Kind == ModSettingActionKind.InjectDll && !string.IsNullOrWhiteSpace(action.Target))
                    injected.Add(action.Target!);
            }
        }
        return injected;
    }

    private static ManifestDllEntry? ResolveDll(
        Dictionary<string, ManifestDllEntry> dlls, string? reference, List<string> errors, string context)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            errors.Add($"{context}: 引用缺少 Ref");
            return null;
        }
        if (!dlls.TryGetValue(reference, out var dll))
        {
            errors.Add($"{context}: 引用了不存在的 Dll ID: {reference}");
            return null;
        }
        return dll;
    }

    private static void RequireKnownProtocol(ManifestDllEntry dll, List<string> errors, string context)
    {
        if (string.IsNullOrWhiteSpace(dll.Protocol))
            errors.Add($"{context}: 注入目标必须声明 Protocol: {dll.Name}");
    }

    private static IReadOnlyList<ModSettingAction> ReadActions(Metadata node)
    {
        var actions = new List<ModSettingAction>();
        foreach (var child in node.Children)
        {
            switch (child.Name)
            {
                case "MountPackage":
                    actions.Add(new ModSettingAction(ModSettingActionKind.MountPackage, child.Get("Name"), null));
                    break;
                case "MountLanguage":
                    actions.Add(new ModSettingAction(ModSettingActionKind.MountLanguage, null, null));
                    break;
                case "InjectDll":
                    actions.Add(new ModSettingAction(ModSettingActionKind.InjectDll, child.Get("Ref"), null));
                    break;
                case "ConfigureLuaBridge":
                    actions.Add(new ModSettingAction(ModSettingActionKind.ConfigureLuaBridge, child.Get("Ref"), child.Get("Adapter")));
                    break;
                case "Case":
                    break; // Case 由 ParseBindings 单独读取
                default:
                    throw new InvalidOperationException($"未知设置动作节点: {child.Name}");
            }
        }
        return actions;
    }

    private static IReadOnlyList<LocalizedTextEntry> ReadLocalized(Metadata node, string name)
    {
        return node.Children
            .Where(c => c.Name == name)
            .Select(c => new LocalizedTextEntry(c.Get("Language") ?? string.Empty, c.Value ?? string.Empty))
            .ToList();
    }

    private static XElement SerializeDefinition(ModSettingDefinition definition)
    {
        var element = new XElement(definition.Kind == ModSettingKind.Boolean ? "Boolean" : "Choice",
            new XAttribute("ID", definition.Id),
            new XAttribute("Default", definition.Default));

        foreach (var text in definition.DisplayNames)
            element.Add(new XElement("DisplayName", new XAttribute("Language", text.Language), text.Text));
        foreach (var text in definition.Descriptions)
            element.Add(new XElement("Description", new XAttribute("Language", text.Language), text.Text));

        foreach (var option in definition.Options)
        {
            var optionElement = new XElement("Option", new XAttribute("Value", option.Value));
            foreach (var text in option.DisplayNames)
                optionElement.Add(new XElement("DisplayName", new XAttribute("Language", text.Language), text.Text));
            foreach (var text in option.Descriptions)
                optionElement.Add(new XElement("Description", new XAttribute("Language", text.Language), text.Text));
            element.Add(optionElement);
        }
        return element;
    }

    private static XElement SerializeBinding(ModSettingBinding binding)
    {
        var element = new XElement("SettingRef", new XAttribute("Ref", binding.Ref));
        foreach (var action in binding.Actions)
            element.Add(SerializeAction(action));

        foreach (var @case in binding.Cases)
        {
            var caseElement = new XElement("Case", new XAttribute("Value", @case.Value));
            foreach (var action in @case.Actions)
                caseElement.Add(SerializeAction(action));
            element.Add(caseElement);
        }
        return element;
    }

    private static XElement SerializeAction(ModSettingAction action) => action.Kind switch
    {
        ModSettingActionKind.MountPackage => new XElement("MountPackage", new XAttribute("Name", action.Target ?? string.Empty)),
        ModSettingActionKind.MountLanguage => new XElement("MountLanguage"),
        ModSettingActionKind.InjectDll => new XElement("InjectDll", new XAttribute("Ref", action.Target ?? string.Empty)),
        ModSettingActionKind.ConfigureLuaBridge => new XElement("ConfigureLuaBridge",
            new XAttribute("Ref", action.Target ?? string.Empty),
            new XAttribute("Adapter", action.Adapter ?? string.Empty)),
        _ => throw new InvalidOperationException($"未知设置动作: {action.Kind}"),
    };

    private static bool IsBooleanLiteral(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase));
}
