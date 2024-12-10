using System;
using System.IO;

namespace Ra3.BattleNet.Metadata
{
    internal class Program
    {
        private const string DefaultSrcFolder = "./Metadata";
        private const string DefaultDstFolder = "./Output";
        
        public static string srcMetadataFolder = DefaultSrcFolder;
        public static string dstOutputFolder = DefaultDstFolder;

        private static string GetArgValue(string arg, string defaultValue)
        {
            var parts = arg.Split('=');
            return parts.Length > 1 ? parts[1] : defaultValue;
        }

        static void Main(string[] args)
        {
            // 读取构建脚本中的参数
            foreach (string arg in args)
            {
                if (arg.StartsWith("--src="))
                {
                    srcMetadataFolder = GetArgValue(arg, DefaultSrcFolder);
                }
                if (arg.StartsWith("--dst="))
                {
                    dstOutputFolder = GetArgValue(arg, DefaultDstFolder);
                }
            }

            // 如果目标文件夹不存在，则创建它
            if (!Directory.Exists(dstOutputFolder))
            {
                Directory.CreateDirectory(dstOutputFolder ?? DefaultDstFolder);
            }

            Console.WriteLine($"工作目录: {Environment.CurrentDirectory}");
            Console.WriteLine($"元数据源目录: {srcMetadataFolder}");
            Console.WriteLine($"输出目录: {dstOutputFolder}");

            try
            {
                // 1. 加载并编译metadata.xml
                string metadataPath = Path.Combine(srcMetadataFolder, "metadata.xml");
                var metadata = Metadata.LoadFromFile(metadataPath);
                var compiledDoc = metadata.Compile();

                // 2. 保存编译后的文件
                string compiledPath = Path.Combine(dstOutputFolder ?? DefaultDstFolder, "metadata.compiled.xml");
                compiledDoc.Save(compiledPath);
                Console.WriteLine($"已生成编译后的元数据文件: {compiledPath}");

                // 3. 复制所有其他文件到输出目录
                string[] files = Directory.GetFiles(srcMetadataFolder, "*.*", SearchOption.AllDirectories);
                foreach (string file in files)
                {
                    if (file.EndsWith("metadata.xml")) continue; // 已处理

                    string targetFilePath = Path.Combine(dstOutputFolder ?? DefaultDstFolder, 
                        file[(srcMetadataFolder.Length + 1)..]);

                    string targetDir = Path.GetDirectoryName(targetFilePath) ?? throw new InvalidOperationException("无法确定目标目录");
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.Copy(file, targetFilePath, true);
                    Console.WriteLine($"复制 {file} 到 {targetFilePath}");
                }

                Console.WriteLine("文件处理完成！");

                // 4. 验证编译后的文件
                try
                {
                    var verifiedMetadata = Metadata.LoadFromFile(compiledPath);
                    Console.WriteLine($"成功验证编译后的元数据");
                    Console.WriteLine($"根节点: {verifiedMetadata.Name}");
                    Console.WriteLine($"包含 {verifiedMetadata.Children.Count} 个子节点");

                    // 示例查询
                    string commit = verifiedMetadata.Get("Commit") ?? "Unknown";
                    Console.WriteLine($"Commit: {commit}");

                    // 打印所有模块信息
                    PrintModuleInfo(verifiedMetadata, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"验证编译后的元数据时发生错误: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理过程中发生错误: {ex.Message}");
            }
        }

        private static void PrintModuleInfo(Metadata metadata, int indentLevel)
        {
            string indent = new string(' ', indentLevel * 2);
            string moduleName = metadata.Get("Name") ?? "Unknown";
            Console.WriteLine($"{indent}模块: {moduleName}");

            // 打印变量
            foreach (var var in metadata.Variables)
            {
                Console.WriteLine($"{indent}  {var.Key}: {var.Value}");
            }

            // 递归打印子模块
            foreach (var child in metadata.Children)
            {
                PrintModuleInfo(child, indentLevel + 1);
            }
        }
    }
}
