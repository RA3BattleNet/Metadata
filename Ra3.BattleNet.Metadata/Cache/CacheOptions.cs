namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 运行期本地缓存的输入。宿主决定缓存目录、超时、体积与并发上限；库不硬编码任何宿主路径，
/// 也不改系统代理或证书策略（宿主注入 <see cref="HttpClient"/> 就用它，没注入才自建一个默认的）。
/// </summary>
public sealed class CacheOptions
{
    /// <summary>发布物入口地址（<c>metadata.xml</c>）。<c>http/https</c> 是正常来源，<c>file://</c> 只用于宿主指定的本机开发入口。</summary>
    public required Uri EntryUri { get; init; }

    /// <summary>缓存根目录（绝对路径）。一个根固定绑定一个入口来源，见 <see cref="CacheOrigin"/>。</summary>
    public required string CacheRoot { get; init; }

    /// <summary>可选注入口；为空时库自建，且不修改默认代理与证书策略。</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>可选时钟；测试用它固定时间。</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>可选日志出口（库不引用任何日志框架）。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>根请求总时限（建议值，需按真实发布物实测后固定）。</summary>
    public TimeSpan RootRequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>一次启动内的自动重试次数；只用于可恢复的网络问题。</summary>
    public int RefreshRetryCount { get; init; } = 2;

    /// <summary>资源并发上限。</summary>
    public int ResourceConcurrency { get; init; } = 4;

    /// <summary>根 XML 体积上限。</summary>
    public long RootMaxBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>叶子清单体积上限。</summary>
    public long LeafMaxBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>单个媒体资源体积上限。</summary>
    public long MediaMaxBytes { get; init; } = 32L * 1024 * 1024;

    internal void Validate()
    {
        if (EntryUri is null) throw new ArgumentException("EntryUri 不能为空", nameof(EntryUri));
        if (!EntryUri.IsAbsoluteUri) throw new ArgumentException("EntryUri 必须是绝对地址", nameof(EntryUri));
        if (EntryUri.Scheme != Uri.UriSchemeHttp && EntryUri.Scheme != Uri.UriSchemeHttps && !EntryUri.IsFile)
            throw new ArgumentException($"不支持的入口协议: {EntryUri.Scheme}", nameof(EntryUri));
        if (string.IsNullOrWhiteSpace(CacheRoot) || !Path.IsPathRooted(CacheRoot))
            throw new ArgumentException("CacheRoot 必须是绝对路径", nameof(CacheRoot));
        if (RootMaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(RootMaxBytes));
        if (LeafMaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(LeafMaxBytes));
        if (MediaMaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MediaMaxBytes));
        if (RefreshRetryCount < 0) throw new ArgumentOutOfRangeException(nameof(RefreshRetryCount));
        if (ResourceConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(ResourceConcurrency));
        if (RootRequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RootRequestTimeout));
    }
}
