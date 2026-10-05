using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 源树 Base + InheritFrom 合并。仅构建期；发布物无 Base/InheritFrom。
/// </summary>
public static class MetadataInheritance
{
    private static readonly string[] ModChildOrder =
        ["CurrentVersion", "Icon", "Style", "Packages", "Posts"];

    private static readonly string[] ApplicationChildOrder =
        ["Version", "Packages", "Posts"];

    private static readonly string[] StyleChildOrder = ["Logo", "Controls", "Background"];

    /// <summary>
    /// 在已展开的根树上解析继承：合并后去掉 InheritFrom，并删除全部 Base。
    /// </summary>
    public static void Resolve(XElement root)
    {
        if (root.Name.LocalName != "Metadata")
            throw new InvalidOperationException("继承解析要求根节点为 Metadata");

        var bases = root.Elements().Where(e => e.Name.LocalName == "Base").ToList();
        var baseMap = new Dictionary<string, XElement>(StringComparer.Ordinal);

        foreach (var baseEl in bases)
        {
            var id = baseEl.Attribute("ID")?.Value;
            var kind = baseEl.Attribute("Kind")?.Value;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Base 缺少 ID");
            if (kind is not ("Mod" or "Application"))
                throw new InvalidOperationException($"Base '{id}' 的 Kind 必须是 Mod 或 Application");
            if (!baseMap.TryAdd(id, baseEl))
                throw new InvalidOperationException($"Base ID 重复: {id}");
        }

        foreach (var entity in root.Elements().Where(e => e.Name.LocalName is "Mod" or "Application"))
        {
            var id = entity.Attribute("ID")?.Value;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (baseMap.ContainsKey(id))
                throw new InvalidOperationException($"Base ID 与 {entity.Name.LocalName} ID 冲突: {id}");
        }

        foreach (var entity in root.Elements().Where(e => e.Name.LocalName is "Mod" or "Application").ToList())
        {
            var inherit = entity.Attribute("InheritFrom")?.Value;
            if (string.IsNullOrWhiteSpace(inherit))
                continue;

            if (!baseMap.TryGetValue(inherit, out var baseEl))
                throw new InvalidOperationException(
                    $"{entity.Name.LocalName} '{entity.Attribute("ID")?.Value}' 的 InheritFrom 找不到 Base: {inherit}");

            var kind = baseEl.Attribute("Kind")!.Value;
            if (!string.Equals(kind, entity.Name.LocalName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{entity.Name.LocalName} '{entity.Attribute("ID")?.Value}' 不能继承 Kind={kind} 的 Base '{inherit}'");
            }

            var merged = MergeEntity(baseEl, entity);
            entity.ReplaceWith(merged);
        }

        foreach (var baseEl in root.Elements().Where(e => e.Name.LocalName == "Base").ToList())
            baseEl.Remove();

        var leftover = root.DescendantsAndSelf()
            .Where(e => e.Attribute("InheritFrom") != null)
            .Select(e => $"{e.Name.LocalName}:{e.Attribute("ID")?.Value}")
            .ToList();
        if (leftover.Count > 0)
            throw new InvalidOperationException("仍有未解析的 InheritFrom: " + string.Join(", ", leftover));
    }

    internal static XElement MergeEntity(XElement baseEl, XElement child)
    {
        var result = new XElement(child.Name);

        foreach (var attr in baseEl.Attributes())
        {
            if (attr.Name.LocalName is "ID" or "Kind" or "InheritFrom")
                continue;
            result.SetAttributeValue(attr.Name, attr.Value);
        }

        foreach (var attr in child.Attributes())
        {
            if (attr.Name.LocalName == "InheritFrom")
                continue;
            result.SetAttributeValue(attr.Name, attr.Value);
        }

        if (string.IsNullOrWhiteSpace(result.Attribute("ID")?.Value))
            throw new InvalidOperationException($"{child.Name.LocalName} 合并后缺少 ID");

        var order = child.Name.LocalName == "Mod" ? ModChildOrder : ApplicationChildOrder;
        var known = new HashSet<string>(order, StringComparer.Ordinal) { "Settings" };

        foreach (var name in order)
        {
            var fromBase = baseEl.Element(name);
            var fromChild = child.Element(name);
            if (fromBase == null && fromChild == null)
                continue;

            if (fromChild == null)
            {
                result.Add(new XElement(fromBase!));
                continue;
            }

            if (fromBase == null)
            {
                result.Add(new XElement(fromChild));
                continue;
            }

            if (name == "Style")
                result.Add(MergeStyle(fromBase, fromChild));
            else
                result.Add(new XElement(fromChild));
        }

        // 子节点独有、不在白名单顺序表中的元素（扩展口）
        foreach (var el in child.Elements())
        {
            if (known.Contains(el.Name.LocalName))
                continue;
            result.Add(new XElement(el));
        }

        foreach (var el in baseEl.Elements())
        {
            if (known.Contains(el.Name.LocalName))
                continue;
            if (result.Element(el.Name) != null)
                continue;
            result.Add(new XElement(el));
        }

        // Settings 局部 ID 保序合并：保留父项顺序与位置，子项同 ID 同类型直接原位覆盖（不改变相对位置），子项新增项依次追加到末尾。
        if (child.Name.LocalName == "Mod")
        {
            var baseSettings = baseEl.Element("Settings");
            var childSettings = child.Element("Settings");
            if (baseSettings != null || childSettings != null)
                result.Add(MergeSettings(baseSettings, childSettings));
        }

        return result;
    }

    /// <summary>
    /// 合并 Mod 级别的设置定义：
    /// 1. 保序与原位覆盖：继承时以父定义的出现顺序为基准，子定义中同 ID 且同类型的项直接覆盖父项内容，但位置保持在父项原位；子定义中新增的 ID 则按其声明顺序追加到末尾。
    /// 2. 类型一致性硬要求：若子项与父项同 ID 但类型不一致（例如父项是 Boolean，子项改为 Choice），直接抛出异常阻止非法覆盖。
    /// </summary>
    internal static XElement MergeSettings(XElement? baseSettings, XElement? childSettings)
    {
        var result = new XElement("Settings");
        if (baseSettings == null)
        {
            foreach (var el in childSettings!.Elements())
                result.Add(new XElement(el));
            return result;
        }
        if (childSettings == null)
        {
            foreach (var el in baseSettings.Elements())
                result.Add(new XElement(el));
            return result;
        }

        var childById = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var childOrder = new List<XElement>();
        foreach (var el in childSettings.Elements())
        {
            var id = SettingId(el);
            if (!childById.TryAdd(id, el))
                throw new InvalidOperationException($"设置 ID 重复: {id}");
            childOrder.Add(el);
        }

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseEl in baseSettings.Elements())
        {
            var id = SettingId(baseEl);
            if (childById.TryGetValue(id, out var childEl))
            {
                if (!string.Equals(baseEl.Name.LocalName, childEl.Name.LocalName, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"设置 ID '{id}' 类型不一致: 父 {baseEl.Name.LocalName}，子 {childEl.Name.LocalName}");
                result.Add(new XElement(childEl));
            }
            else
            {
                result.Add(new XElement(baseEl));
            }
            emitted.Add(id);
        }

        foreach (var childEl in childOrder)
        {
            if (!emitted.Contains(SettingId(childEl)))
                result.Add(new XElement(childEl));
        }

        return result;
    }

    private static string SettingId(XElement el)
    {
        if (el.Name.LocalName is not ("Boolean" or "Choice"))
            throw new InvalidOperationException($"Settings 未知子元素: {el.Name.LocalName}");

        var id = el.Attribute("ID")?.Value;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Settings 子元素 {el.Name.LocalName} 缺少 ID");
        return id;
    }

    internal static XElement MergeStyle(XElement baseStyle, XElement childStyle)
    {
        var result = new XElement("Style");
        foreach (var attr in baseStyle.Attributes())
            result.SetAttributeValue(attr.Name, attr.Value);
        foreach (var attr in childStyle.Attributes())
            result.SetAttributeValue(attr.Name, attr.Value);

        foreach (var name in StyleChildOrder)
        {
            var fromBase = baseStyle.Element(name);
            var fromChild = childStyle.Element(name);
            if (fromBase == null && fromChild == null)
                continue;

            if (fromChild == null)
            {
                result.Add(new XElement(fromBase!));
                continue;
            }

            if (fromBase == null || name is "Logo" or "Background")
            {
                result.Add(new XElement(fromChild));
                continue;
            }

            // Controls 深合并
            result.Add(MergeControls(fromBase, fromChild));
        }

        return result;
    }

    internal static XElement MergeControls(XElement baseControls, XElement childControls)
    {
        var result = new XElement("Controls");
        foreach (var attr in baseControls.Attributes())
            result.SetAttributeValue(attr.Name, attr.Value);
        foreach (var attr in childControls.Attributes())
            result.SetAttributeValue(attr.Name, attr.Value);

        var map = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var control in baseControls.Elements())
            map[control.Name.LocalName] = new XElement(control);

        foreach (var control in childControls.Elements())
        {
            var name = control.Name.LocalName;
            if (!map.TryGetValue(name, out var existing))
            {
                map[name] = new XElement(control);
                continue;
            }

            map[name] = MergeNamedBag(existing, control);
        }

        foreach (var control in map.Values)
            result.Add(control);

        return result;
    }

    /// <summary>
    /// 同名控件：普通子元素整节点覆盖（颜色值与 Format 一起替换，不继承 Format）；
    /// Hover/Active 按各自字段合并。空状态包不覆盖父级，也不从按钮基础字段补齐。
    /// </summary>
    internal static XElement MergeNamedBag(XElement baseControl, XElement childControl)
    {
        var result = new XElement(baseControl.Name);
        foreach (var attr in baseControl.Attributes())
            result.SetAttributeValue(attr.Name, attr.Value);
        foreach (var attr in childControl.Attributes())
            result.SetAttributeValue(attr.Name, attr.Value);

        var fields = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var field in baseControl.Elements())
            fields[field.Name.LocalName] = new XElement(field);

        foreach (var field in childControl.Elements())
        {
            var name = field.Name.LocalName;
            if (IsStateBag(name))
            {
                // 空 Hover/Active 是 no-op：保留父状态，也不物化缺省状态。
                if (!field.HasElements)
                    continue;

                if (!fields.TryGetValue(name, out var existing))
                {
                    fields[name] = new XElement(field);
                    continue;
                }

                fields[name] = MergeStateBag(existing, field);
                continue;
            }

            // 颜色等字段整节点替换，Format 不从父节点继承。
            fields[name] = new XElement(field);
        }

        foreach (var field in fields.Values)
            result.Add(field);

        if (!baseControl.HasElements && !childControl.HasElements)
        {
            var text = childControl.Value;
            if (string.IsNullOrEmpty(text))
                text = baseControl.Value;
            if (!string.IsNullOrEmpty(text))
                result.Value = text;
        }

        return result;
    }

    /// <summary>Hover/Active：只合并本状态的原始字段；颜色节点整节点替换。</summary>
    private static XElement MergeStateBag(XElement baseState, XElement childState)
    {
        var result = new XElement(baseState.Name);
        foreach (var attr in baseState.Attributes())
            result.SetAttributeValue(attr.Name, attr.Value);
        foreach (var attr in childState.Attributes())
            result.SetAttributeValue(attr.Name, attr.Value);

        var fields = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var field in baseState.Elements())
            fields[field.Name.LocalName] = new XElement(field);
        foreach (var field in childState.Elements())
            fields[field.Name.LocalName] = new XElement(field);

        foreach (var field in fields.Values)
            result.Add(field);

        return result;
    }

    private static bool IsStateBag(string name) => name is "Hover" or "Active";
}
