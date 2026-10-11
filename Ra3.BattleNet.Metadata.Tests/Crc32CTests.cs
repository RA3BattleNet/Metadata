using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class Crc32CTests
{
    /// <summary>标准 CRC-32C 检查值：ASCII "123456789" 应得 0xE3069283。</summary>
    [TestMethod]
    public void Compute_AsciiCheckVector()
    {
        Crc32C.Compute(new MemoryStream(Encoding.ASCII.GetBytes("123456789"))).Should().Be(0xE3069283u);
    }

    [TestMethod]
    public void Compute_EmptyStream_IsZero()
    {
        Crc32C.Compute(new MemoryStream()).Should().Be(0u);
    }

    [TestMethod]
    public void Compute_StreamAndFileAgreeAcrossReadBuffer()
    {
        // 66000 字节跨过内部 64 KiB 读缓冲：尾块必须按实际读到的长度参与计算
        var data = new byte[66000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31) + 7);
        }

        var path = Path.Combine(Path.GetTempPath(), $"crc32c-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, data);
        try
        {
            Crc32C.ComputeFile(path).Should().Be(Crc32C.Compute(new MemoryStream(data)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ComputeFile_AsciiCheckVector()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crc32c-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("123456789"));
        try
        {
            Crc32C.ComputeFile(path).Should().Be(0xE3069283u);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
