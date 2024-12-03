namespace Ra3.BattleNet.Metadata
{
    internal class Program
    {
        public static string srcMetadataFolder = "./Metadata";
        public static string dstOutputFolder = "./Output";
        static void Main(string[] args)
        {

            // Read parameters from build script.
            foreach (string arg in args)
            {
                if (arg.StartsWith("--src="))
                {
                    srcMetadataFolder = arg.Split('=')[1];
                }
                if (arg.StartsWith("--dst="))
                {
                    dstOutputFolder = arg.Split('=')[1];
                }
            }

            if (!Directory.Exists(dstOutputFolder))
            {
                Directory.CreateDirectory(dstOutputFolder);
            }

            Console.WriteLine($"workingDirectory: {Environment.CurrentDirectory}");
            Console.WriteLine($"srcMetadataFolder: {srcMetadataFolder}");
            Console.WriteLine($"dstOutputFolder: {dstOutputFolder}");


            // Copy to output dir
            try
            {
                // 获取源目录下所有的文件
                string[] files = Directory.GetFiles(srcMetadataFolder, "*.*", SearchOption.AllDirectories);

                foreach (string file in files)
                {
                    // 构建目标文件的完整路径
                    string targetFilePath = Path.Combine(dstOutputFolder, file[(srcMetadataFolder.Length + 1)..]);

                    // 确保目标文件所在的目录存在
                    string targetFileDirectory = Path.GetDirectoryName(targetFilePath);
                    if (!Directory.Exists(targetFileDirectory))
                    {
                        Directory.CreateDirectory(targetFileDirectory);
                    }

                    // 复制文件
                    File.Copy(file, targetFilePath, true);  // 第三个参数为true表示如果目标位置已有同名文件，则覆盖
                    Console.WriteLine($"Copy {file} to {targetFilePath}");
                }

                Console.WriteLine("文件复制完成！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发生错误: {ex.Message}");
            }
        }
    }
}
