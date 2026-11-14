namespace Ra3.BattleNet.Metadata;

public static class MetadataId
{
    public static string Qualify(string scope, string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        if (localId.Contains('/') || localId.Contains(':')) return localId;
        return string.IsNullOrEmpty(scope) ? Escape(localId) : scope + "/" + Escape(localId);
    }

    public static string LocalId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        return Uri.UnescapeDataString(id[(id.LastIndexOf('/') + 1)..]);
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
    private static string Escape(string value) => value is "." or ".."
        ? value.Replace(".", "%2E", StringComparison.Ordinal) : Uri.EscapeDataString(value);
}
