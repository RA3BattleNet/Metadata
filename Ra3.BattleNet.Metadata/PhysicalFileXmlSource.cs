using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// <see cref="IMetadataXmlSource"/> 的本地文件实现：逐个转发 System.IO，构造无副作用、无全局状态。
/// </summary>
public sealed class PhysicalFileXmlSource : IMetadataXmlSource
{
    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public XDocument LoadDocument(string path) => XDocument.Load(path);

    /// <inheritdoc />
    // 作者可能写 \ 分隔的路径；先统一成 / 再交给 Path.Combine，跨平台口径一致
    public string Combine(string basePath, string relativePath) =>
        Path.Combine(basePath, relativePath.Replace('\\', '/'));

    /// <inheritdoc />
    public string? GetDirectoryName(string path) => Path.GetDirectoryName(path);

    /// <inheritdoc />
    public string GetRelativePath(string fromPath, string toPath) => Path.GetRelativePath(fromPath, toPath);

    /// <inheritdoc />
    public string GetFullPath(string path) => Path.GetFullPath(path);

    /// <inheritdoc />
    public bool IsPathRooted(string path) => Path.IsPathRooted(path);
}
