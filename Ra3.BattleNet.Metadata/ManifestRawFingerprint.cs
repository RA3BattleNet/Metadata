namespace Ra3.BattleNet.Metadata;

/// <summary>下载物指纹的共享校验；构建与客户端读取使用同一契约。</summary>
internal static class ManifestRawFingerprint
{
    internal static string? Validate(string? rawHash, string? rawHashAlgorithm, string manifestAlgorithm)
    {
        if (rawHash is null)
        {
            if (rawHashAlgorithm is not null)
                throw new InvalidOperationException("RawHashAlgorithm 必须同时声明 RawHash");
            return null;
        }

        var algorithm = rawHashAlgorithm?.Trim().ToUpperInvariant() ?? manifestAlgorithm;
        if (rawHashAlgorithm is not null && algorithm is not ("SHA256" or "CRC32C"))
            throw new InvalidOperationException("RawHashAlgorithm 仅支持 SHA256/CRC32C");

        var length = algorithm switch
        {
            "SHA256" => 64,
            "CRC32C" => 8,
            "MD5" => 32,
            _ => throw new InvalidOperationException("RawHash 需要有效的内容哈希算法，不能继承 None"),
        };
        if (rawHash.Length != length)
            throw new InvalidOperationException($"RawHash 长度应为 {length}（{algorithm}），实际 {rawHash.Length}");
        if (!rawHash.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"RawHash 必须是十六进制字符串（{algorithm}）");
        return algorithm;
    }
}
