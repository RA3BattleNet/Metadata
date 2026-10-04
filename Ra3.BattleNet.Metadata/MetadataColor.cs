namespace Ra3.BattleNet.Metadata;

/// <summary>样式颜色校验及 CSS 表示转换；不猜测八位颜色的格式。</summary>
public static class MetadataColor
{
    /// <summary>
    /// 将 CSS（#RRGGBB / #RRGGBBAA）或 ARGB（#AARRGGBB）颜色转换成 CSS 表示。
    /// </summary>
    /// <param name="value">颜色节点的文本。</param>
    /// <param name="format">颜色节点的 Format 属性；null 表示 CSS。</param>
    /// <exception cref="FormatException">格式、长度或十六进制字符不合法。</exception>
    public static string ToCss(string value, string? format = null)
    {
        Validate(value, format);

        if (format != "ARGB")
            return value;

        return string.Create(9, value, static (result, source) =>
        {
            result[0] = '#';
            source.AsSpan(3, 6).CopyTo(result[1..]);
            source.AsSpan(1, 2).CopyTo(result[7..]);
        });
    }

    internal static void Validate(string value, string? format)
    {
        if (format is not (null or "CSS" or "ARGB"))
            throw new FormatException($"颜色 Format '{format}' 不合法，只允许 CSS 或 ARGB");

        if (value is null || value.Length is not (7 or 9) || value[0] != '#'
            || (format == "ARGB" && value.Length != 9))
            throw new FormatException($"颜色 '{value}' 与格式 {format ?? "CSS"} 不匹配：CSS 使用 #RRGGBB 或 #RRGGBBAA，ARGB 使用 #AARRGGBB");

        foreach (var character in value.AsSpan(1))
        {
            if (!char.IsAsciiHexDigit(character))
                throw new FormatException($"颜色 '{value}' 包含非法十六进制字符");
        }
    }
}
