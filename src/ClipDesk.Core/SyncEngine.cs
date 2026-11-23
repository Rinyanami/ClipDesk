using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipDesk.Core;

public sealed record Packet(int Protocol, string Writer, long Sequence, Change Change,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LiveEligible = false);
public sealed record SyncResult(int Uploaded, int Received, int Downloaded);

/// <summary>每台设备一个顺序追加日志。上传确认后才发下一条；游标只跨过已验证并落地的条目。</summary>
public sealed class SyncEngine(Store store, IObjectStore remote, SyncCrypto crypto, string prefix, TimeProvider? timeProvider = null)
{
    internal const int MaxEventObjectBytes = 64 * 1024 * 1024;
    private const int UploadBatchSize = 100;
    private sealed record Registration(int Protocol, string Device);

    private readonly SemaphoreSlim busy = new(1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly HashSet<string> authenticated = new(StringComparer.Ordinal);
    private readonly HashSet<string> missingAssets = new(StringComparer.Ordinal);
    private readonly string registrationMarker = "registered:" + crypto.Fingerprint + ":" + prefix;
    private readonly string assetMarkerPrefix = "asset-uploaded:" + crypto.Fingerprint + ":" + prefix;
    private HashSet<string> liveDevices = new(StringComparer.Ordinal);
    private HashSet<string> publishedLocalEvents = new(StringComparer.Ordinal);
    private bool baselineReady;
    private bool inspectLocalTail = true;
    private long lastPoll;
    private long lastPollTimestamp;
    private long? lastAssetScan;

    public event Action<Change>? LiveCopy;
    public event Action<string>? Status;
    public event Action? RemoteWritten;

    private string Root => prefix + "v1/";
    private string Devices => Root + "devices/";
    private string AssetKey(string id) => Root + "assets/" + crypto.AssetName(id) + ".bin";
    private string Journal(string device) => Devices + device + "/events/";
    private string EventKey(string device, long sequence) => Journal(device) + sequence.ToString("D20", CultureInfo.InvariantCulture) + ".bin";
    private string RegistrationKey(string device) => Devices + device + "/device.bin";
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<SyncResult> Run(CancellationToken ct = default)
    {
        await busy.WaitAsync(ct);
        var downloaded = 0;
        try
        {
            Status?.Invoke("同步中");
            var uploaded = 0;
            var received = 0;
            var pollStarted = Now;
            var pollTimestamp = clock.GetTimestamp();
            var elapsed = clock.GetElapsedTime(lastPollTimestamp, pollTimestamp);
            var allowLive = baselineReady && pollStarted >= lastPoll && elapsed < TimeSpan.FromSeconds(90);
            var publishLive = allowLive && elapsed < TimeSpan.FromSeconds(30);
            var listed = await remote.List(Devices, null, "/", ct);
            var devices = listed.Select(DeviceFromPath).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var recheckStoredObjects = lastAssetScan == null || clock.GetElapsedTime(lastAssetScan.Value, pollTimestamp) >= TimeSpan.FromMinutes(10);
            if (recheckStoredObjects) authenticated.Clear();
            else authenticated.IntersectWith(devices);

            var localListed = devices.Contains(store.DeviceId);
            var localRegistered = store.GetMeta(registrationMarker) != null;
            if (localRegistered) devices.Add(store.DeviceId);
            ExceptionDispatchInfo? failure = null;
            void Remember(Exception ex) => failure ??= ExceptionDispatchInfo.Capture(ex);
            async Task DownloadAsset(string id)
            {
                var key = AssetKey(id);
                store.Assets.Receive(id, crypto.Decrypt(await remote.Get(key, ct), key));
                missingAssets.Remove(id);
                downloaded++;
            }

            // 新出现的设备要先通过认证。未经认证的前缀不能接收本机写入。
            foreach (var device in devices.Order(StringComparer.Ordinal))
            {
                if (authenticated.Contains(device)) continue;
                try
                {
                    var registration = await Read<Registration>(RegistrationKey(device), ct);
                    if (registration.Protocol != 1 || registration.Device != device) throw new InvalidDataException("远端设备注册信息不匹配");
                    authenticated.Add(device);
                }
                catch (FileNotFoundException) when (device == store.DeviceId && localRegistered) { }
                catch (Exception ex) when (IsObjectFailure(ex)) { Remember(ex); }
            }

            if (failure == null)
            {
                try
                {
                    if (!authenticated.Contains(store.DeviceId))
                    {
                        if (localRegistered) inspectLocalTail = true;
                        var key = RegistrationKey(store.DeviceId);
                        await remote.Put(key, Encrypt(new Registration(1, store.DeviceId), key), ct);
                        RemoteWritten?.Invoke();
                        authenticated.Add(store.DeviceId);
                    }
                    else if (!localRegistered) RemoteWritten?.Invoke();
                    if (!localRegistered) store.SetMeta(registrationMarker, "1");

                    var pending = store.Pending(UploadBatchSize);
                    if (inspectLocalTail && pending.Count > 0)
                    {
                        if (localListed || localRegistered) await FindPublishedLocalEvents(pending[0].Seq, ct);
                        inspectLocalTail = false;
                    }

                    while (pending.Count > 0 && uploaded < UploadBatchSize)
                    {
                        foreach (var (seq, change) in pending)
                        {
                            ct.ThrowIfCancellationRequested();
                            var key = EventKey(store.DeviceId, seq);
                            if (publishedLocalEvents.Remove(key))
                            {
                                // 确认可能在断网前已提交的对象，保持原始密文和在线资格。
                                var published = await ReadPacket(store.DeviceId, seq, key, ct);
                                if (Codec.Json(published.Change) != Codec.Json(change)) throw new InvalidDataException("远端日志序号已存在另一条变更");
                            }
                            else
                            {
                                var lastPublished = publishedLocalEvents.Max(StringComparer.Ordinal);
                                if (string.CompareOrdinal(key, lastPublished) < 0) throw new InvalidDataException("本机远端日志缺少较早序号，已暂停上传");
                                var packet = new Packet(1, store.DeviceId, seq, change);
                                var wire = JsonSerializer.SerializeToUtf8Bytes(packet, Codec.Options);
                                if (wire.Length > MaxEventObjectBytes - SyncCrypto.Overhead)
                                    throw new InvalidDataException("待上传事件超过 64 MiB，同步已暂停，本机内容已保留");
                                foreach (var id in change.Value.Assets.Distinct(StringComparer.Ordinal))
                                {
                                    var marker = assetMarkerPrefix + id;
                                    if (store.GetMeta(marker) != null) continue;
                                    if (!store.Assets.Exists(id)) await DownloadAsset(id);
                                    var assetKey = AssetKey(id);
                                    await remote.Put(assetKey, crypto.Encrypt(store.Assets.Read(id), assetKey), ct);
                                    store.SetMeta(marker, "1");
                                }

                                var now = Now;
                                var eligible = publishLive && IsClipboardChange(change, store.DeviceId) &&
                                    now >= pollStarted && change.Time >= lastPoll && IsFresh(change.Time, now) &&
                                    store.Get(change.Value.Id)?.Deleted != true;
                                if (eligible)
                                {
                                    var liveWire = JsonSerializer.SerializeToUtf8Bytes(packet with { LiveEligible = true }, Codec.Options);
                                    if (liveWire.Length <= MaxEventObjectBytes - SyncCrypto.Overhead) wire = liveWire;
                                }
                                await remote.Put(key, crypto.Encrypt(wire, key), ct);
                            }
                            store.Uploaded(seq);
                            uploaded++;
                            if (uploaded == UploadBatchSize) break;
                        }
                        if (uploaded == UploadBatchSize) break;
                        pending = store.Pending(UploadBatchSize - uploaded);
                    }
                }
                catch (Exception ex) when (IsObjectFailure(ex)) { Remember(ex); }
            }

            Change? latest = null;
            foreach (var device in authenticated.Order(StringComparer.Ordinal))
            {
                if (device == store.DeviceId) continue;
                try
                {
                    var cursor = store.Cursor(device);
                    var keys = await remote.List(Journal(device), cursor > 0 ? EventKey(device, cursor) : null, null, ct);
                    if (keys.Count > 0)
                    {
                        var validCount = 0;
                        while (validCount < keys.Count && cursor + validCount < long.MaxValue &&
                               keys[validCount] == EventKey(device, cursor + validCount + 1))
                            validCount++;

                        using var prefetchGate = new SemaphoreSlim(4);
                        var prefetched = Enumerable.Range(0, validCount).Select(async index =>
                        {
                            await prefetchGate.WaitAsync(ct);
                            try { return await ReadPacket(device, cursor + index + 1, keys[index], ct); }
                            finally { prefetchGate.Release(); }
                        }).ToArray();

                        try
                        {
                            for (var index = 0; index < validCount; index++)
                            {
                                ct.ThrowIfCancellationRequested();
                                var wire = await prefetched[index];
                                var added = store.ReceiveRemote(wire.Change, device, wire.Sequence);
                                foreach (var id in wire.Change.Value.Assets)
                                    if (!store.Assets.Exists(id)) missingAssets.Add(id);
                                if (added)
                                {
                                    received++;
                                    if (allowLive && liveDevices.Contains(device) && wire.LiveEligible &&
                                        IsClipboardChange(wire.Change, device) && wire.Change.Time >= lastPoll &&
                                        wire.Change.Time <= pollStarted + 5000 && IsFresh(wire.Change.Time, Now) &&
                                        (latest == null || wire.Change.Time > latest.Time ||
                                            (wire.Change.Time == latest.Time && string.CompareOrdinal(wire.Change.EventId, latest.EventId) > 0)))
                                        latest = wire.Change;
                                }
                                cursor = wire.Sequence;
                            }
                        }
                        finally
                        {
                            foreach (var task in prefetched)
                                if (task.IsFaulted) _ = task.Exception;
                        }
                        if (validCount < keys.Count)
                            throw new InvalidDataException("远端日志序号不连续，已保留游标等待重试");
                    }
                }
                catch (Exception ex) when (IsObjectFailure(ex)) { Remember(ex); }
            }

            // 重启时重建缺件集合，正常轮询只查新引用和待补图片。
            if (recheckStoredObjects)
            {
                foreach (var id in store.ReferencedAssets())
                    if (!store.Assets.Exists(id)) missingAssets.Add(id);
                lastAssetScan = pollTimestamp;
            }
            foreach (var id in missingAssets.ToArray())
            {
                if (store.Assets.Exists(id)) { missingAssets.Remove(id); continue; }
                try { await DownloadAsset(id); }
                catch (Exception ex) when (IsObjectFailure(ex)) { Remember(ex); }
            }
            failure?.Throw();

            baselineReady = true;
            lastPoll = pollStarted;
            lastPollTimestamp = pollTimestamp;
            liveDevices = new(authenticated, StringComparer.Ordinal);
            if (latest != null && store.Get(latest.Value.Id)?.Deleted != true && latest.Value.Assets.All(store.Assets.Exists))
            {
                var finishedAt = Now;
                if (finishedAt >= pollStarted && IsFresh(latest.Time, finishedAt)) LiveCopy?.Invoke(latest);
            }
            Status?.Invoke(store.PendingCount > 0 ? $"同步中 · 待上传 {store.PendingCount} 条" : "已同步");
            return new(uploaded, received, downloaded);
        }
        catch
        {
            baselineReady = false;
            inspectLocalTail = true;
            Status?.Invoke("同步失败，内容已保存在本机 · 将自动重试");
            throw;
        }
        finally
        {
            try { if (downloaded > 0) store.NotifyAssets(); }
            finally { busy.Release(); }
        }
    }

    private string? DeviceFromPath(string path)
    {
        if (!path.StartsWith(Devices, StringComparison.Ordinal) || !path.EndsWith('/') || path.Length != Devices.Length + 33)
            return null;
        var id = path[Devices.Length..^1];
        return Guid.TryParseExact(id, "N", out var guid) && guid.ToString("N") == id ? id : null;
    }

    private async Task FindPublishedLocalEvents(long firstSequence, CancellationToken ct)
    {
        var after = firstSequence > 2 ? EventKey(store.DeviceId, firstSequence - 2) : null;
        publishedLocalEvents = (await remote.List(Journal(store.DeviceId), after, null, ct)).ToHashSet(StringComparer.Ordinal);
        if (firstSequence > 1 && !publishedLocalEvents.Contains(EventKey(store.DeviceId, firstSequence - 1)))
            throw new InvalidDataException("本机远端日志缺少上一条变更，已暂停上传");
    }

    private async Task<Packet> ReadPacket(string device, long sequence, string key, CancellationToken ct)
    {
        var data = await remote.Get(key, ct);
        if (data.Length > MaxEventObjectBytes)
            throw new InvalidDataException("远端日志事件超过 64 MiB，已保留游标");
        var wire = Decode<Packet>(data, key);
        if (wire.Protocol != 1 || wire.Writer != device || wire.Sequence != sequence || wire.Change == null)
            throw new InvalidDataException("远端日志信息与对象路径不匹配");
        return wire;
    }

    private async Task<T> Read<T>(string key, CancellationToken ct) where T : class =>
        Decode<T>(await remote.Get(key, ct), key);
    private T Decode<T>(byte[] data, string key) where T : class =>
        JsonSerializer.Deserialize<T>(crypto.Decrypt(data, key), Codec.Options) ??
        throw new InvalidDataException("远端对象格式不正确");

    private byte[] Encrypt<T>(T value, string key) => crypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(value, Codec.Options), key);
    private static bool IsObjectFailure(Exception ex) => ex is FileNotFoundException or InvalidDataException or CryptographicException or JsonException;
    private static bool IsClipboardChange(Change change, string writer) =>
        change.DeviceId == writer && !change.Value.Deleted && change.Action is "copy" or "capture";
    private static bool IsFresh(long time, long now) => time >= now - 30_000 && time <= now + 5000;

    public static async Task TestConnection(IObjectStore remote, SyncCrypto crypto, string prefix, CancellationToken ct)
    {
        var key = prefix + "_connection-test/" + Codec.Id() + ".bin";
        var payload = Encoding.UTF8.GetBytes("ClipDesk 专用连接测试");
        await remote.Put(key, crypto.Encrypt(payload, key), ct);
        var read = crypto.Decrypt(await remote.Get(key, ct), key);
        if (!read.SequenceEqual(payload)) throw new InvalidDataException("测试对象内容校验失败");
        var listed = await remote.List(key, null, null, ct);
        if (!listed.Contains(key)) throw new InvalidDataException("测试对象未出现在对象列表中");
        await remote.DeleteTestObject(key, ct);
    }
}
