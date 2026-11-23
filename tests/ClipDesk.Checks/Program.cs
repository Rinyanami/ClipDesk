using ClipDesk.Core;
using System.Security.Cryptography;
using System.Text;

var root = args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "clipdesk-checks-" + Codec.Id());
Directory.CreateDirectory(root);
var checks = new List<object>();
void Check(string name, bool ok) { checks.Add(new { name, ok }); Console.WriteLine((ok ? "通过：" : "失败：") + name); if (!ok) throw new Exception(name); }
async Task MustFail(string name, Func<Task> action) { bool failed = false; try { await action(); } catch { failed = true; } Check(name, failed); }
try
{
    using var a = new Store(Path.Combine(root, "a")); using var b = new Store(Path.Combine(root, "b"));
    var key = SyncCrypto.GenerateKey(); var crypto = new SyncCrypto(key); var remote = new MemoryRemote(crypto);
    var syncA = new SyncEngine(a, remote, crypto, "test/"); var syncB = new SyncEngine(b, remote, crypto, "test/");
    var e = a.Save(new Entry { Body = "原始正文\n第二行", Protected = true });
    Check("保存和同步队列同时落地", a.Get(e.Id) != null && a.PendingCount == 1);
    a.Save(e with { Pinned = true, Body = "手动编辑" });
    Check("编辑保留 ID、创建时间和置顶", a.Get(e.Id) is { Pinned: true } edited && edited.CreatedAt == e.CreatedAt);
    await syncA.Run(); await syncB.Run();
    Check("增量同步落地", b.Get(e.Id)?.Body == "手动编辑");
    var aBase = a.Clock(e.Id); var bBase = b.Clock(e.Id);
    a.Save(a.Get(e.Id)! with { Body = "电脑甲版本" }, baseClock: aBase);
    b.Save(b.Get(e.Id)! with { Body = "电脑乙版本" }, baseClock: bBase);
    await syncA.Run(); await syncB.Run(); await syncA.Run();
    Check("并发正文保留双方版本", a.Versions(e.Id).Count == 2 && b.Versions(e.Id).Count == 2);
    Check("并发版本结果收敛", a.Get(e.Id)?.Body == b.Get(e.Id)?.Body);
    var stale = b.Get(e.Id)!; var staleClock = b.Clock(e.Id);
    a.Delete(e.Id); await syncA.Run();
    b.Save(stale with { Body = "离线旧设备写回" }, baseClock: staleClock); await syncB.Run(); await syncA.Run();
    Check("离线旧编辑不能复活删除", a.Get(e.Id)!.Deleted && b.Get(e.Id)!.Deleted);
    b.Restore(e.Id); await syncB.Run(); await syncA.Run();
    Check("显式恢复可以同步", !a.Get(e.Id)!.Deleted && !b.Get(e.Id)!.Deleted);
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9XcAAAAASUVORK5CYII=");
    var asset = a.Assets.Put(png); var picture = a.Save(new Entry { Format = "image", Assets = [asset] });
    var memo = a.Convert(picture, "memo", "convert-memo"); var same = a.Convert(picture, "memo", "convert-memo");
    Check("重复转换幂等", memo.Id == same.Id);
    var todo = a.Convert(a.Get(e.Id)!, "todo", "convert-todo"); a.Save(todo with { Done = true }); a.Save(a.Get(e.Id)! with { Body = "来源再次修改" });
    Check("任务内容和完成状态独立", a.Get(todo.Id)!.Done && a.Get(todo.Id)!.Body == todo.Body);
    a.Delete(picture.Id); Check("删除原剪贴不破坏备忘附件", a.Assets.Exists(a.Get(memo.Id)!.Assets[0]));
    a.Draft("memo-draft", new(memo with { Body = "未保存的草稿" }, a.Clock(memo.Id)));
    await syncA.Run(); await syncB.Run();
    Check("图片上传先于引用发布", remote.ImageBeforeReference);
    Check("图片下载字节一致", b.Assets.Read(asset).SequenceEqual(png));
    b.Save(b.Get(memo.Id)! with { Body = "远端新正文" }); await syncB.Run(); await syncA.Run();
    Check("远端更新不覆盖本机草稿", a.GetDraft("memo-draft")!.Value.Body == "未保存的草稿");
    var pendingBefore = a.PendingCount; remote.FailNextPut = true; a.Save(new Entry { Body = "网络中断时保存" });
    await MustFail("网络失败可检测", () => syncA.Run()); Check("失败保留上传队列", a.PendingCount > pendingBefore);
    await syncA.Run(); await syncB.Run(); Check("重试后上传完成", a.PendingCount == 0 && b.List("clip").Any(x => x.Body == "网络中断时保存"));
    var duplicate = a.AllChanges().First(); var versionsBefore = b.AllChanges().Count; b.Receive(duplicate); b.Receive(duplicate); Check("重复下载幂等", b.AllChanges().Count == versionsBefore);
    var draftClock = a.Clock(memo.Id); b.Save(b.Get(memo.Id)! with { Body = "新远端版本" }); await syncB.Run(); await syncA.Run();
    a.Save(memo with { Body = "旧草稿保存" }, baseClock: draftClock); Check("旧草稿保存保留冲突", a.Versions(memo.Id).Count == 2);
    var backup = Path.Combine(root, "roundtrip.clipdesk.zip"); Backup.Export(a, backup);
    using var c = new Store(Path.Combine(root, "c")); var imported = Backup.Import(c, backup);
    Check("完整备份恢复文字、状态、图片和来源", imported > 0 && c.Get(memo.Id)?.SourceId == picture.Id && c.Assets.Read(asset).SequenceEqual(png) && c.Get(todo.Id)!.Done);
    Check("重复恢复不重复插入", Backup.Import(c, backup) == 0);
    Check("恢复不克隆设备身份", c.DeviceId != a.DeviceId);
    using (var zip = System.IO.Compression.ZipFile.OpenRead(backup)) Check("备份不含凭据或本机设置", zip.Entries.All(x => x.FullName == "data.json" || x.FullName.StartsWith("assets/")));
    var beforeRecovery = a.AllChanges().Count;
    var syncC = new SyncEngine(c, remote, crypto, "test/"); await syncC.Run(); await syncA.Run(); Check("恢复数据可重新发布且不重复", a.AllChanges().Count == beforeRecovery && a.AllChanges().Select(x => x.EventId).Distinct().Count() == beforeRecovery);
    var encrypted = crypto.Encrypt(Encoding.UTF8.GetBytes("加密正文"), "test/object");
    Check("密文往返", Encoding.UTF8.GetString(crypto.Decrypt(encrypted, "test/object")) == "加密正文");
    await MustFail("篡改密文被拒绝", () => { var bad = encrypted.ToArray(); bad[^1] ^= 1; crypto.Decrypt(bad, "test/object"); return Task.CompletedTask; });
    await MustFail("错误路径被认证拒绝", () => { crypto.Decrypt(encrypted, "other/object"); return Task.CompletedTask; });
    await MustFail("错误密钥被拒绝", () => { new SyncCrypto(SyncCrypto.GenerateKey()).Decrypt(encrypted, "test/object"); return Task.CompletedTask; });
    await MustFail("空白内容拒绝保存", () => { a.Save(new Entry { Body = "  \n " }); return Task.CompletedTask; });
    await MustFail("非 HTTPS 端点拒绝", () => { KodoStore.ValidateConfig(new("http://example.com", "r", "b", "prefix/")); return Task.CompletedTask; });
    var oldAuto = a.Save(new Entry { Body = "过期普通历史", CreatedAt = Codec.Now - 60L * 86400000 });
    a.Cleanup(); Check("保留规则只清理普通历史", a.Get(oldAuto.Id)!.Deleted && !a.Get(todo.Id)!.Deleted && !a.Get(memo.Id)!.Deleted);
    using var fresh = new Store(Path.Combine(root, "fresh")); var syncFresh = new SyncEngine(fresh, remote, crypto, "test/"); var live = 0; syncFresh.LiveCopy += _ => live++;
    a.RecordCopy(a.Get(e.Id)!); await syncA.Run(); await syncFresh.Run(); Check("首次初始化不接管当前剪贴板", live == 0);
    a.Save(a.Get(e.Id)! with { Pinned = false }); await syncA.Run(); await syncFresh.Run(); Check("普通编辑不当作复制事件", live == 0);
    await Task.Delay(10); a.RecordCopy(a.Get(e.Id)!); await syncA.Run(); await syncFresh.Run(); Check("在线新复制单独传播", live == 1);
    Check("新设备收齐删除标记", fresh.Get(picture.Id)?.Deleted == true);
    var replyLost = a.Save(new Entry { Body = "上传成功但响应丢失" }); remote.LoseNextPutReply = true;
    await MustFail("服务器已写入但确认丢失", () => syncA.Run());
    Check("丢失确认仍保留待上传记录", a.PendingCount == 1);
    var restartedSync = new SyncEngine(a, remote, crypto, "test/"); await restartedSync.Run(); await syncB.Run();
    Check("重启同步器幂等补传", a.PendingCount == 0 && b.List("clip").Count(x => x.Id == replyLost.Id) == 1);
    var pendingAsset = a.Assets.Put(png.Concat(new byte[] { 0 }).ToArray()); var pendingImage = a.Save(new Entry { Format = "image", Assets = [pendingAsset] }); await restartedSync.Run(); remote.FailAssetGet = true;
    await MustFail("图片下载失败可重试", () => syncB.Run());
    Check("缺图保留记录与有效引用", b.Get(pendingImage.Id) != null && !b.Assets.Exists(pendingAsset));
    await syncB.Run(); Check("恢复后补齐图片", b.Assets.Exists(pendingAsset));
    var cursorBefore = b.Cursor(a.DeviceId); a.Save(new Entry { Body = "不跳过损坏日志" }); await restartedSync.Run(); remote.CorruptEventRead = true;
    await MustFail("损坏日志阻止推进游标", () => syncB.Run()); Check("失败不跨过坏事件", b.Cursor(a.DeviceId) == cursorBefore); await syncB.Run();
    using var wrongKeyDevice = new Store(Path.Combine(root, "wrong-key")); var objectsBefore = remote.Count;
    await MustFail("错误密钥在上传前被拒绝", () => new SyncEngine(wrongKeyDevice, remote, new SyncCrypto(SyncCrypto.GenerateKey()), "test/").Run());
    Check("错误密钥未污染远端", remote.Count == objectsBefore);
    var wire = new WireFactory(); using var sdk = new KodoStore(new("https://s3.cn-east-1.qiniucs.com", "cn-east-1", "test-space", "test/"), "test-access", "test-secret", wire);
    await sdk.Put("test/wire.bin", [1, 2, 3], default); await sdk.List("test/", "test/previous", "/", default); await sdk.Get("test/wire.bin", default);
    File.WriteAllText(Path.Combine(root, "sdk-wire.json"), Codec.Json(wire.Requests));
    Check("S3 SDK 使用签名 V4", wire.Requests.All(r => r.Authorization.StartsWith("AWS4-HMAC-SHA256", StringComparison.Ordinal)));
    Check("S3 SDK 禁用流式分块和高级校验", wire.Requests.All(r => !r.Chunked && !r.AdvancedChecksum));
    Check("S3 列表不发送未兼容 encoding-type", wire.Requests.All(r => !r.Uri.Contains("encoding-type", StringComparison.OrdinalIgnoreCase)));
    Check("S3 使用路径风格与正确空间", wire.Requests.All(r => new Uri(r.Uri).AbsolutePath.StartsWith("/test-space", StringComparison.Ordinal)));
    var persistentPath = Path.Combine(root, "restart"); string id;
    using (var d = new Store(persistentPath)) { id = d.Save(new Entry { Body = "重启数据" }).Id; d.Draft("new:memo", new(new Entry { Kind = "memo", Body = "草稿" }, [])); }
    using (var d = new Store(persistentPath)) Check("进程重建保留记录、队列与草稿", d.Get(id)?.Body == "重启数据" && d.PendingCount == 1 && d.GetDraft("new:memo") != null);
    AdversarialChecks.Run(root, Check);
    await SyncAuditChecks.Run(root, Check);
    DataAuditChecks.Run(root, Check);
    File.WriteAllText(Path.Combine(root, "checks.json"), Codec.Json(new { checks, status = "passed", scope = "本地数据与模拟对象存储；不是七牛真实接口或两台电脑验收" }));
    Console.WriteLine($"完成 {checks.Count} 项数据与协议检查。");
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(root, "checks.json"), Codec.Json(new { checks, status = "failed", error = ex.ToString() })); Console.Error.WriteLine(ex); Environment.ExitCode = 1;
}

sealed class MemoryRemote(SyncCrypto crypto) : IObjectStore
{
    private readonly Dictionary<string, byte[]> values = [];
    public bool FailNextPut { get; set; }
    public bool LoseNextPutReply { get; set; }
    public bool FailAssetGet { get; set; }
    public bool CorruptEventRead { get; set; }
    public int Count => values.Count;
    public bool ImageBeforeReference { get; private set; } = true;
    public Task Put(string key, byte[] data, CancellationToken ct)
    {
        if (FailNextPut) { FailNextPut = false; throw new IOException("模拟断网"); }
        if (key.Contains("/events/", StringComparison.Ordinal))
        {
            var wire = Codec.Read<Packet>(Encoding.UTF8.GetString(crypto.Decrypt(data, key)));
            foreach (var id in wire.Change.Value.Assets)
                if (!values.ContainsKey("test/v1/assets/" + crypto.AssetName(id) + ".bin")) ImageBeforeReference = false;
        }
        values[key] = data.ToArray();
        if (LoseNextPutReply && key.Contains("/events/", StringComparison.Ordinal)) { LoseNextPutReply = false; throw new IOException("模拟服务器已提交、响应丢失"); }
        return Task.CompletedTask;
    }
    public Task<byte[]> Get(string key, CancellationToken ct)
    {
        if (FailAssetGet && key.Contains("/assets/", StringComparison.Ordinal)) { FailAssetGet = false; throw new IOException("模拟图片下载中断"); }
        var data = values[key].ToArray();
        if (CorruptEventRead && key.Contains("/events/", StringComparison.Ordinal)) { CorruptEventRead = false; data[^1] ^= 1; }
        return Task.FromResult(data);
    }
    public Task<List<string>> List(string prefix, string? after, string? delimiter, CancellationToken ct)
    {
        var matches = values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal));
        if (delimiter != null) matches = matches.Select(k => k.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal) is var pos && pos >= 0 ? k[..(pos + 1)] : k);
        if (after != null) matches = matches.Where(k => string.CompareOrdinal(k, after) > 0);
        return Task.FromResult(matches.Distinct().Order(StringComparer.Ordinal).ToList());
    }
    public Task DeleteTestObject(string key, CancellationToken ct) { values.Remove(key); return Task.CompletedTask; }
    public void Dispose() { }
}

sealed record WireRequest(string Uri, string Authorization, bool Chunked, bool AdvancedChecksum);
sealed class WireFactory : Amazon.Runtime.HttpClientFactory
{
    public List<WireRequest> Requests { get; } = [];
    public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig config) => new(new WireHandler(Requests));
}
sealed class WireHandler(List<WireRequest> requests) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        requests.Add(new(request.RequestUri!.ToString(), request.Headers.TryGetValues("Authorization", out var auth) ? string.Join("", auth) : "", request.Headers.TransferEncodingChunked == true, request.Headers.Any(h => h.Key.StartsWith("x-amz-checksum", StringComparison.OrdinalIgnoreCase) || h.Key == "x-amz-sdk-checksum-algorithm")));
        var list = request.RequestUri.Query.Contains("list-type", StringComparison.Ordinal);
        var content = list ? "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>test-space</Name><KeyCount>0</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated></ListBucketResult>" : request.Method == HttpMethod.Get ? "test" : "";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(content) });
    }
}
