namespace Ra3.BattleNet.Metadata;

/// <summary>Resolves a package manifest in the declaration's shared source-root context.</summary>
public static class MetadataSourceManifest
{
    public static Metadata? Resolve(MetadataReadResult declaration, Metadata package)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(package);
        var id = package.Find("Manifest")?.Value;
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var registration = declaration.Root.ManifestRegistration(id)
            ?? throw new InvalidOperationException($"模组声明找不到文件清单：{id}");
        var source = registration.Get("Source");
        if (string.IsNullOrWhiteSpace(source))
            return registration;

        var leaf = MetadataResourceUri.Resolve(declaration.EntryPath, source);
        if (!leaf.IsFile)
            throw new InvalidOperationException("本地模组文件清单必须在模组目录内");
        var document = MetadataReader.Read(leaf.LocalPath, new MetadataSourceOptions
        {
            SourceRoot = declaration.SourceRoot,
            InlineManifests = true,
        }).Root;
        return document.ManifestRegistration(id)
            ?? throw new InvalidOperationException($"Manifest identity does not match registration: {id}");
    }
}
