using System.Diagnostics;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 编译阶段按开关调用 Imaging CLI（进程隔离，主包不引用 SkiaSharp）。
/// </summary>
public static class ImagingInvoker
{
    /// <summary>
    /// 对已构建的 outputDir 运行 WebP 管线。
    /// </summary>
    /// <returns>进程退出码。</returns>
    public static int RunWebP(string outputDir, TextWriter? log = null)
    {
        log ??= Console.Out;
        outputDir = Path.GetFullPath(outputDir);

        var project = FindImagingProject();
        if (project == null)
        {
            log.WriteLine("错误: 找不到 Ra3.BattleNet.Metadata.Imaging 项目（编译仓库内应存在）。");
            return 1;
        }

        log.WriteLine($">>> Imaging CLI: {project}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                "run",
                "--project", project,
                "--no-launch-profile",
                "--",
                "webp",
                $"--dst={outputDir}"
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 dotnet Imaging 进程");

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) log.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log.WriteLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        return proc.ExitCode;
    }

    public static string? FindImagingProject()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(typeof(ImagingInvoker).Assembly.Location) ?? ""
                 })
        {
            var dir = start;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "Ra3.BattleNet.Metadata.Imaging",
                    "Ra3.BattleNet.Metadata.Imaging.csproj");
                if (File.Exists(candidate))
                    return candidate;

                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }

        return null;
    }
}
