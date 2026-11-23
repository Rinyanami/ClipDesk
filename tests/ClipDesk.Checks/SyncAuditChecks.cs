using Amazon.Runtime;
using ClipDesk.Core;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

internal static class SyncAuditChecks
{
    private const string Prefix = "audit/";
    private const string Devices = Prefix + "v1/devices/";

    public static async Task Run(string root, Action<string, bool> check)
    {
        await Registrations(root, check);
        await IsolatedRecovery(root, check);
        await MissingAssets(root, check);
        await LiveCopyFreshness(root, check);
        await LostUploadConfirmation(root, check);
        await Pagination(check);
        await ObjectSizeLimits(check);
    }

    private static async Task Registrations(string root, Action<string, bool> check)
    {
        using var writer = new Store(Path.Combine(root, "audit-registration-writer"));
        using var reader = new Store(Path.Combine(root, "audit-registration-reader"));
        using var remote = new ObjectMemory();
        var crypto = new SyncCrypto(SyncCrypto.GenerateKey());
        await Engine(writer, remote, crypto).Run();
        var sync = Engine(reader, remote, crypto);
        await sync.Run();
        var pending = reader.Save(new Entry { Body = "等待上传的内容" });
        var puts = remote.Puts;
        var foreign = Codec.Id();
        var registration = Registration(foreign);
        var wrong = new SyncCrypto(SyncCrypto.GenerateKey());
        remote.Add(registration, new { protocol = 1, device = foreign }, wrong);
        check("持续同步遇到新异密钥设备仍拒绝写入",
            await Fails<CryptographicException>(() => sync.Run()) && remote.Puts == puts && reader.PendingCount == 1);

        remote.Add(registration, new { protocol = 1, device = writer.DeviceId }, crypto);
        check("注册信息已认证仍需匹配路径设备",
            await Fails<InvalidDataException>(() => sync.Run()) && remote.Puts == puts);
        remote.Objects.Remove(registration);

        remote.Objects.Remove(Registration(reader.DeviceId));
        await Engine(reader, remote, crypto).Run();
        await Engine(writer, remote, crypto).Run();
        check("重启恢复丢失的本机注册并继续上传",
            remote.Objects.ContainsKey(Registration(reader.DeviceId)) && writer.Get(pending.Id) != null);
    }

    private static async Task IsolatedRecovery(string root, Action<string, bool> check)
    {
        using var damaged = new Store(Path.Combine(root, "audit-damaged-writer"));
        using var healthy = new Store(Path.Combine(root, "audit-healthy-writer"));
        using var reader = new Store(Path.Combine(root, "audit-isolation-reader"));
        using var remote = new ObjectMemory();
        var crypto = new SyncCrypto(SyncCrypto.GenerateKey());
        var first = damaged.Save(new Entry { Body = "将暂时缺失的第一条变更" });
        var second = damaged.Save(new Entry { Body = "必须等待第一条变更" });
        var other = healthy.Save(new Entry { Body = "独立设备可下载的变更" });
        await Engine(damaged, remote, crypto).Run();
        await Engine(healthy, remote, crypto).Run();
        var key = Event(damaged.DeviceId, 1);
        var original = remote.Objects[key];
        remote.Objects.Remove(key);

        var sync = Engine(reader, remote, crypto);
        check("日志间断只阻塞相应写入设备",
            await Fails<InvalidDataException>(() => sync.Run()) &&
            reader.Cursor(damaged.DeviceId) == 0 && reader.Get(second.Id) == null &&
            reader.Cursor(healthy.DeviceId) == 1 && reader.Get(other.Id) != null);
        var damagedBytes = original.ToArray();
        damagedBytes[^1] ^= 1;
        remote.Objects[key] = damagedBytes;
        check("损坏密文保留游标",
            await Fails<CryptographicException>(() => sync.Run()) && reader.Cursor(damaged.DeviceId) == 0);

        remote.Objects[key] = original;
        await sync.Run();
        check("缺损日志补齐后继续按顺序接收",
            reader.Cursor(damaged.DeviceId) == 2 && reader.Get(first.Id) != null && reader.Get(second.Id) != null);
    }

    private static async Task MissingAssets(string root, Action<string, bool> check)
    {
        using var writer = new Store(Path.Combine(root, "audit-asset-writer"));
        using var reader = new Store(Path.Combine(root, "audit-asset-reader"));
        using var remote = new ObjectMemory();
        var crypto = new SyncCrypto(SyncCrypto.GenerateKey());
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9XcAAAAASUVORK5CYII=");
        var missing = writer.Assets.Put(png);
        var available = writer.Assets.Put([.. png, 0]);
        var absentEntry = writer.Save(new Entry { Format = "image", Assets = [missing] });
        writer.Save(new Entry { Format = "image", Assets = [available] });
        await Engine(writer, remote, crypto).Run();

        var assetKey = Prefix + "v1/assets/" + crypto.AssetName(missing) + ".bin";
        var bytes = remote.Objects[assetKey];
        remote.Objects.Remove(assetKey);
        var sync = Engine(reader, remote, crypto);
        check("缺图保留事件并独立下载其他附件",
            await Fails<FileNotFoundException>(() => sync.Run()) &&
            reader.Cursor(writer.DeviceId) == 2 && reader.Get(absentEntry.Id) != null &&
            !reader.Assets.Exists(missing) && reader.Assets.Exists(available));
        remote.Objects[assetKey] = bytes;
        var result = await Engine(reader, remote, crypto).Run();
        check("重启从已提交引用补图，无需重读日志",
            result is { Received: 0, Downloaded: 1 } && reader.Assets.Read(missing).SequenceEqual(png));
    }

    private static async Task LiveCopyFreshness(string root, Action<string, bool> check)
    {
        using var writer = new Store(Path.Combine(root, "audit-live-writer"));
        using var reader = new Store(Path.Combine(root, "audit-live-reader"));
        using var remote = new ObjectMemory();
        var crypto = new SyncCrypto(SyncCrypto.GenerateKey());
        var clock = new TestClock();
        await Engine(writer, remote, crypto, clock).Run();
        var sync = Engine(reader, remote, crypto, clock);
        var live = new List<Change>();
        sync.LiveCopy += live.Add;
        await sync.Run();

        Change Make(string device, long sequence, string body, string action = "capture") => new()
        {
            DeviceId = device, Sequence = sequence, Time = clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Clock = new() { [device] = sequence }, Action = action, Value = new Entry { Body = body }
        };
        void Publish(long sequence, Change change) =>
            remote.Add(Event(writer.DeviceId, sequence), new Packet(1, writer.DeviceId, sequence, change, true), crypto);

        var legacy = Make(writer.DeviceId, 1, "旧版日志");
        remote.Add(Event(writer.DeviceId, 1), new { protocol = 1, writer = writer.DeviceId, sequence = 1, change = legacy }, crypto);
        var republished = Make(Codec.Id(), 1, "其他设备的备份");
        Publish(2, republished);
        await sync.Run();
        check("旧协议包和其他来源重发仅收录历史",
            live.Count == 0 && reader.Get(legacy.Value.Id) != null && reader.Get(republished.Value.Id) != null);

        Publish(3, Make(writer.DeviceId, 2, "完成本轮时已过期的复制"));
        Publish(4, Make(writer.DeviceId, 3, "此对象使轮询变长", "save"));
        remote.OnGet = key => { if (key == Event(writer.DeviceId, 4)) clock.Advance(TimeSpan.FromSeconds(31)); };
        await sync.Run();
        remote.OnGet = null;
        check("长轮询完成时重新校验剪贴板事件时效", live.Count == 0);

        var removed = Make(writer.DeviceId, 4, "同轮随后删除的复制");
        Publish(5, removed);
        Publish(6, Make(writer.DeviceId, 5, "删除", "delete") with { Value = removed.Value with { Deleted = true } });
        await sync.Run();
        check("同轮随后删除的历史不会覆盖当前剪贴板", live.Count == 0 && reader.Get(removed.Value.Id)?.Deleted == true);

        var fresh = Make(writer.DeviceId, 6, "来自已知设备的真正新复制");
        Publish(7, fresh);
        await sync.Run();
        check("已知设备新复制仍可传播", live.Count == 1 && live[0].EventId == fresh.EventId);
    }

    private static async Task LostUploadConfirmation(string root, Action<string, bool> check)
    {
        using var writer = new Store(Path.Combine(root, "audit-upload-writer"));
        using var reader = new Store(Path.Combine(root, "audit-upload-reader"));
        using var remote = new ObjectMemory();
        var crypto = new SyncCrypto(SyncCrypto.GenerateKey());
        var source = Engine(writer, remote, crypto);
        await source.Run();
        var target = Engine(reader, remote, crypto);
        await target.Run();
        var live = 0;
        target.LiveCopy += _ => live++;

        var first = writer.Save(new Entry { Body = "发送端失败后补发的近期复制" }, "capture");
        remote.FailPut = Event(writer.DeviceId, 1);
        if (!await Fails<IOException>(() => source.Run())) throw new InvalidOperationException("未触发模拟上传故障");
        remote.FailPut = null;
        await source.Run();
        await target.Run();
        check("发送端重试近期积压只同步历史", reader.Get(first.Id) != null && live == 0);

        writer.Save(new Entry { Body = "正常发送且确认丢失的复制" }, "capture");
        var secondKey = Event(writer.DeviceId, 2);
        remote.LostReply = secondKey;
        if (!await Fails<IOException>(() => source.Run())) throw new InvalidOperationException("未触发模拟上传确认丢失");
        var original = remote.Objects[secondKey];
        await Engine(writer, remote, crypto).Run();
        var packet = Codec.Read<Packet>(Encoding.UTF8.GetString(crypto.Decrypt(original, secondKey)));
        check("重启校验已提交日志，不重写密文和首次发送资格",
            packet.LiveEligible && writer.PendingCount == 0 && remote.Objects[secondKey].SequenceEqual(original));
    }

    private static async Task Pagination(Action<string, bool> check)
    {
        const string token = "next/value";
        var pages = new ListFactory(Page(true, token, "audit/b"), Page(false, null, "audit/a", "audit/b"));
        using (var store = OpenStore(pages))
        {
            var result = await store.List("audit/", "audit/previous", null, default);
            check("分页续请求使用令牌并去重排序",
                result.SequenceEqual(["audit/a", "audit/b"]) && pages.Requests.Count == 2 &&
                pages.Requests[0].Query.Contains("start-after=", StringComparison.Ordinal) &&
                !pages.Requests[1].Query.Contains("start-after=", StringComparison.Ordinal) &&
                pages.Requests[1].Query.Contains("continuation-token=", StringComparison.Ordinal));
            check("连接测试禁止删除其他前缀的对象",
                await Fails<InvalidOperationException>(() => store.DeleteTestObject("other/_connection-test/" + Codec.Id() + ".bin", default)));
        }

        var broken = new ListFactory(Page(true, null));
        using (var store = OpenStore(broken))
            check("分页截断且缺少令牌时拒绝结果",
                await Fails<InvalidDataException>(() => store.List("audit/", null, null, default)));

        var cycling = new ListFactory(Page(true, token), Page(true, token));
        using (var store = OpenStore(cycling))
            check("分页重复令牌立即停止",
                await Fails<InvalidDataException>(() => store.List("audit/", null, null, default)) && cycling.Requests.Count == 2);

        check("端点拒绝 URL 片段",
            await Fails<InvalidDataException>(() =>
            {
                KodoStore.ValidateConfig(new("https://s3.cn-east-1.qiniucs.com/#fragment", "cn-east-1", "test-space", Prefix));
                return Task.CompletedTask;
            }));
    }

    private static async Task ObjectSizeLimits(Action<string, bool> check)
    {
        const int overLimit = 64 * 1024 * 1024 + 1;
        var wire = new ListFactory("body not read") { ContentLength = overLimit };
        using var store = OpenStore(wire);
        var key = Event(Codec.Id(), 1);
        check("超限日志上传不发出请求",
            await Fails<InvalidDataException>(() => store.Put(key, new byte[overLimit], default)) && wire.Requests.Count == 0);
        check("超限日志下载拒绝服务端声明的长度",
            await Fails<InvalidDataException>(() => store.Get(key, default)) && wire.Requests.Count == 1);
    }

    private static async Task<bool> Fails<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return true; }
        return false;
    }

    private static KodoStore OpenStore(ListFactory factory) =>
        new(new("https://s3.cn-east-1.qiniucs.com", "cn-east-1", "test-space", Prefix), "test-access", "test-secret", factory);
    private static string Page(bool truncated, string? token, params string[] keys) =>
        "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>test-space</Name><MaxKeys>1000</MaxKeys>" +
        "<IsTruncated>" + (truncated ? "true" : "false") + "</IsTruncated>" +
        (token == null ? "" : "<NextContinuationToken>" + token + "</NextContinuationToken>") +
        string.Concat(keys.Select(key => "<Contents><Key>" + key + "</Key><Size>1</Size></Contents>")) + "</ListBucketResult>";
    private static string Registration(string device) => Devices + device + "/device.bin";
    private static string Event(string device, long sequence) => Devices + device + "/events/" + sequence.ToString("D20", CultureInfo.InvariantCulture) + ".bin";
    private static SyncEngine Engine(Store store, ObjectMemory remote, SyncCrypto crypto, TimeProvider? clock = null) =>
        new(store, remote, crypto, Prefix, clock);

    private sealed class ObjectMemory : IObjectStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public int Puts { get; private set; }
        public string? FailPut { get; set; }
        public string? LostReply { get; set; }
        public Action<string>? OnGet { get; set; }

        public void Add<T>(string key, T payload, SyncCrypto crypto) =>
            Objects[key] = crypto.Encrypt(Encoding.UTF8.GetBytes(Codec.Json(payload)), key);

        public Task Put(string key, byte[] data, CancellationToken ct)
        {
            Puts++;
            if (key == FailPut) throw new IOException("模拟请求未到达");
            Objects[key] = data.ToArray();
            if (key == LostReply) throw new IOException("模拟对象已提交但确认丢失");
            return Task.CompletedTask;
        }

        public Task<byte[]> Get(string key, CancellationToken ct)
        {
            OnGet?.Invoke(key);
            if (!Objects.TryGetValue(key, out var bytes)) throw new FileNotFoundException("模拟远端缺失对象", key);
            return Task.FromResult(bytes.ToArray());
        }

        public Task<List<string>> List(string prefix, string? after, string? delimiter, CancellationToken ct)
        {
            var keys = Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal));
            if (delimiter != null)
                keys = keys.Select(key => key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal) is var end && end >= 0 ? key[..(end + 1)] : key);
            return Task.FromResult(keys.Where(key => after == null || string.CompareOrdinal(key, after) > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
        }

        public Task DeleteTestObject(string key, CancellationToken ct) { Objects.Remove(key); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) { now += duration; ticks += duration.Ticks; }
    }

    private sealed class ListFactory(params string[] pages) : HttpClientFactory
    {
        public List<Uri> Requests { get; } = [];
        public long? ContentLength { get; init; }
        private readonly Queue<string> responses = new(pages);
        public override HttpClient CreateHttpClient(IClientConfig config) => new(new ListHandler(Requests, responses, ContentLength));
    }

    private sealed class ListHandler(List<Uri> requests, Queue<string> responses, long? contentLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            requests.Add(request.RequestUri!);
            var content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/xml");
            if (contentLength != null) content.Headers.ContentLength = contentLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }
}
