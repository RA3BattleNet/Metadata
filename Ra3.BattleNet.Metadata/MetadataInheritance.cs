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
        var known = new HashSet<string>(order, StringComparer.Ordinal);

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

        return result;
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

    /// <summary>同名控件：按子元素名覆盖（如 FontSize），子缺省保留 Base。</summary>
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
            fields[field.Name.LocalName] = new XElement(field);

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
}
