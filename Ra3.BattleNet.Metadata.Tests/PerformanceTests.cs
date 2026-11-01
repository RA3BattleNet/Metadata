using System.Diagnostics;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 源树读取性能保护：<see cref="MetadataBuilder.LoadSource"/> 是编辑器与客户端的整仓入口，
/// 展平/继承/ID 限定的实现改动不应该把它从毫秒级拖到百毫秒级。
/// 计时用 <see cref="Stopwatch"/>，只断言上界；不设下界——更快只可能是好事，且下界在 CI 上必然抖动。
/// </summary>
[TestClass]
public class PerformanceTests
{
    /// <summary>
    /// 上界 500 ms：本机基线约 28 ms（Debug 构型、整仓 661 节点），留约 18 倍余量吸收 CI 抖动；
    /// 真退化到百毫秒级会被这里拦下。下界不设，理由见类型注释。
    /// </summary>
    private const double MaxElapsedMilliseconds = 500;

    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string RepoMetadataDir => Path.Combine(RepoRoot, "Metadata");

    [TestMethod]
    public void LoadSource_WholeRepository_StaysUnderTimeBudget()
    {
        var src = RepoMetadataDir;
        var entry = Path.Combine(src, "metadata.xml");

        // 预热一次：JIT、程序集加载、文件系统缓存都不应计入被测耗时。
        MetadataBuilder.LoadSource(entry, src).Should().NotBeNull();

        var stopwatch = Stopwatch.StartNew();
        var metadata = MetadataBuilder.LoadSource(entry, src);
        stopwatch.Stop();

        var elapsed = stopwatch.Elapsed.TotalMilliseconds;
        Console.WriteLine(
            $"LoadSource 整仓源树读取: {elapsed:F1} ms（上界 {MaxElapsedMilliseconds:F0} ms，基线约 28 ms）");

        metadata.Should().NotBeNull();
        metadata.Mods().Should().NotBeEmpty("整仓源树必须能读出 Mod");
        elapsed.Should().BeLessThan(MaxElapsedMilliseconds,
            $"整仓源树读取应在 {MaxElapsedMilliseconds:F0} ms 以内（本机基线约 28 ms）");
    }
}
