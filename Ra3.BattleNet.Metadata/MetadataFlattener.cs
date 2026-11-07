using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

public static class MetadataFlattener
{
    public static XDocument Flatten(string entryPath, string sourceRoot, string schemaVersion, string contentRevision) =>
        Flatten(new PhysicalFileXmlSource(), entryPath, sourceRoot, schemaVersion, contentRevision, true);

    internal static XDocument Flatten(string entryPath, string sourceRoot, string schemaVersion,
        string contentRevision, bool validateCompatibility) =>
        Flatten(new PhysicalFileXmlSource(), entryPath, sourceRoot, schemaVersion, contentRevision, validateCompatibility);

    internal static XDocument Flatten(IMetadataXmlSource source, string entryPath, string sourceRoot,
        string schemaVersion, string contentRevision, bool validateCompatibility)
    {
        var result = MetadataReader.ReadCore(source, entryPath, new MetadataSourceOptions
        {
            SourceRoot = sourceRoot,
            CustomSource = source,
            ForceSourceTree = true,
            LegacySourcePaths = true
        });
        var document = result.Document;
        if (validateCompatibility) ModCompatibility.Validate(document.Root!);
        document.Root!.SetAttributeValue("SchemaVersion", schemaVersion);
        document.Root.SetAttributeValue("ContentRevision", contentRevision);
        return document;
    }

    public static string PathPrefix(string absoluteFilePath, string sourceRoot) =>
        PathPrefix(new PhysicalFileXmlSource(), absoluteFilePath, sourceRoot);

    internal static string PathPrefix(IMetadataXmlSource source, string absoluteFilePath, string sourceRoot)
    {
        var full = source.GetFullPath(absoluteFilePath);
        var root = source.GetFullPath(sourceRoot);
        MetadataReader.EnsureWithinRoot(source, full, root, "Document");
        var relative = source.GetRelativePath(root, full).Replace('\\', '/');
        return relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? relative[..^4] : relative;
    }

    public static string QualifyId(string pathPrefix, string localId) => MetadataId.Qualify(pathPrefix, localId);
    public static string LocalId(string? id) => MetadataId.LocalId(id);
}
