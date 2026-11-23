using System.Security.Cryptography;
using System.Text;

namespace ClipDesk.Core;

/// <summary>使用 .NET 的 AES-256-GCM；格式版本、对象路径作为认证附加数据。</summary>
public sealed class SyncCrypto
{
    internal const int Overhead = 1 + 12 + 16;
    private readonly byte[] key;
    public SyncCrypto(byte[] key)
    { if (key.Length != 32) throw new InvalidDataException("同步密钥必须为 32 字节"); this.key = [.. key]; }
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(32);
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(key))[..16];
    public string AssetName(string id) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("asset:" + id)));
    public byte[] Encrypt(byte[] data, string path)
    {
        var output = new byte[Overhead + data.Length]; output[0] = 1;
        RandomNumberGenerator.Fill(output.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(1, 12), data, output.AsSpan(Overhead), output.AsSpan(13, 16), Encoding.UTF8.GetBytes("ClipDesk/v1/" + path));
        return output;
    }
    public byte[] Decrypt(byte[] data, string path)
    {
        if (data.Length < Overhead || data[0] != 1) throw new InvalidDataException("加密文件格式不受支持");
        var output = new byte[data.Length - Overhead]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(1, 12), data.AsSpan(Overhead), data.AsSpan(13, 16), output, Encoding.UTF8.GetBytes("ClipDesk/v1/" + path));
        return output;
    }
}
