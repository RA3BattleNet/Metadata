using System;
using System.IO;

namespace Ra3.BattleNet.Metadata
{
    internal class Program
    {
        private const string DefaultSrcFolder = "./Metadata";
        private const string DefaultDstFolder = "./Output";

        static void Main(string[] args)
        {
            // 解析命令行参数
            var srcFolder = DefaultSrcFolder;
            var dstFolder = DefaultDstFolder;

            foreach (string arg in args)
            {
                if (arg.StartsWith("--src="))
                {
                    srcFolder = arg.Split('=')[1];
                }
                else if (arg.StartsWith("--dst="))
                {
                    dstFolder = arg.Split('=')[1];
                }
            }

            Console.WriteLine($"工作目录: {Environment.CurrentDirectory}");
            Console.WriteLine($"元数据源目录: {srcFolder}");
            Console.WriteLine($"输出目录: {dstFolder}");
            Console.WriteLine();

            try
            {
                // 创建输出目录
                if (!Directory.Exists(dstFolder))
                {
                    Directory.CreateDirectory(dstFolder);
                }

                // 1. 加载元数据
                string metadataPath = Path.Combine(srcFolder, "metadata.xml");
                Console.WriteLine($"加载元数据: {metadataPath}");
                var metadata = Metadata.LoadFromFile(metadataPath);
                Console.WriteLine($"✓ 成功加载元数据（{metadata.Children.Count} 个子节点）");

                // 2. 替换变量（${ENV:...}, ${MD5::}）
                Console.WriteLine("替换环境变量和计算哈希...");
                metadata.ReplaceVariablesInFile(metadataPath);
                Console.WriteLine("✓ 变量替换完成");

                // 3. 复制所有文件到输出目录
                Console.WriteLine("复制文件到输出目录...");
                CopyFiles(srcFolder, dstFolder);
                Console.WriteLine("✓ 文件复制完成");

                // 4. 验证处理后的元数据
                Console.WriteLine("验证处理后的元数据...");
                var verifiedMetadata = Metadata.LoadFromFile(metadataPath);
                Console.WriteLine($"✓ 验证成功");
                Console.WriteLine();

                // 显示元数据信息
                Console.WriteLine("=== 元数据信息 ===");
                Console.WriteLine($"根节点: {verifiedMetadata.Name}");
                Console.WriteLine($"子节点数: {verifiedMetadata.Children.Count}");

                var commit = verifiedMetadata.Get("Commit");
                if (!string.IsNullOrEmpty(commit))
                {
                    Console.WriteLine($"Commit: {commit}");
                }

                // 显示Include树结构
                Console.WriteLine();
                Console.WriteLine("=== Include 引用树 ===");
                Console.WriteLine(verifiedMetadata.GetIncludeTree());

                Console.WriteLine("处理完成！");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("循环引用"))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"错误: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"处理过程中发生错误: {ex.Message}");
                Console.WriteLine($"详细信息: {ex}");
                Console.ResetColor();
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// 复制源目录的所有文件到目标目录，保持目录结构
        /// </summary>
        private static void CopyFiles(string srcFolder, string dstFolder)
        {
            var files = Directory.GetFiles(srcFolder, "*.*", SearchOption.AllDirectories);
            int copiedCount = 0;

            foreach (string file in files)
            {
                // 计算相对路径
                string relativePath = Path.GetRelativePath(srcFolder, file);
                string targetFilePath = Path.Combine(dstFolder, relativePath);

                // 创建目标目录
                string? targetDir = Path.GetDirectoryName(targetFilePath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // 复制文件
                File.Copy(file, targetFilePath, overwrite: true);
                copiedCount++;
            }

            Console.WriteLine($"  已复制 {copiedCount} 个文件");
        }
    }
}
