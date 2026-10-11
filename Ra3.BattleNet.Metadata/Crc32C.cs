namespace Ra3.BattleNet.Metadata;

/// <summary>
/// CRC-32C（Castagnoli，反射多项式 0x82F63B78）流式计算。
/// 与清单 <c>File@Hash</c> 的取值口径一致：算完整文件，结果是 <see cref="uint"/>，
/// 输出成 8 位十六进制大写（如 <c>5140F182</c>），不做字节反转；
/// 客户端 <c>Ra3.Tools.ChecksumCalculator</c> 用 Full 模式算出的就是同一个值。
/// </summary>
public static class Crc32C
{
    private const uint Polynomial = 0x82F63B78u;
    private const int BufferSize = 64 * 1024;

    private static readonly uint[] Table = BuildTable();

    /// <summary>流式计算从当前位置读到结尾的全部字节。</summary>
    public static uint Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var crc = 0xFFFFFFFFu;
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ buffer[i]) & 0xFF];
            }
        }

        return ~crc;
    }

    /// <summary>计算本地文件的完整 CRC-32C；以共享读打开，游戏进程占着文件也能算。</summary>
    public static uint ComputeFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Compute(stream);
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
