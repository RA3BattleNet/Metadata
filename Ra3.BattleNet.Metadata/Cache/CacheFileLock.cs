namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 跨进程独占文件租约。刷新与清理都拿它，避免两个进程同时改当前指针。
///
/// 用 <see cref="FileShare.None"/> 打开一个固定文件即可：进程崩了操作系统会自己放锁，
/// 所以不需要（也不该）删除锁文件来"解锁"——删文件反而会制造两个持有者。
/// </summary>
internal sealed class CacheFileLock : IDisposable
{
    private readonly FileStream _stream;

    private CacheFileLock(FileStream stream) => _stream = stream;

    public static CacheFileLock? TryAcquire(string path, TimeSpan timeout, CancellationToken token = default) =>
        TryAcquireAsync(path, timeout, token).GetAwaiter().GetResult();

    public static async Task<CacheFileLock?> TryAcquireAsync(string path, TimeSpan timeout, CancellationToken token = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return new CacheFileLock(stream);
            }
            catch (IOException)
            {
                // 别人占着：等一小会儿再看
            }
            catch (UnauthorizedAccessException)
            {
                // 同上：权限问题也按"拿不到"处理，由调用方决定降级行为
            }

            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _stream.Dispose();
}
