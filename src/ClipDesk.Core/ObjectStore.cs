using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using System.Net;

namespace ClipDesk.Core;

public interface IObjectStore : IDisposable
{
    Task Put(string key, byte[] data, CancellationToken ct);
    /// <summary>对象缺失时抛出 <see cref="FileNotFoundException"/>。</summary>
    Task<byte[]> Get(string key, CancellationToken ct);
    /// <summary>返回按对象键序排列的对象与目录。</summary>
    Task<List<string>> List(string prefix, string? after, string? delimiter, CancellationToken ct);
    Task DeleteTestObject(string key, CancellationToken ct);
}

/// <summary>仅使用七牛文档列明的基本接口；不使用对象锁、版本控制或服务端加密。</summary>
public sealed class KodoStore : IObjectStore
{
    private readonly AmazonS3Client client;
    private readonly string bucket;
    private readonly string connectionTestPrefix;
    private readonly string devicePrefix;
    public KodoStore(SyncConfig config, string accessKey, string secretKey, HttpClientFactory? httpClientFactory = null)
    {
        ValidateConfig(config); bucket = config.Bucket;
        connectionTestPrefix = config.Prefix + "_connection-test/";
        devicePrefix = config.Prefix + "v1/devices/";
        var sdk = new AmazonS3Config
        {
            ServiceURL = config.Endpoint.TrimEnd('/'), AuthenticationRegion = config.Region, ForcePathStyle = true,
            Timeout = TimeSpan.FromSeconds(25), MaxErrorRetry = 2,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (httpClientFactory != null) sdk.HttpClientFactory = httpClientFactory;
        client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), sdk);
    }
    public static void ValidateConfig(SyncConfig c)
    {
        if (!Uri.TryCreate(c.Endpoint, UriKind.Absolute, out var u) || u.Scheme != "https" || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || u.AbsolutePath != "/") throw new InvalidDataException("Endpoint 必须是 HTTPS 服务域名，不含路径、查询、片段或账号");
        if (string.IsNullOrWhiteSpace(c.Region) || string.IsNullOrWhiteSpace(c.Bucket) || c.Bucket.Contains('/')) throw new InvalidDataException("请填写 Region 和 S3 空间名");
        if (c.Prefix.Length < 3 || !c.Prefix.EndsWith('/') || c.Prefix.StartsWith('/') || c.Prefix.Contains("..") || c.Prefix.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '/' or '-' or '_'))) throw new InvalidDataException("请设置独立前缀，例如 clipdesk-personal/，仅使用英文字母、数字、横线、下划线和斜线");
    }
    public async Task Put(string key, byte[] data, CancellationToken ct)
    {
        if (data.Length > ObjectLimit(key)) throw new InvalidDataException("本机对象超过上传上限");
        using var stream = new MemoryStream(data, false);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = stream, ContentType = "application/octet-stream", UseChunkEncoding = false, DisableDefaultChecksumValidation = true }, ct);
    }
    public async Task<byte[]> Get(string key, CancellationToken ct)
    {
        using var r = await GetResponse(key, ct);
        var limit = ObjectLimit(key);
        if (r.ContentLength > limit) throw new InvalidDataException("远端对象超过下载上限");
        using var m = new MemoryStream(); var buffer = new byte[65536]; int n;
        while ((n = await r.ResponseStream.ReadAsync(buffer, ct)) > 0)
        { if (m.Length + n > limit) throw new InvalidDataException("远端对象超过下载上限"); await m.WriteAsync(buffer.AsMemory(0, n), ct); }
        return m.ToArray();
    }
    private int ObjectLimit(string key)
    {
        var deviceEnd = devicePrefix.Length + 32;
        return key.StartsWith(devicePrefix, StringComparison.Ordinal) && key.Length > deviceEnd &&
            key.AsSpan(deviceEnd).StartsWith("/events/", StringComparison.Ordinal)
            ? SyncEngine.MaxEventObjectBytes
            : AssetStore.MaxBytes + 4096;
    }
    private async Task<GetObjectResponse> GetResponse(string key, CancellationToken ct)
    {
        try { return await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.ErrorCode != "NoSuchBucket")
        {
            throw new FileNotFoundException("远端对象不存在", key, ex);
        }
    }
    public async Task<List<string>> List(string prefix, string? after, string? delimiter, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        while (true)
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, StartAfter = token == null ? after : null, Delimiter = delimiter, ContinuationToken = token, MaxKeys = 1000, Encoding = null }, ct);
            if (response.S3Objects != null) result.UnionWith(response.S3Objects.Select(o => o.Key));
            if (response.CommonPrefixes != null) result.UnionWith(response.CommonPrefixes);
            if (response.IsTruncated != true) return result.Order(StringComparer.Ordinal).ToList();
            token = response.NextContinuationToken;
            if (string.IsNullOrEmpty(token) || !seenTokens.Add(token)) throw new InvalidDataException("远端对象列表分页标记缺失或重复");
        }
    }
    public async Task DeleteTestObject(string key, CancellationToken ct)
    {
        if (!key.StartsWith(connectionTestPrefix, StringComparison.Ordinal) || !key.EndsWith(".bin", StringComparison.Ordinal) ||
            key.Length != connectionTestPrefix.Length + 36 || !Guid.TryParseExact(key.AsSpan(connectionTestPrefix.Length, 32), "N", out _))
            throw new InvalidOperationException("仅允许删除配置前缀下的连接测试对象");
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, ct);
    }

    public static string DescribeError(Exception ex) => ex switch
    {
        AmazonS3Exception { ErrorCode: "NoSuchBucket" } => "S3 空间名（Bucket）不存在，或 Endpoint 区域与空间所在区域不匹配",
        AmazonS3Exception { ErrorCode: "InvalidAccessKeyId" } => "Access Key (AK) 无效，请核对七牛密钥管理中的 AK",
        AmazonS3Exception { ErrorCode: "SignatureDoesNotMatch" } => "Secret Key (SK) 签名不匹配，请重新填写正确的 SK",
        AmazonS3Exception { ErrorCode: "AccessDenied" } => "访问被拒绝（AccessDenied），请确认该凭据拥有此空间和前缀的读写与列举权限",
        AmazonS3Exception { ErrorCode: "RequestTimeTooSkewed" } => "本机系统时间偏差过大，请校准 Windows 系统时间后重试",
        AmazonS3Exception { ErrorCode: "InvalidBucketName" } => "S3 空间名格式不正确，请填写七牛空间名称",
        AmazonS3Exception s3 when !string.IsNullOrWhiteSpace(s3.ErrorCode) => $"对象存储返回错误（{s3.ErrorCode}）：请核对区域、空间名与凭据权限",
        HttpRequestException or AmazonServiceException { InnerException: HttpRequestException } => "网络连接失败，请检查 Endpoint 域名与当前网络",
        InvalidDataException data => data.Message,
        _ => "请核对区域、S3 空间名、凭据权限及网络"
    };

    public void Dispose() => client.Dispose();
}
