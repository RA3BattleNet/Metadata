using SkiaSharp;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 图片处理工具：将图片转换为 WebP 格式。
/// </summary>
public static class ImageProcessor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif"
    };

    /// <summary>
    /// 扫描输出目录中所有 <see cref="Image"/> 元素引用的本地图片，转换为 WebP 格式。
    /// </summary>
    /// <param name="metadata">已加载的元数据根节点。</param>
    /// <param name="outputDir">输出目录路径。</param>
    /// <returns>转换的文件数量。</returns>
    public static int ConvertImagesToWebP(Metadata metadata, string outputDir)
    {
        int convertedCount = 0;
        var images = metadata.GetAllElements("Image");

        foreach (var image in images)
        {
            var source = image.Get("Source");
            if (string.IsNullOrWhiteSpace(source))
                continue;

            var sourcePath = Path.Combine(outputDir, source.Replace('\\', '/'));
            if (!File.Exists(sourcePath))
                continue;

            var ext = Path.GetExtension(sourcePath);
            if (!SupportedExtensions.Contains(ext))
                continue;

            var webpPath = Path.ChangeExtension(sourcePath, ".webp");
            if (ConvertToWebP(sourcePath, webpPath))
            {
                // 更新 Source 引用为 WebP 文件
                var webpRelative = Path.ChangeExtension(source, ".webp").Replace('\\', '/');
                // 通过反射设置变量（因为 _variables 是私有字段）
                // 使用公共 API：重新加载时 Source 会指向 webp
                // 这里我们直接操作文件，元数据中的 Source 会在后续变量替换时更新
                Console.WriteLine($"  WebP: {source} -> {webpRelative}");
                convertedCount++;
            }
        }

        return convertedCount;
    }

    /// <summary>
    /// 将单张图片转换为 WebP 格式。
    /// </summary>
    private static bool ConvertToWebP(string sourcePath, string webpPath)
    {
        try
        {
            // 如果 WebP 已存在且比源文件新，跳过
            if (File.Exists(webpPath))
            {
                var sourceTime = File.GetLastWriteTimeUtc(sourcePath);
                var webpTime = File.GetLastWriteTimeUtc(webpPath);
                if (webpTime >= sourceTime)
                    return true;
            }

            using var input = File.OpenRead(sourcePath);
            using var stream = new SKManagedStream(input);
            using var codec = SKCodec.Create(stream);
            if (codec == null)
            {
                Console.WriteLine($"  警告: 无法解码图片 {sourcePath}");
                return false;
            }

            var info = codec.Info;
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap == null)
            {
                Console.WriteLine($"  警告: 无法解码图片 {sourcePath}");
                return false;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Webp, 90);

            using var output = File.Open(webpPath, FileMode.Create, FileAccess.Write);
            data.SaveTo(output);

            Console.WriteLine($"  转换: {Path.GetFileName(sourcePath)} -> {Path.GetFileName(webpPath)} " +
                             $"({data.Size / 1024} KB)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  警告: 转换图片失败 {sourcePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 更新 XML 中 Image 元素的 Source 引用为 .webp 路径。
    /// </summary>
    public static void UpdateImageSourceReferences(string outputDir)
    {
        var xmlFiles = Directory.GetFiles(outputDir, "*.xml", SearchOption.AllDirectories);
        foreach (var xmlFile in xmlFiles)
        {
            var content = File.ReadAllText(xmlFile);
            if (!content.Contains("<Image"))
                continue;

            bool modified = false;
            var doc = System.Xml.Linq.XDocument.Load(xmlFile);
            foreach (var imageEl in doc.Descendants("Image"))
            {
                var sourceAttr = imageEl.Attribute("Source");
                if (sourceAttr == null || string.IsNullOrWhiteSpace(sourceAttr.Value))
                    continue;

                var sourcePath = Path.Combine(outputDir, sourceAttr.Value.Replace('\\', '/'));
                var webpPath = Path.ChangeExtension(sourcePath, ".webp");
                if (File.Exists(webpPath))
                {
                    var oldSource = sourceAttr.Value;
                    sourceAttr.Value = Path.ChangeExtension(oldSource, ".webp").Replace('\\', '/');
                    modified = true;
                    Console.WriteLine($"  更新引用: {oldSource} -> {sourceAttr.Value}");
                }
            }

            if (modified)
                doc.Save(xmlFile);
        }
    }
}
