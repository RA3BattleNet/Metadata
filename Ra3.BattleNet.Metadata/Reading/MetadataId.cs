namespace Ra3.BattleNet.Metadata;

public static class MetadataId
{
    public static string Qualify(string scope, string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        if (localId.Contains(':') || localId.Any(char.IsControl))
            throw new InvalidOperationException($"Invalid registration ID: {localId}");
        if (localId.Contains('/'))
        {
            ValidatePath(localId);
            return localId;
        }
        if (!string.IsNullOrEmpty(scope)) ValidatePath(scope);
        return string.IsNullOrEmpty(scope) ? Escape(localId) : scope + "/" + Escape(localId);
    }

    public static string LocalId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        return Unescape(id[(id.LastIndexOf('/') + 1)..]);
    }

    public static string PathPrefix(string absoluteFilePath, string sourceRoot) =>
        PathPrefix(new PhysicalFileXmlSource(), absoluteFilePath, sourceRoot);

    internal static string PathPrefix(IMetadataXmlSource source, string absoluteFilePath, string sourceRoot)
    {
        var full = source.GetFullPath(absoluteFilePath);
        var root = source.GetFullPath(sourceRoot);
        MetadataReader.EnsureWithinRoot(source, full, root, "Document");
        var relative = source.GetRelativePath(root, full).Replace('\\', '/');
        if (relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) relative = relative[..^4];
        return string.Join("/", relative.Split('/').Select(segment => Escape(MetadataReader.IsHttp(full)
            ? Uri.UnescapeDataString(segment) : segment)));
    }

    private static void ValidatePath(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            var value = Unescape(segment);
            if (value.Length == 0 || segment is "." or ".." || value.Contains(':') || value.Contains('\\')
                || value.Any(char.IsControl))
                throw new InvalidOperationException($"Invalid path ID: {path}");
        }
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
