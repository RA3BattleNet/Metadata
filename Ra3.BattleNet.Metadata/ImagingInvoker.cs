using System.Diagnostics;
using System.Text;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 调用 Imaging CLI：单张图片转 WebP，返回 MD5。主包不引用 SkiaSharp。
/// </summary>
public static class ImagingInvoker
{
    private static string? _cachedDll;
    private static readonly object Gate = new();

    /// <summary>
    /// 将 input 转为 WebP 写到 output，返回 hash（小写 hex）。
    /// </summary>
    public static string ConvertToWebP(string inputPath, string outputPath)
    {
        var dll = EnsureImagingDll();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                "exec", dll,
                "convert",
                $"--input={Path.GetFullPath(inputPath)}",
                $"--output={Path.GetFullPath(outputPath)}"
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Imaging CLI");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            throw new InvalidOperationException(
                string.IsNullOrEmpty(err)
                    ? $"Imaging CLI 失败 (exit {proc.ExitCode})"
                    : $"Imaging CLI 失败: {err}");
        }

        var hash = stdout.ToString().Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 32)
            throw new InvalidOperationException($"Imaging CLI 未返回有效 MD5: '{stdout}'");

        return hash.ToLowerInvariant();
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

    private static string EnsureImagingDll()
    {
        lock (Gate)
        {
            if (_cachedDll != null && File.Exists(_cachedDll))
                return _cachedDll;

            var project = FindImagingProject()
                ?? throw new InvalidOperationException(
                    "找不到 Ra3.BattleNet.Metadata.Imaging 项目（仓库编译环境需要）。");

            var projectDir = Path.GetDirectoryName(project)!;
            foreach (var config in new[] { "Debug", "Release" })
            {
                var dll = Path.Combine(projectDir, "bin", config, "net10.0",
                    "Ra3.BattleNet.Metadata.Imaging.dll");
                if (File.Exists(dll))
                {
                    _cachedDll = dll;
                    return dll;
                }
            }

            // 先编译一次再 exec
            var build = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { "build", project, "-c", "Debug", "--nologo", "-v", "q" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = Process.Start(build) ?? throw new InvalidOperationException("无法 build Imaging"))
            {
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("编译 Imaging CLI 失败");
            }

            var built = Path.Combine(projectDir, "bin", "Debug", "net10.0",
                "Ra3.BattleNet.Metadata.Imaging.dll");
            if (!File.Exists(built))
                throw new FileNotFoundException($"Imaging 编译后未找到: {built}");

            _cachedDll = built;
            return built;
        }
    }
}
