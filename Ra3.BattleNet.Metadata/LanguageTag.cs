namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 语言标签比较工具：元数据里统一存短标签 <c>zh</c> 和 <c>en</c>。
/// 如果查询带地区（如 <c>zh-CN</c> 或 <c>en-US</c>），自动按前面的语言族（<c>zh</c> 或 <c>en</c>）去匹配；
/// 查询不带地区时只做精确比较。比较时不区分大小写，<c>en</c> 不会去匹配 <c>english</c>，
/// 也不做反向匹配（查 <c>zh</c> 不会去匹配 <c>zh-CN</c>）。
/// </summary>
public static class LanguageTag
{
    public static bool Matches(string? candidate, string? query)
    {
        var stored = candidate?.Trim();
        var asked = query?.Trim();
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(asked))
            return false;

        if (string.Equals(stored, asked, StringComparison.OrdinalIgnoreCase))
            return true;

        var dash = asked.IndexOf('-');
        return dash > 0 && string.Equals(stored, asked[..dash], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从列表里挑出第一个语言标签能对上的条目，都没对上就返回默认值。</summary>
    public static T? Select<T>(IEnumerable<T>? items, Func<T, string?> language, string? query)
        => items is null ? default : items.FirstOrDefault(item => Matches(language(item), query));
}
