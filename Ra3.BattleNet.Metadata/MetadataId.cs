namespace Ra3.BattleNet.Metadata;

public static class MetadataId
{
    public static string Qualify(string scope, string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        if (localId.Contains(':'))
            throw new InvalidOperationException($"Registration ID must be local: {localId}");
        return string.IsNullOrWhiteSpace(scope) ? localId : scope + ":" + localId;
    }

    public static string LocalId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        var separator = id.LastIndexOf(':');
        return separator < 0 ? id : id[(separator + 1)..];
    }

    public static string EncodePath(string legacyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyId);
        var separator = legacyId.LastIndexOf(':');
        if (separator < 0) return Escape(legacyId);
        var scope = legacyId[..separator].Split('/').Select(Escape);
        return string.Join("/", scope) + "/" + Escape(legacyId[(separator + 1)..]);
    }

    public static string DecodePath(string pathId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathId);
        var parts = pathId.Split('/');
        if (parts.Any(p => p.Length == 0))
            throw new InvalidOperationException($"Invalid path ID: {pathId}");
        var decoded = parts.Select(Unescape).ToArray();
        if (decoded.Any(p => p.Contains(':') || p.Any(char.IsControl)) || decoded[..^1].Any(p => p.Contains('/')))
            throw new InvalidOperationException($"Invalid path ID segment: {pathId}");
        return decoded.Length == 1 ? decoded[0] : string.Join("/", decoded[..^1]) + ":" + decoded[^1];
    }

    private static string Escape(string value) => value is "." or ".."
        ? value.Replace(".", "%2E", StringComparison.Ordinal) : Uri.EscapeDataString(value);

    private static string Unescape(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                throw new InvalidOperationException($"Invalid path ID escape: {value}");
            i += 2;
        }
        return Uri.UnescapeDataString(value);
    }
}
