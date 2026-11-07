using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

public sealed class MetadataIncludeInfo
{
    internal MetadataIncludeInfo(string sourcePath, string targetPath)
    {
        SourcePath = sourcePath;
        TargetPath = targetPath;
    }
    public string SourcePath { get; }
    public string TargetPath { get; }
}

public sealed class MetadataDocumentInfo
{
    internal MetadataDocumentInfo(string path, string relativePath, XDocument document)
    {
        Path = path;
        RelativePath = relativePath;
        Document = new XDocument(document);
        NormalizedDocument = new XDocument(document);
    }
    public string Path { get; }
    public string RelativePath { get; }
    public XDocument Document { get; }
    public XDocument NormalizedDocument { get; }
    public IReadOnlyList<MetadataIncludeInfo> Includes => IncludeList;
    internal List<MetadataIncludeInfo> IncludeList { get; } = [];
}

public sealed class MetadataReadResult
{
    internal MetadataReadResult(XDocument document, string entryPath, string sourceRoot,
        IReadOnlyList<MetadataDocumentInfo> documents, IMetadataXmlSource source)
    {
        Document = document;
        Root = MetadataParser.Load(document);
        EntryPath = entryPath;
        SourceRoot = sourceRoot;
        Documents = documents;
        Source = source;
    }
    public Metadata Root { get; }
    public XDocument Document { get; }
    public string EntryPath { get; }
    public string SourceRoot { get; }
    public IReadOnlyList<MetadataDocumentInfo> Documents { get; }
    internal IMetadataXmlSource Source { get; }
}
