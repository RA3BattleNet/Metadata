using System.Security.Cryptography;
using System.Text;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>缓存层用到的摘要工具：一律 SHA-256、小写十六进制、UTF-8。</summary>
internal static class CacheHash
{
    /// <summary>字节的 SHA-256，小写十六进制。</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>UTF-8 字符串的 SHA-256，小写十六进制。</summary>
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>文件的 SHA-256，小写十六进制；流式读取，不把文件读进内存。</summary>
    public static string Sha256HexOfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>判断是否是小写十六进制（长度必填，校验摘要与目录名不会被路径分隔符或大小写绕过）。</summary>
    public static bool IsLowerHex(string? value, int length)
    {
        if (value is null || value.Length != length) return false;
        foreach (var c in value)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>SHA-256 摘要的十六进制长度。</summary>
    public const int Sha256HexLength = 64;
}
