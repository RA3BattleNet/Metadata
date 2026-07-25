using System.Security.Cryptography;
using SkiaSharp;

namespace Ra3.BattleNet.Metadata.Imaging;

/// <summary>
/// 纯图片处理：转 WebP 并计算输出文件 MD5。不读 XML。
/// </summary>
public static class ImageConverter
{
    private static readonly HashSet<string> Convertible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif"
    };

    /// <summary>
    /// 将 input 转为 WebP 写入 output，返回 output 的 MD5（小写 hex）。
    /// </summary>
    public static string ConvertToWebP(string inputPath, string outputPath)
    {
        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"输入图片不存在: {inputPath}");

        var ext = Path.GetExtension(inputPath);
        if (!Convertible.Contains(ext))
            throw new InvalidOperationException($"不支持的输入格式: {ext}");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var input = File.OpenRead(inputPath);
        using var codec = SKCodec.Create(input)
            ?? throw new InvalidOperationException($"无法解码: {inputPath}");
        using var bitmap = SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException($"无法解码位图: {inputPath}");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
        using (var output = File.Open(outputPath, FileMode.Create, FileAccess.Write))
            data.SaveTo(output);

        return ComputeMd5(outputPath);
    }

    public static string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }

    public static bool IsConvertible(string path) =>
        Convertible.Contains(Path.GetExtension(path));
}
