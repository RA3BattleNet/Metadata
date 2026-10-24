using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 模组设置契约：解析与校验 <c>Mod/Settings</c>（定义）与 <c>Manifest/Settings</c>（版本绑定），并负责安装快照序列化。
/// 设置 ID 属于 Mod 局部，依赖 Dll ID 属于 Manifest 局部，均不含全根资源前缀。
/// Manifest 仅使用 <c>SettingRef Ref="..."</c> 引用定义；开关为 false 不等同于全局阻止注入。
/// </summary>
public static class ModSettingsContract
{
    /// <summary>支持的 DLL 注入协议。凡是需要被注入的 DLL，都必须在清单的 Protocol 属性中显式指定其中之一。</summary>
    public static readonly IReadOnlyList<string> KnownProtocols = ["lyi-create-process", "easyhook"];

    /// <summary>客户端已实现的 LuaBridge 功能适配器名称列表，用于校验 ConfigureLuaBridge 的 Adapter 属性。</summary>
    public static readonly IReadOnlyList<string> KnownAdapters =
        ["audio-fix", "desync-debug", "debug-overlay", "enhancer-logger", "always-enable-engine-fix"];

    private const string LuaBridgeProtocol = "lyi-create-process";
    private const string LogFileRuntimeValue = "log-file";
    private const string Utf8Encoding = "utf8";

    /// <summary>
    /// 解析 Mod 级设置定义（来源可是元数据节点的 <c>Mod/Settings</c>，也可以是快照中的 <c>Definitions</c>）。
    /// 返回列表中各项的顺序与 XML 中子元素（<c>Boolean</c>/<c>Choice</c>）的声明顺序严格一致。
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
    /// 解析版本清单的设置绑定（来源可是 <c>Manifest/Settings</c>，也可以是快照中的 <c>Bindings</c>）。
    /// 返回列表中各项的顺序与 XML 中 <c>SettingRef</c> 的声明顺序严格一致。
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
    /// 将模组设置快照序列化为 <c>&lt;SettingsSnapshot&gt;</c> XML 字符串。
    /// 不包含 XML 头部声明（&lt;?xml...?&gt;），以便直接嵌入到宿主系统的存储文件或外层 XML 中。
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
    /// 回读 <see cref="SerializeSnapshot"/> 输出的 XML 字符串，恢复为强类型快照对象。
    /// 该方法保证与序列化互为可逆操作（roundtrip），字段解析顺序与内容完全对称。
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
    /// 校验 Mod 级设置定义合法性，以及叶子 Manifest 绑定与引用的协同一致性。
    /// 包含定义值域与多语言校验、设置与 Dll 局部引用存在性、类型规则匹配，以及新格式清单的显式 Injection 节点要求。
    /// </summary>
    /// <param name="definitions">所属 Mod 的设置定义列表；若 Mod 未声明任何设置则传空列表。</param>
    /// <param name="manifest">待校验的叶子清单实体。</param>
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

    /// <summary>
    /// 校验语言标签必须为标准主语言族代号（如 zh、en）。
    /// 模组设置面向通用国际化显示，不应绑定特定国家/地区变体（如 zh-CN、en-US）。
    /// </summary>
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

            // Choice 根级 Actions 只允许声明 MountLanguage（用于把用户选中的语言代码直传挂载）；
            // 其余具体动作（挂载包、注入 DLL、配置桥接等）必须按分支写在对应的 Case 中。
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
                    break; // Case 属于条件分支容器，由 ParseBindings 循环单独提取，不混入平铺动作列表
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
