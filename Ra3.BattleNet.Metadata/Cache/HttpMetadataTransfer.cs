using System.Globalization;
using System.Net;
using System.Net.Http;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// <see cref="IMetadataTransfer"/> 的默认实现：用 <see cref="HttpClient"/> 把正文下载到本地临时文件。
/// 只在库自用、测试或调用方没有注入传输时生效。
/// 语义与旧的内联 HttpClient 路径一致：条件请求带 <c>If-None-Match</c>/<c>If-Modified-Since</c>，
/// 304 原样回报，非 200 不落盘。
/// </summary>
internal sealed class HttpMetadataTransfer : IMetadataTransfer
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public HttpMetadataTransfer(HttpClient? httpClient, TimeSpan requestTimeout)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = requestTimeout };
    }

    public async Task<MetadataTransferResult> FetchAsync(MetadataTransferRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Source);
        if (!string.IsNullOrWhiteSpace(request.ETag))
            message.Headers.TryAddWithoutValidation("If-None-Match", request.ETag);
        if (DateTimeOffset.TryParse(
                request.LastModified,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var modified))
        {
            message.Headers.IfModifiedSince = modified;
        }

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new MetadataTransferResult(
                (int)response.StatusCode,
                Header(response, "ETag"),
                Header(response, "Last-Modified"),
                response.Content.Headers.ContentType?.MediaType);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.StagingPath)!);
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var staged = new FileStream(
            request.StagingPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true))
        {
            await body.CopyToAsync(staged, cancellationToken).ConfigureAwait(false);
        }

        return new MetadataTransferResult(
            (int)response.StatusCode,
            Header(response, "ETag"),
            Header(response, "Last-Modified"),
            response.Content.Headers.ContentType?.MediaType);
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();
        return response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
