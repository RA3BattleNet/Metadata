using System.Xml.Linq;
using SkiaSharp;

namespace Ra3.BattleNet.Metadata.Imaging;

/// <summary>
/// 发布可选步骤：将 Output 中本地图片转为 WebP 并改写 metadata.xml 的 Image Source。
/// 主库不引用本项目；仅 build.sh --webp / npm run build:webp 使用。
/// </summary>
internal static class Program
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif"
    };

    private static int Main(string[] args)
    {
        var outputDir = args.FirstOrDefault(a => a.StartsWith("--dst=", StringComparison.Ordinal))
            ?["--dst=".Length..] ?? "./Output";
        outputDir = Path.GetFullPath(outputDir);
        if (!Directory.Exists(outputDir))
        {
            Console.Error.WriteLine($"输出目录不存在: {outputDir}");
            return 1;
        }

        var flat = Path.Combine(outputDir, "metadata.xml");
        if (!File.Exists(flat))
        {
            Console.Error.WriteLine($"找不到展平文件: {flat}");
            return 1;
        }

        var doc = XDocument.Load(flat);
        var converted = 0;
        foreach (var image in doc.Descendants("Image"))
        {
            var sourceAttr = image.Attribute("Source");
            if (sourceAttr == null || string.IsNullOrWhiteSpace(sourceAttr.Value))
                continue;
            if (!string.IsNullOrWhiteSpace(image.Attribute("Url")?.Value))
                continue;

            var sourcePath = Path.Combine(outputDir, sourceAttr.Value.Replace('\\', '/'));
            if (!File.Exists(sourcePath))
                continue;
            var ext = Path.GetExtension(sourcePath);
            if (!Supported.Contains(ext))
                continue;

            var webpPath = Path.ChangeExtension(sourcePath, ".webp");
            if (!ConvertToWebP(sourcePath, webpPath))
                continue;

            sourceAttr.Value = Path.ChangeExtension(sourceAttr.Value, ".webp")!.Replace('\\', '/');
            converted++;
            Console.WriteLine($"  WebP: {sourceAttr.Value}");
        }

        doc.Save(flat);
        Console.WriteLine($"Imaging 完成，转换 {converted} 张图片");
        return 0;
    }

    private static bool ConvertToWebP(string sourcePath, string webpPath)
    {
        try
        {
            using var input = File.OpenRead(sourcePath);
            using var codec = SKCodec.Create(input);
            if (codec == null) return false;
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap == null) return false;
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
            using var output = File.Open(webpPath, FileMode.Create, FileAccess.Write);
            data.SaveTo(output);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  转换失败 {sourcePath}: {ex.Message}");
            return false;
        }
    }
}
