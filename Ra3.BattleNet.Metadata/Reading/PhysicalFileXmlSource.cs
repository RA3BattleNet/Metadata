using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// <see cref="IMetadataXmlSource"/> 的本地文件实现：文档读取只走本地文件，远程地址由
/// <see cref="HttpXmlSource"/> 负责，调用方按入口形态选择（见 <see cref="MetadataReader.Read"/>）。
/// 构造无副作用、无全局状态。
/// </summary>
public sealed class PhysicalFileXmlSource : IMetadataXmlSource
{
    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public XDocument LoadDocument(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader);
    }

    /// <inheritdoc />
    // 作者可能写 \ 分隔的路径；先统一成 / 再交给 Path.Combine，跨平台口径一致
    // 绝对 URL 原样返回：本地文档里的远程引用交给根检查拒绝，不做路径拼接
    public string Combine(string basePath, string relativePath) => MetadataReader.IsHttp(relativePath)
        ? new Uri(relativePath).AbsoluteUri
        : Path.Combine(basePath, relativePath.Replace('\\', '/'));

    /// <inheritdoc />
    public string? GetDirectoryName(string path) => Path.GetDirectoryName(path);

    /// <inheritdoc />
    public string GetRelativePath(string fromPath, string toPath) =>
        MetadataReader.IsHttp(toPath) ? toPath : Path.GetRelativePath(fromPath, toPath);

    /// <inheritdoc />
    public string GetFullPath(string path)
    {
        if (MetadataReader.IsHttp(path)) return new Uri(path).AbsoluteUri;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) return Path.GetFullPath(uri.LocalPath);
        return Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public bool IsPathRooted(string path) => MetadataReader.IsHttp(path) || Path.IsPathRooted(path);
}
