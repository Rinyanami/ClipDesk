using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClipDesk.Core;

public sealed class DraftConflictException(string message) : InvalidOperationException(message) { }

/// <summary>每次内容修改、版本记录与待上传队列在同一个 SQLite 事务中提交。</summary>
public sealed class Store : IDisposable
{
    public const int MaxChangeBytes = 60 * 1024 * 1024;
    private readonly object gate = new();
    private readonly SqliteConnection db;
    private readonly bool singleOwner;
    private volatile LocalSettings? cachedSettings;
    public string Root { get; }
    public string DeviceId { get; }
    public AssetStore Assets { get; }
    public event Action? Changed;
    public event Action? LocalChanged;

    public Store(string root) : this(root, singleOwner: false) { }
    public Store(string root, bool singleOwner)
    {
        this.singleOwner = singleOwner;
        Root = root; Directory.CreateDirectory(root); Assets = new(root);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "clipdesk.sqlite"), Pooling = false }.ToString());
        db.Open();
        db.CreateFunction<string, string, bool>("contains_text", (text, query) => text.Contains(query, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        var userVersion = int.Parse(Scalar("PRAGMA user_version") ?? "0");
        if (userVersion > 1) { db.Dispose(); throw new InvalidDataException("数据由较新版 ClipDesk 创建，请使用较新版打开"); }
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-8000; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entries(id TEXT PRIMARY KEY, kind TEXT NOT NULL, deleted INTEGER NOT NULL, pinned INTEGER NOT NULL, protected INTEGER NOT NULL, created INTEGER NOT NULL, search TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS entries_list ON entries(kind,deleted,pinned,created DESC);
            CREATE INDEX IF NOT EXISTS entries_recent ON entries(kind,deleted,pinned DESC,created DESC,id DESC);
            CREATE INDEX IF NOT EXISTS entries_recent_clip ON entries(kind,deleted,created DESC,id DESC);
            CREATE TABLE IF NOT EXISTS changes(id TEXT PRIMARY KEY, entity TEXT NOT NULL, active INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS changes_frontier ON changes(entity,active);
            CREATE INDEX IF NOT EXISTS changes_origin ON changes(json_extract(json,'$.deviceId'),json_extract(json,'$.sequence')) WHERE json_extract(json,'$.sequence')>0;
            CREATE TABLE IF NOT EXISTS outbox(seq INTEGER PRIMARY KEY AUTOINCREMENT, event TEXT NOT NULL UNIQUE REFERENCES changes(id), uploaded INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS outbox_pending ON outbox(uploaded,seq);
            CREATE TABLE IF NOT EXISTS asset_refs(event TEXT NOT NULL REFERENCES changes(id), asset TEXT NOT NULL, PRIMARY KEY(event,asset));
            CREATE INDEX IF NOT EXISTS asset_refs_asset ON asset_refs(asset);
            CREATE TABLE IF NOT EXISTS drafts(key TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS imports(token TEXT PRIMARY KEY, entity TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
        Exec("INSERT INTO meta VALUES('device',$p0) ON CONFLICT(key) DO NOTHING", null, Codec.Id());
        DeviceId = GetMeta("device")!;
    }

    private SqliteCommand Cmd(string sql, SqliteTransaction? tx = null, params object?[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }
    private int Exec(string sql, SqliteTransaction? tx = null, params object?[] args)
    { using var cmd = Cmd(sql, tx, args); return cmd.ExecuteNonQuery(); }
    private string? Scalar(string sql, SqliteTransaction? tx = null, params object?[] args)
    { using var cmd = Cmd(sql, tx, args); return cmd.ExecuteScalar()?.ToString(); }
    private List<T> JsonRows<T>(string sql, SqliteTransaction? tx = null, params object?[] args)
    {
        using var cmd = Cmd(sql, tx, args); using var r = cmd.ExecuteReader(); List<T> rows = [];
        while (r.Read()) rows.Add(Codec.Read<T>(r.GetString(0))); return rows;
    }
    private string? ReadMeta(string key, SqliteTransaction? tx) => Scalar("SELECT value FROM meta WHERE key=$p0", tx, key);
    private long NumberMeta(string key, SqliteTransaction? tx) => long.Parse(ReadMeta(key, tx) ?? "0");
    private void WriteMeta(string key, string value, SqliteTransaction? tx)
        => Exec("INSERT INTO meta VALUES($p0,$p1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", tx, key, value);
    public string? GetMeta(string key) { lock (gate) return ReadMeta(key, null); }
    public void SetMeta(string key, string value)
    {
        lock (gate)
        {
            WriteMeta(key, value, null);
            if (key == "settings") cachedSettings = null;
        }
    }
    public LocalSettings Settings
    {
        get
        {
            // Other tools may open multiple Store connections. Only the mutex-owning app opts in.
            if (!singleOwner) return Codec.Read<LocalSettings>(GetMeta("settings") ?? "{}");
            var snapshot = cachedSettings;
            if (snapshot == null)
            {
                lock (gate) snapshot = cachedSettings ??= Codec.Read<LocalSettings>(ReadMeta("settings", null) ?? "{}");
            }
            return snapshot with { Windows = new(snapshot.Windows) };
        }
    }
    public void SaveSettings(LocalSettings settings) => SetMeta("settings", Codec.Json(settings));
    private Entry? FindEntry(string id, SqliteTransaction? tx) => JsonRows<Entry>("SELECT json FROM entries WHERE id=$p0", tx, id).FirstOrDefault();
    public Entry? Get(string id) { lock (gate) return FindEntry(id, null); }
    public List<Entry> List(string? kind = null, string search = "", bool deleted = false, int limit = 5000, bool? done = null, bool pinnedOnly = false, bool summaryOnly = false)
    {
        var order = !pinnedOnly && kind == "clip" ? " ORDER BY created DESC,id DESC" : " ORDER BY pinned DESC,created DESC,id DESC";
        List<Entry> results;
        if (kind != null && search.Length == 0 && done == null)
        {
            var pinnedFilter = pinnedOnly ? " AND pinned=1" : "";
            lock (gate) results = JsonRows<Entry>(
                "SELECT json FROM entries WHERE kind=$p0 AND deleted=$p1" + pinnedFilter
                + order + " LIMIT $p2", null, kind, deleted, limit);
        }
        else
        {
            lock (gate) results = JsonRows<Entry>("""
                SELECT json FROM entries
                WHERE ($p0 IS NULL OR kind=$p0) AND deleted=$p1 AND ($p2='' OR contains_text(search,$p2))
                  AND ($p4 IS NULL OR COALESCE(json_extract(json,'$.done'),0)=$p4)
                  AND ($p5=0 OR pinned=1)
                """ + order + " LIMIT $p3", null, kind, deleted, search, limit, done, pinnedOnly);
        }
        if (summaryOnly)
        {
            for (var i = 0; i < results.Count; i++) results[i] = StripSummary(results[i]);
        }
        return results;
    }

    private static Entry StripSummary(Entry e) =>
        e.Html == null && e.Rtf == null && e.OriginalHtml == null && e.OriginalRtf == null && e.OriginalBody == null && e.Body.Length <= 2000
            ? e
            : e with
            {
                Body = e.Body.Length > 2000 ? e.Body[..2000] : e.Body,
                Html = null,
                Rtf = null,
                OriginalBody = null,
                OriginalHtml = null,
                OriginalRtf = null
            };
    public List<Change> Versions(string id) { lock (gate) return Frontiers(id, null); }
    private List<Change> Frontiers(string id, SqliteTransaction? tx) => JsonRows<Change>("SELECT json FROM changes WHERE entity=$p0 AND active=1", tx, id);
    public Dictionary<string, long> Clock(string id) => Clocks.Join(Versions(id));
    public (Entry? Value, Dictionary<string, long> Clock) EditSnapshot(string id)
    {
        var heads = Versions(id);
        return (heads.Count == 0 ? null : Winner(heads).Value, Clocks.Join(heads));
    }

    public HashSet<string> ConflictIds(IEnumerable<string> ids)
    {
        var selected = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return [];
        lock (gate)
        {
            using var cmd = Cmd("""
                SELECT c.entity FROM json_each($p0) AS selected
                JOIN changes AS c ON c.entity=selected.value AND c.active=1
                GROUP BY c.entity HAVING COUNT(*)>1
                """, null, Codec.Json(selected));
            using var reader = cmd.ExecuteReader();
            HashSet<string> conflicts = new(StringComparer.Ordinal);
            while (reader.Read()) conflicts.Add(reader.GetString(0));
            return conflicts;
        }
    }

    public Entry Save(Entry entry, string action = "save", Dictionary<string, long>? baseClock = null, string? draftKey = null)
    {
        ValidateEntry(entry);
        Entry saved;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            if (action == "capture" && entry.Kind == "clip" && baseClock == null && draftKey == null)
            {
                var latest = JsonRows<Entry>("SELECT json FROM entries WHERE kind='clip' AND deleted=0 ORDER BY created DESC,id DESC LIMIT 1", tx).FirstOrDefault();
                if (latest != null && latest.Format == entry.Format && latest.Title == entry.Title &&
                    latest.Body == entry.Body && latest.Html == entry.Html && latest.Rtf == entry.Rtf &&
                    latest.Assets.SequenceEqual(entry.Assets, StringComparer.Ordinal) &&
                    Frontiers(latest.Id, tx) is { Count: 1 } latestHeads)
                {
                    var now = Math.Max(Codec.Now, latest.CreatedAt);
                    var updated = latest with
                    {
                        CreatedAt = now,
                        UpdatedAt = now,
                        SourceApp = string.IsNullOrEmpty(entry.SourceApp) ? latest.SourceApp : entry.SourceApp
                    };
                    SaveEntry(updated, action, null, null, tx, latestHeads, refreshCreatedAt: true);
                    saved = FindEntry(latest.Id, tx)!;
                    tx.Commit();
                    NotifyLocalChange();
                    return saved;
                }
            }
            SaveEntry(entry, action, baseClock, draftKey, tx, Frontiers(entry.Id, tx));
            saved = FindEntry(entry.Id, tx)!;
            tx.Commit();
        }
        NotifyLocalChange();
        return saved;
    }

    public Entry AdoptVersion(Entry chosen, Dictionary<string, long> inspectedClock)
    {
        ValidateEntry(chosen);
        if (!ValidClock(inspectedClock)) throw new InvalidDataException("查看的版本信息不正确");
        Entry saved;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var heads = Frontiers(chosen.Id, tx);
            var inspectedLocalSeq = inspectedClock.GetValueOrDefault(DeviceId);
            if (heads.Any(head => head.Clock.GetValueOrDefault(DeviceId) > inspectedLocalSeq))
                throw new InvalidOperationException("本机已有更新，请重新打开版本详情后再选择");
            SaveEntry(chosen, chosen.Deleted ? "delete" : "restore", inspectedClock, null, tx, heads);
            saved = FindEntry(chosen.Id, tx)!;
            tx.Commit();
        }
        NotifyLocalChange();
        return saved;
    }

    private void SaveEntry(Entry entry, string action, Dictionary<string, long>? baseClock, string? draftKey, SqliteTransaction tx, List<Change> heads, bool refreshCreatedAt = false)
    {
        if (action == "copy") throw new InvalidDataException("请通过复制事件记录剪贴板操作");
        var current = heads.Count == 0 ? null : Winner(heads).Value;
        if (draftKey != null)
        {
            if (heads.Count > 1) throw new DraftConflictException("这条记录有并发版本，请先在详情中核对版本");
            if (current is { Deleted: true }) throw new DraftConflictException("这条记录已移入回收站，请先恢复后再保存");
            if (entry.Deleted && current != null) throw new DraftConflictException("这条记录已恢复，请打开当前记录后再保存");
            if (baseClock != null && HasNewerLocalContent(entry.Id, baseClock, tx))
                throw new DraftConflictException("打开编辑后本机已更新这条记录的内容，请先核对当前版本");
        }
        if (action == "restore" && entry.Kind == "clip") entry = entry with { Protected = true };
        if (current != null)
        {
            entry = entry with { CreatedAt = refreshCreatedAt ? entry.CreatedAt : current.CreatedAt, Protected = current.Protected || entry.Protected };
            if (draftKey != null) entry = entry with { Pinned = current.Pinned, Done = current.Done };
        }

        var previousSeq = NumberMeta("eventSeq", tx);
        var clock = new Dictionary<string, long>(baseClock ?? Clocks.Join(heads));
        if (clock.GetValueOrDefault(DeviceId) > previousSeq || previousSeq >= long.MaxValue - 1)
            throw new InvalidDataException("本机版本序号异常");
        var seq = previousSeq + 1;
        clock[DeviceId] = seq;
        if (action != "restore" && !entry.Deleted && heads.Any(head => head.Value.Deleted && Clocks.Dominates(clock, head.Clock)))
            throw new InvalidOperationException("这条记录已移入回收站，请先恢复后再保存");
        if (action == "save" && entry.Deleted && current is { Deleted: false })
            throw new InvalidOperationException("这条记录已恢复，请打开当前记录后再保存");

        var change = new Change { DeviceId = DeviceId, Sequence = seq, Value = entry with { UpdatedAt = Codec.Now }, Action = action, Clock = clock };
        Validate(change);
        foreach (var id in entry.Assets)
        {
            if (!Assets.Exists(id) && Scalar("""
                SELECT 1 FROM changes c JOIN asset_refs a ON a.event=c.id
                WHERE (c.entity=$p0 OR c.entity=$p1) AND a.asset=$p2 LIMIT 1
                """, tx, entry.Id, entry.SourceId, id) == null)
                throw new InvalidOperationException("新增图片尚未落地，请完成导入后再保存");
        }
        Apply(change, tx, true, heads);
        WriteMeta("eventSeq", seq.ToString(), tx);
        if (draftKey != null) Exec("DELETE FROM drafts WHERE key=$p0", tx, draftKey);
    }

    private bool HasNewerLocalContent(string id, Dictionary<string, long> baseClock, SqliteTransaction tx)
    {
        var baseSequence = baseClock.GetValueOrDefault(DeviceId);
        var newer = Scalar("""
            SELECT 1 FROM changes WHERE entity=$p0 AND json_extract(json,'$.deviceId')=$p1
              AND json_extract(json,'$.sequence')>0 AND json_extract(json,'$.sequence')>$p2
            LIMIT 1
            """, tx, id, DeviceId, baseSequence);
        if (newer == null) return false;

        var history = JsonRows<Change>("SELECT json FROM changes WHERE entity=$p0 AND json_extract(json,'$.action')<>'copy' ORDER BY rowid", tx, id);
        foreach (var change in history.Where(change => change.DeviceId == DeviceId && change.Sequence > baseSequence))
        {
            if (change.Action is "delete" or "restore") return true;
            // 本机元数据更新可能基于刚收到的远端正文。比较该更新自己的前序版本，避免把置顶、完成误判为本机正文修改。
            var before = new Dictionary<string, long>(change.Clock) { [DeviceId] = change.Sequence - 1 };
            var previous = EntryAtClock(history, before);
            if (previous == null || !SameDraftContent(previous, change.Value)) return true;
        }
        return false;
    }

    private static Entry? EntryAtClock(IEnumerable<Change> history, Dictionary<string, long> clock)
    {
        List<Change> heads = [];
        foreach (var version in history)
        {
            if (!Clocks.Dominates(clock, version.Clock) || heads.Any(head => Clocks.Dominates(head.Clock, version.Clock))) continue;
            heads.RemoveAll(head => Clocks.Dominates(version.Clock, head.Clock));
            heads.Add(version);
        }
        return heads.Count == 0 ? null : Winner(heads).Value;
    }

    private static bool SameDraftContent(Entry left, Entry right)
        => left.Kind == right.Kind && left.Format == right.Format && left.Title == right.Title && left.Body == right.Body &&
           left.Html == right.Html && left.Rtf == right.Rtf && left.Due == right.Due && left.Deleted == right.Deleted &&
           left.OriginalTitle == right.OriginalTitle && left.OriginalBody == right.OriginalBody &&
           left.OriginalHtml == right.OriginalHtml && left.OriginalRtf == right.OriginalRtf &&
           left.SourceId == right.SourceId && left.SourceApp == right.SourceApp && left.OcrText == right.OcrText &&
           left.Assets.SequenceEqual(right.Assets, StringComparer.Ordinal);

    public void RecordCopy(Entry entry)
    {
        if (singleOwner && !Settings.Sync.Enabled) return;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            Exec("""
                DELETE FROM asset_refs WHERE event IN (
                    SELECT o.event FROM outbox o JOIN changes c ON c.id=o.event
                    WHERE o.uploaded=0 AND json_extract(c.json,'$.action')='copy'
                );
                DELETE FROM outbox WHERE uploaded=0 AND event IN (
                    SELECT id FROM changes WHERE json_extract(json,'$.action')='copy'
                );
                DELETE FROM changes WHERE active=0 AND json_extract(json,'$.action')='copy'
                  AND NOT EXISTS (SELECT 1 FROM outbox o WHERE o.event=changes.id);
                """, tx);
            var change = new Change { DeviceId = DeviceId, Action = "copy", Value = entry, Clock = Clocks.Join(Frontiers(entry.Id, tx)) };
            Validate(change);
            Apply(change, tx, true); tx.Commit();
        }
        // 复制不改变内容和列表位置，也不触发列表重建。
        Notify(LocalChanged);
    }

    public Entry Convert(Entry source, string kind, string token)
    {
        if (kind is not ("todo" or "memo")) throw new InvalidDataException("转换类型不正确");
        var id = AssetStore.Hash(System.Text.Encoding.UTF8.GetBytes(token))[..32];
        Entry result;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var previous = FindEntry(id, tx);
            if (previous != null) return previous;
            var caption = source.Caption;
            var titleLength = Math.Min(caption.Length, 160);
            if (titleLength < caption.Length && char.IsHighSurrogate(caption[titleLength - 1])) titleLength--;
            var convertedBody = source.Body;
            if (kind == "todo" && string.IsNullOrWhiteSpace(source.Title) && !string.IsNullOrWhiteSpace(source.Body))
            {
                var trimmedBody = source.Body.Trim();
                var firstNewline = trimmedBody.IndexOf('\n');
                if (firstNewline < 0)
                {
                    convertedBody = caption.Length > titleLength ? trimmedBody : "";
                }
                else
                {
                    var firstLine = trimmedBody[..firstNewline].Trim();
                    convertedBody = firstLine.Length <= titleLength ? trimmedBody[(firstNewline + 1)..].TrimStart('\r', '\n') : trimmedBody;
                }
            }
            var value = new Entry { Id = id, Kind = kind, Title = kind == "todo" ? caption[..titleLength] : source.Title, Body = convertedBody, Assets = [.. source.Assets], SourceId = source.Id, Protected = true, Format = source.Assets.Count > 0 ? "image" : "text" };
            // 稳定提交令牌决定内容 ID；崩溃重试也不会生成第二份。
            SaveEntry(value, "save", null, null, tx, []);
            result = FindEntry(id, tx)!;
            tx.Commit();
        }
        NotifyLocalChange();
        return result;
    }

    public void Delete(string id) => UpdateEntry(id, "delete", e => e.Deleted ? null : e with { Deleted = true });
    public void Restore(string id) => UpdateEntry(id, "restore", e => e.Deleted ? e with { Deleted = false } : null);
    public void SetPinned(string id, bool pinned) => UpdateEntry(id, "save", e => e.Deleted || e.Pinned == pinned ? null : e with { Pinned = pinned });
    public void SetDone(string id, bool done) => UpdateEntry(id, "save", e => e.Deleted || e.Kind != "todo" || e.Done == done ? null : e with { Done = done });

    private void UpdateEntry(string id, string action, Func<Entry, Entry?> update)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var heads = Frontiers(id, tx);
            if (heads.Count == 0) return;
            var winner = Winner(heads);
            var next = update(winner.Value);
            if (next == null) return;
            if (action == "save" && heads.Count > 1)
                throw new InvalidOperationException("这条记录有并发版本，请先在详情中核对版本");
            SaveEntry(next, action, action == "save" ? winner.Clock : null, null, tx, heads);
            tx.Commit();
        }
        NotifyLocalChange();
    }

    public int Cleanup(bool clearAll = false)
    {
        var removed = 0;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var settings = Codec.Read<LocalSettings>(ReadMeta("settings", tx) ?? "{}");
            if (!clearAll && settings.RetentionDays <= 0 && settings.RetentionCount <= 0) return 0;
            var cutoff = Codec.Now - settings.RetentionDays * 86400000L;
            var candidates = JsonRows<Entry>("""
                SELECT json FROM (
                    SELECT e.json,e.created,ROW_NUMBER() OVER (ORDER BY e.created DESC,e.id DESC) AS position
                    FROM entries e WHERE e.kind='clip' AND e.deleted=0 AND e.pinned=0 AND e.protected=0
                      AND NOT EXISTS (
                          SELECT 1 FROM changes c WHERE c.entity=e.id AND c.active=1 LIMIT 1 OFFSET 1
                      )
                ) WHERE $p0 OR ($p1>0 AND created<$p2) OR ($p3>0 AND position>$p3)
                ORDER BY position
                """, tx, clearAll, settings.RetentionDays, cutoff, settings.RetentionCount);
            foreach (var entry in candidates)
            {
                var heads = Frontiers(entry.Id, tx);
                if (heads.Any(head => head.Value.Pinned || head.Value.Protected)) continue;
                SaveEntry(entry with { Deleted = true }, "delete", null, null, tx, heads);
                removed++;
            }
            tx.Commit();
        }
        if (removed > 0) NotifyLocalChange();
        return removed;
    }

    private void NotifyLocalChange() { Notify(Changed); Notify(LocalChanged); }

    private static void Notify(Action? observers)
    {
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception ex)
            {
                // 通知发生在提交之后，订阅者故障不能使调用方误判持久化失败。
                Trace.TraceWarning("ClipDesk 数据变更通知失败：{0}", ex.GetType().Name);
            }
        }
    }

    public bool Receive(Change change, bool queueForRecovery = false)
    {
        Validate(change);
        bool added;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            added = Apply(change, tx, queueForRecovery, incoming: true);
            tx.Commit();
        }
        if (added && change.Action != "copy") Notify(Changed);
        if (added && queueForRecovery) Notify(LocalChanged);
        return added;
    }

    public bool ReceiveRemote(Change change, string writer, long sequence)
    {
        Validate(change);
        if (!IsId(writer) || sequence <= 0) throw new InvalidDataException("远端日志标识或序号不正确");
        var cursorKey = "cursor:" + writer;
        bool added;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var cursor = NumberMeta(cursorKey, tx);
            if (sequence <= cursor && IsKnownChange(change, Codec.Json(change), tx)) return false;
            if (sequence - 1 != cursor) throw new InvalidDataException("远端日志序号不连续，已保留游标等待重试");
            added = Apply(change, tx, false, incoming: true);
            WriteMeta(cursorKey, sequence.ToString(), tx);
            tx.Commit();
        }
        if (added && change.Action != "copy") Notify(Changed);
        return added;
    }

    private bool IsKnownChange(Change change, string json, SqliteTransaction tx)
    {
        var existing = Scalar("SELECT json FROM changes WHERE id=$p0", tx, change.EventId);
        if (existing == null) return false;
        if (existing == json) return true;
        using var stored = JsonDocument.Parse(existing);
        using var incoming = JsonDocument.Parse(json);
        if (!JsonElement.DeepEquals(stored.RootElement, incoming.RootElement))
            throw new InvalidDataException("同一事件标识的内容不一致");
        return true;
    }

    private void ValidateIncomingVersion(Change c, SqliteTransaction tx)
    {
        var localSeq = NumberMeta("eventSeq", tx);
        var observedLocalSeq = c.Clock.GetValueOrDefault(DeviceId);
        if (observedLocalSeq > localSeq)
        {
            if (c.DeviceId != DeviceId || c.Action == "copy")
                throw new InvalidDataException("同步变更引用了未知的本机版本");
            WriteMeta("eventSeq", observedLocalSeq.ToString(), tx);
        }

        if (c.Action == "copy") return;
        // 相同向量的另一事件只能来自其某个设备和序号，按索引查找该版本。
        using var cmd = Cmd("""
            SELECT CASE WHEN stamp.key=$p1 THEN NULL ELSE json_extract(c.json,'$.clock') END
            FROM json_each($p0) AS stamp JOIN changes c
              ON json_extract(c.json,'$.deviceId')=stamp.key AND json_extract(c.json,'$.sequence')=stamp.value
            WHERE json_extract(c.json,'$.sequence')>0 AND (stamp.key=$p1 OR c.entity=$p2)
            """, tx, Codec.Json(c.Clock), c.DeviceId, c.Value.Id);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) throw new InvalidDataException("同一设备版本序号对应多个事件");
            var known = Codec.Read<Dictionary<string, long>>(reader.GetString(0));
            if (Clocks.Dominates(known, c.Clock) && Clocks.Dominates(c.Clock, known))
                throw new InvalidDataException("同一版本向量对应多个事件");
        }
    }

    private static Change Winner(IEnumerable<Change> heads)
        => heads.OrderByDescending(x => x.Value.Deleted).ThenByDescending(x => x.Time).ThenByDescending(x => x.EventId, StringComparer.Ordinal).First();

    private bool Apply(Change c, SqliteTransaction tx, bool queue, List<Change>? heads = null, bool incoming = false)
    {
        var json = Codec.Json(c);
        ValidateSerializedChange(json);
        if (IsKnownChange(c, json, tx)) return false;
        if (incoming) ValidateIncomingVersion(c, tx);
        heads ??= c.Action == "copy" ? [] : Frontiers(c.Value.Id, tx);
        var active = c.Action != "copy" && !heads.Any(x => Clocks.Dominates(x.Clock, c.Clock));
        Exec("INSERT INTO changes VALUES($p0,$p1,$p2,$p3)", tx, c.EventId, c.Value.Id, active, json);
        foreach (var asset in c.Value.Assets.Distinct()) Exec("INSERT INTO asset_refs VALUES($p0,$p1)", tx, c.EventId, asset);
        if (queue) Exec("INSERT INTO outbox(event) VALUES($p0)", tx, c.EventId);
        if (!active) return true;
        var remaining = new List<Change> { c };
        foreach (var old in heads)
        {
            if (Clocks.Dominates(c.Clock, old.Clock)) Exec("UPDATE changes SET active=0 WHERE id=$p0", tx, old.EventId);
            else remaining.Add(old);
        }
        // 并发删除优先；正文的所有并发版本都在 active frontier 中保留。
        var winner = Winner(remaining).Value;
        Exec("INSERT INTO entries VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7) ON CONFLICT(id) DO UPDATE SET kind=excluded.kind,deleted=excluded.deleted,pinned=excluded.pinned,protected=excluded.protected,created=excluded.created,search=excluded.search,json=excluded.json", tx,
            winner.Id, winner.Kind, winner.Deleted, winner.Pinned, winner.Protected, winner.CreatedAt, winner.SearchText, Codec.Json(winner));
        return true;
    }

    public List<(long Seq, Change Change)> Pending(int limit = 100)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT o.seq,c.json FROM outbox o JOIN changes c ON c.id=o.event WHERE uploaded=0 ORDER BY seq LIMIT $p0", null, limit);
            using var r = cmd.ExecuteReader(); List<(long, Change)> rows = [];
            var remaining = MaxChangeBytes;
            while (r.Read())
            {
                var json = r.GetString(1);
                var bytes = Encoding.UTF8.GetByteCount(json);
                if (bytes > remaining && rows.Count > 0) break;
                if (bytes > MaxChangeBytes)
                    throw new InvalidDataException("待同步的旧变更超过 60 MB，已保留本机数据和队列，需要离线迁移");
                rows.Add((r.GetInt64(0), Codec.Read<Change>(json)));
                remaining -= bytes;
            }
            return rows;
        }
    }
    public int PendingCount { get { lock (gate) return int.Parse(Scalar("SELECT COUNT(*) FROM outbox WHERE uploaded=0")!); } }
    public void Uploaded(long seq) { lock (gate) Exec("UPDATE outbox SET uploaded=1 WHERE seq=$p0", null, seq); }
    public void RequeueAll() { lock (gate) Exec("UPDATE outbox SET uploaded=0"); }
    public long Cursor(string device) => long.Parse(GetMeta("cursor:" + device) ?? "0");
    public void AdvanceCursor(string device, long seq) => SetMeta("cursor:" + device, seq.ToString());
    public void Draft(string key, Draft draft)
    {
        ValidateDraft(key, draft);
        lock (gate) Exec("INSERT INTO drafts VALUES($p0,$p1) ON CONFLICT(key) DO UPDATE SET json=excluded.json", null, key, Codec.Json(draft));
    }
    public Draft? GetDraft(string key) { lock (gate) return JsonRows<Draft>("SELECT json FROM drafts WHERE key=$p0", null, key).FirstOrDefault(); }
    public List<string> DraftKeys()
    {
        lock (gate) { using var cmd = Cmd("SELECT key FROM drafts"); using var r = cmd.ExecuteReader(); List<string> result = []; while (r.Read()) result.Add(r.GetString(0)); return result; }
    }
    public void DiscardDraft(string key) { lock (gate) Exec("DELETE FROM drafts WHERE key=$p0", null, key); }
    public List<Change> AllChanges() { lock (gate) return JsonRows<Change>("SELECT json FROM changes ORDER BY rowid"); }
    public List<string> ReferencedAssets()
    { lock (gate) { using var c = Cmd("SELECT DISTINCT asset FROM asset_refs"); using var r = c.ExecuteReader(); List<string> a = []; while (r.Read()) a.Add(r.GetString(0)); return a; } }
    public void NotifyAssets() => Notify(Changed);

    internal BackupData BackupSnapshot()
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction(deferred: true);
            var changes = JsonRows<Change>("SELECT json FROM changes WHERE json_extract(json,'$.action')<>'copy' ORDER BY rowid", tx);
            Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
            using (var cmd = Cmd("SELECT key,json FROM drafts", tx))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) drafts.Add(reader.GetString(0), Codec.Read<Draft>(reader.GetString(1)));
            tx.Commit();
            return new BackupData(1, changes, drafts);
        }
    }

    internal int MergeBackup(BackupData backup, Func<int> publishAssets)
    {
        var added = 0;
        var draftsAdded = 0;
        int assetsAdded;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var ownSeq = backup.Changes.Where(c => c.DeviceId == DeviceId).Select(c => c.Sequence).DefaultIfEmpty(0).Max();
            if (ownSeq > NumberMeta("eventSeq", tx)) WriteMeta("eventSeq", ownSeq.ToString(), tx);
            foreach (var change in backup.Changes)
                if (change.Action != "copy" && Apply(change, tx, true, incoming: true)) added++;
            foreach (var pair in backup.Drafts)
                draftsAdded += Exec("INSERT INTO drafts VALUES($p0,$p1) ON CONFLICT(key) DO NOTHING", tx, pair.Key, Codec.Json(pair.Value));
            // 图片校验和暂存已经完成。先发布文件，再提交数据库引用。
            assetsAdded = publishAssets();
            tx.Commit();
        }
        if (added > 0 || draftsAdded > 0 || assetsAdded > 0) Notify(Changed);
        if (added > 0) Notify(LocalChanged);
        return added;
    }

    private static bool IsId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);
    private static bool IsTime(long time)
        => time >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && time <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private static void ValidateEntryShape(Entry? e)
    {
        if (e == null || !IsId(e.Id) || e.Kind is not ("clip" or "todo" or "memo") || e.Format is not ("text" or "link" or "image") ||
            (e.SourceId != null && !IsId(e.SourceId)) || !IsTime(e.CreatedAt) || !IsTime(e.UpdatedAt))
            throw new InvalidDataException("记录标识、类型或时间不正确");
        if (e.Title is null or { Length: > 4000 } || e.Body is null or { Length: > 4_000_000 } ||
            e.SourceApp is null or { Length: > 4000 } || e.OcrText is null or { Length: > 4_000_000 } ||
            e.Html?.Length > 4_000_000 || e.Rtf?.Length > 4_000_000 || e.OriginalTitle?.Length > 4000 ||
            e.OriginalBody?.Length > 4_000_000 || e.OriginalHtml?.Length > 4_000_000 || e.OriginalRtf?.Length > 4_000_000 ||
            e.Due?.Length > 4000 || e.Assets == null || e.Assets.Count > 20 || e.Assets.Any(id => !AssetStore.Valid(id)))
            throw new InvalidDataException("记录内容格式、长度或图片标识不正确");
    }

    public static void ValidateEntry(Entry e)
    {
        ValidateEntryShape(e);
        if (!e.Deleted && string.IsNullOrWhiteSpace(e.Body) && string.IsNullOrWhiteSpace(e.Title) && e.Assets.Count == 0)
            throw new InvalidDataException("内容不能为空，请输入文字或添加图片");
        if (e.Kind == "todo" && string.IsNullOrWhiteSpace(e.Title)) throw new InvalidDataException("请填写待办标题");
    }

    private static bool ValidClock(IReadOnlyDictionary<string, long>? clock)
        => clock is { Count: <= 1000 } && clock.All(p => IsId(p.Key) && p.Value is >= 0 and < long.MaxValue);

    internal static void ValidateDraft(string key, Draft? draft)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4000 || draft == null || !ValidClock(draft.BaseClock))
            throw new InvalidDataException("草稿标识或版本信息不正确");
        ValidateEntryShape(draft.Value);
    }

    public static void Validate(Change? c)
    {
        if (c == null || c.Protocol != 1 || !IsId(c.EventId) || !IsId(c.DeviceId) || !IsTime(c.Time) ||
            !ValidClock(c.Clock) || c.Action is not ("save" or "capture" or "copy" or "delete" or "restore"))
            throw new InvalidDataException("不支持的同步变更格式");
        ValidateEntry(c.Value);
        if (c.Action == "copy" ? c.Sequence != 0 : c.Sequence <= 0 || c.Clock.GetValueOrDefault(c.DeviceId) != c.Sequence)
            throw new InvalidDataException("同步版本信息不完整");
        if ((c.Action == "delete" && !c.Value.Deleted) || (c.Action is "restore" or "capture" && c.Value.Deleted) ||
            (c.Action == "capture" && c.Value.Kind != "clip"))
            throw new InvalidDataException("同步动作与记录状态不一致");
    }

    internal static void ValidateSerializedChange(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxChangeBytes)
            throw new InvalidDataException("单条变更超过 60 MB，请缩短正文或富文本");
    }
    public void Dispose() { lock (gate) db.Dispose(); }
}
