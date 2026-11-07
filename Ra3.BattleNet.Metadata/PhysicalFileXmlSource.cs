using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// <see cref="IMetadataXmlSource"/> 的本地文件实现：逐个转发 System.IO，构造无副作用、无全局状态。
/// </summary>
public sealed class PhysicalFileXmlSource : IMetadataXmlSource
{
    /// <inheritdoc />
    public bool Exists(string path)
    {
        if (!MetadataReader.IsHttp(path)) return File.Exists(path);
        using var source = new HttpXmlSource(new Uri(new Uri(path), "."));
        return source.Exists(path);
    }

    /// <inheritdoc />
    public XDocument LoadDocument(string path)
    {
        if (!MetadataReader.IsHttp(path))
        {
            using var stream = File.OpenRead(path);
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null
            });
            return XDocument.Load(reader);
        }
        using var source = new HttpXmlSource(new Uri(new Uri(path), "."));
        return source.LoadDocument(path);
    }

    /// <inheritdoc />
    // 作者可能写 \ 分隔的路径；先统一成 / 再交给 Path.Combine，跨平台口径一致
    public string Combine(string basePath, string relativePath)
    {
        if (MetadataReader.IsHttp(relativePath)) return new Uri(relativePath).AbsoluteUri;
        var relative = relativePath.Replace('\\', '/');
        if (!MetadataReader.IsHttp(basePath)) return Path.Combine(basePath, relative);
        var directory = basePath.EndsWith('/') ? basePath : basePath + "/";
        return new Uri(new Uri(directory), relative).AbsoluteUri;
    }

    /// <inheritdoc />
    public string? GetDirectoryName(string path) => MetadataReader.IsHttp(path)
        ? new Uri(new Uri(path), ".").AbsoluteUri : Path.GetDirectoryName(path);

    /// <inheritdoc />
    public string GetRelativePath(string fromPath, string toPath)
    {
        if (!MetadataReader.IsHttp(fromPath))
            return MetadataReader.IsHttp(toPath) ? toPath : Path.GetRelativePath(fromPath, toPath);
        if (!MetadataReader.IsHttp(toPath)) return toPath;
        var from = new Uri(fromPath);
        var to = new Uri(toPath);
        if (!string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase) || from.Port != to.Port)
            return to.AbsoluteUri;
        var directory = from.AbsoluteUri.EndsWith('/') ? from.AbsoluteUri : from.AbsoluteUri + "/";
        return to.AbsoluteUri.StartsWith(directory, StringComparison.Ordinal) ? to.AbsoluteUri[directory.Length..] : to.AbsoluteUri;
    }

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
