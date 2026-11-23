using System.IO.Compression;
using System.Text;
using ClipDesk.Core;

internal static class DataAuditChecks
{
    private static readonly byte[] SampleImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9XcAAAAASUVORK5CYII=");

    public static void Run(string root, Action<string, bool> check)
    {
        BackupTransactions(root, check);
        RemoteTransactions(root, check);
        AdoptAndCleanup(root, check);
        DraftSaveGuards(root, check);
        ConcurrentWrites(root, check);
        PinnedProjection(root, check);
        LegacyWindowSettings(root, check);
    }

    private static void BackupTransactions(string root, Action<string, bool> check)
    {
        using var source = new Store(Path.Combine(root, "data-backup-source"));
        using var target = new Store(Path.Combine(root, "data-backup-target"));
        var original = target.Save(new Entry { Body = "本机已有正文" });
        target.Draft("new:memo", new(new Entry { Kind = "memo", Body = "本机已有草稿" }, []));
        source.Draft("new:memo", new(new Entry { Kind = "memo", Body = "备份里的同名草稿" }, []));
        source.Draft("new:todo", new(new Entry { Kind = "todo" }, []));
        var asset = source.Assets.Put(SampleImage);
        var image = source.Save(new Entry { Format = "image", Assets = [asset] });
        var first = source.AllChanges().Single();
        var existing = target.AllChanges().Single();
        var inconsistent = existing with { Value = existing.Value with { Body = "不同正文" } };
        var draft = source.GetDraft("new:todo")!;
        var broken = Path.Combine(root, "data-inconsistent.clipdesk.zip");
        WriteLegacyBackup(broken, new(1, [first, inconsistent], new() { ["new:todo"] = draft }), asset);
        var pending = target.PendingCount;
        check("恢复冲突事件时回滚记录、附件引用和队列",
            Throws<InvalidDataException>(() => Backup.Import(target, broken)) &&
            target.Get(image.Id) == null && target.Get(original.Id)?.Body == original.Body &&
            target.GetDraft("new:todo") == null && target.PendingCount == pending &&
            target.ReferencedAssets().Count == 0 && !target.Assets.Exists(asset));

        var legacy = Path.Combine(root, "data-legacy.clipdesk.zip");
        WriteLegacyBackup(legacy, new(1, [first], new()
        {
            ["new:memo"] = source.GetDraft("new:memo")!,
            ["new:todo"] = draft
        }), asset);
        var notifications = 0;
        target.Changed += () => notifications++;
        var added = Backup.Import(target, legacy);
        check("旧版带 BOM 的备份恢复空白草稿并保留本机同名草稿",
            added == 1 && target.PendingCount == pending + 1 && notifications == 1 &&
            target.GetDraft("new:memo")?.Value.Body == "本机已有草稿" &&
            target.GetDraft("new:todo")?.Value.Kind == "todo" && target.Assets.Read(asset).SequenceEqual(SampleImage));
        check("同一份备份重复恢复不新增通知或队列",
            Backup.Import(target, legacy) == 0 && notifications == 1 && target.PendingCount == pending + 1);
    }

    private static void RemoteTransactions(string root, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "data-remote-transactions"));
        var writer = Codec.Id();
        var first = Version(writer, 1, new Entry { Body = "已验证的第一条" });
        var added = store.ReceiveRemote(first, writer, 1);
        var changed = first with { Value = first.Value with { Body = "不一致的事件内容" } };
        var second = Version(writer, 2, new Entry { Body = "第二条" });
        check("事件与游标同事务，跳号或不一致重发均保留原游标",
            added && Throws<InvalidDataException>(() => store.ReceiveRemote(changed, writer, 2)) &&
            Throws<InvalidDataException>(() => store.ReceiveRemote(second, writer, 3)) &&
            store.Cursor(writer) == 1 && store.Get(first.Value.Id)?.Body == first.Value.Body && store.Get(second.Value.Id) == null);
        check("原事件由新日志顺序重发时去重并推进游标",
            !store.ReceiveRemote(first, writer, 2) && store.Cursor(writer) == 2 && store.AllChanges().Count == 1);

        var duplicateVersion = Version(writer, 1, new Entry { Body = "同一来源版本号的另一记录" });
        var badTime = second with { Time = long.MaxValue };
        var noContent = second with { Value = second.Value with { Body = null! } };
        check("拒绝复用来源版本号和无效嵌套数据",
            Throws<InvalidDataException>(() => store.ReceiveRemote(duplicateVersion, writer, 3)) &&
            Throws<InvalidDataException>(() => store.ReceiveRemote(badTime, writer, 3)) &&
            Throws<InvalidDataException>(() => store.ReceiveRemote(noContent, writer, 3)) &&
            store.Cursor(writer) == 2 && store.AllChanges().Count == 1);

        var restored = Version(store.DeviceId, 17, new Entry { Body = "曾经保存的本机记录" });
        store.Receive(restored, queueForRecovery: true);
        var saved = store.Save(new Entry { Body = "恢复后保存" });
        check("恢复本机历史版本后续写更高版本号",
            store.Versions(saved.Id).Single().Sequence == 18 && store.PendingCount == 2);
    }

    private static void AdoptAndCleanup(string root, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "data-adopt-cleanup"));
        var original = store.Save(new Entry { Body = "本机原文" });
        var inspected = store.Clock(original.Id);
        store.SetPinned(original.Id, true);
        var remote = Version(Codec.Id(), 1, original with { Body = "并发正文" }) with { Time = Codec.Now - 1000 };
        store.Receive(remote);
        check("版本详情打开后本机有新更新时拒绝旧选择",
            Throws<InvalidOperationException>(() => store.AdoptVersion(remote.Value, inspected)) &&
            store.Get(original.Id)?.Pinned == true);
        var selected = store.AdoptVersion(remote.Value, store.Clock(original.Id));
        check("当前详情仍可显式采用所选冲突正文",
            selected.Body == remote.Value.Body && store.Versions(original.Id).Count == 1);

        var old = new Entry { Body = "早期普通历史", CreatedAt = Codec.Now - 70L * 86_400_000 };
        store.Receive(Version(Codec.Id(), 1, old) with { Time = Codec.Now + 1000 });
        store.Receive(Version(Codec.Id(), 1, old with { Body = "独立编辑版", Protected = true }));
        var plain = store.Save(new Entry { Body = "可清理的普通历史", CreatedAt = old.CreatedAt });
        var removed = store.Cleanup();
        check("普通历史清理保留并发分支中的长期保护",
            removed == 1 && store.Get(old.Id)?.Deleted == false && store.Get(plain.Id)?.Deleted == true &&
            store.ConflictIds([old.Id, plain.Id]).SetEquals([old.Id]));
        store.Restore(plain.Id);
        check("手动恢复早期剪贴后保留原排序时间并跳过后续普通清理",
            store.Cleanup() == 0 && store.Get(plain.Id) is { Deleted: false, Protected: true } restored &&
            restored.CreatedAt == plain.CreatedAt);

        var ordinaryConflict = new Entry { Body = "过期剪贴的并发版本一", CreatedAt = old.CreatedAt };
        store.Receive(Version(Codec.Id(), 1, ordinaryConflict));
        store.Receive(Version(Codec.Id(), 1, ordinaryConflict with { Body = "过期剪贴的并发版本二" }));
        var recent = store.Save(new Entry { Body = "没有并发的普通剪贴" });
        check("自动和手动清理普通历史均保留未核对的并发版本",
            store.Cleanup() == 0 && store.Cleanup(clearAll: true) == 1 && store.Get(recent.Id)?.Deleted == true &&
            store.Get(ordinaryConflict.Id)?.Deleted == false && store.Versions(ordinaryConflict.Id).Count == 2);
    }

    private static void ConcurrentWrites(string root, Action<string, bool> check)
    {
        var path = Path.Combine(root, "data-concurrent");
        using var first = new Store(path);
        using var second = new Store(path);
        Parallel.Invoke(
            () => first.Save(new Entry { Body = "连接一保存" }),
            () => second.Save(new Entry { Body = "连接二保存" }));
        check("两个数据库连接共享稳定设备 ID 和严格递增的本机序号",
            first.DeviceId == second.DeviceId && first.PendingCount == 2 &&
            first.AllChanges().Select(change => change.Sequence).SequenceEqual([1L, 2L]));
        var id = AssetStore.Hash(SampleImage);
        Parallel.For(0, 8, i => (i % 2 == 0 ? first : second).Assets.Receive(id, SampleImage));
        check("并发落地相同原图保持内容和摘要一致",
            first.Assets.Exists(id) && second.Assets.Read(id).SequenceEqual(SampleImage));
        byte[] invalid = [1, 2, 3, 4];
        check("无效 PNG 内容即使摘要匹配也不会落地",
            Throws<InvalidDataException>(() => first.Assets.Receive(AssetStore.Hash(invalid), invalid)) &&
            !first.Assets.Exists(AssetStore.Hash(invalid)));
    }

    private static void DraftSaveGuards(string root, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "data-draft-guards"));
        var original = store.Save(new Entry { Body = "已有正文" });
        var opened = store.EditSnapshot(original.Id);
        var draft = original with { Body = "本机未提交的草稿" };
        store.Draft("conflict", new(draft, opened.Clock));
        store.Receive(Version(Codec.Id(), 1, original with { Body = "远端并发正文" }));
        var pending = store.PendingCount;
        check("已有并发版本时普通编辑保留草稿和双方版本",
            Throws<DraftConflictException>(() => store.Save(draft, baseClock: opened.Clock, draftKey: "conflict")) &&
            store.Versions(original.Id).Count == 2 && store.GetDraft("conflict")?.Value.Body == draft.Body &&
            store.PendingCount == pending);

        var editable = store.Save(new Entry { Title = "初始标题", Body = "初始正文" });
        var beforeLocal = store.Clock(editable.Id);
        var localDraft = editable with { Body = "待保存草稿" };
        store.Draft("local-edit", new(localDraft, beforeLocal));
        store.Save(editable with { Title = "新标题", Body = "本机后来保存的正文" });
        pending = store.PendingCount;
        check("旧编辑器不能覆盖本机后来保存的内容",
            Throws<DraftConflictException>(() => store.Save(localDraft, baseClock: beforeLocal, draftKey: "local-edit")) &&
            store.Get(editable.Id)?.Title == "新标题" && store.GetDraft("local-edit")?.Value.Body == localDraft.Body &&
            store.PendingCount == pending);

        var beforeAdopt = store.Clock(editable.Id);
        store.Draft("adopt", new(localDraft, beforeAdopt));
        store.AdoptVersion(editable, beforeAdopt);
        check("旧草稿不能覆盖刚显式采用的版本",
            Throws<DraftConflictException>(() => store.Save(localDraft, baseClock: beforeAdopt, draftKey: "adopt")) &&
            store.Get(editable.Id)?.Body == editable.Body && store.GetDraft("adopt")?.Value.Body == localDraft.Body);

        var todo = store.Save(new Entry { Kind = "todo", Title = "待办标题", Body = "打开编辑器时的正文" });
        var beforeRemote = store.Clock(todo.Id);
        var todoDraft = todo with { Body = "本机草稿正文" };
        store.Draft("todo", new(todoDraft, beforeRemote));
        var remote = Version(Codec.Id(), 1, todo with { Body = "新同步来的正文" });
        remote.Clock[store.DeviceId] = beforeRemote[store.DeviceId];
        store.Receive(remote);
        store.SetDone(todo.Id, true);
        store.Save(todoDraft, baseClock: beforeRemote, draftKey: "todo");
        var versions = store.Versions(todo.Id);
        check("本机只改完成状态时保留新远端正文与旧草稿的并发版本",
            versions.Count == 2 && versions.All(change => change.Value.Done) && store.GetDraft("todo") == null &&
            versions.Select(change => change.Value.Body).ToHashSet().SetEquals([remote.Value.Body, todoDraft.Body]));

        var task = store.Save(new Entry { Kind = "todo", Title = "带冲突的待办", Body = "共同版本" });
        var taskBase = store.Clock(task.Id);
        store.Save(task with { Body = "本机并发编辑" });
        var remoteWinner = Version(Codec.Id(), 1, task with { Body = "显示的远端并发版本" })
            with { Time = store.Versions(task.Id).Single().Time + 1000 };
        remoteWinner.Clock[store.DeviceId] = taskBase[store.DeviceId];
        store.Receive(remoteWinner);
        pending = store.PendingCount;
        check("置顶或完成不会消掉没有采纳的本机并发版本",
            store.Get(task.Id)?.Body == remoteWinner.Value.Body &&
            Throws<InvalidOperationException>(() => store.SetPinned(task.Id, true)) &&
            Throws<InvalidOperationException>(() => store.SetDone(task.Id, true)) &&
            store.Versions(task.Id).Count == 2 && store.PendingCount == pending);

        var changeNotifications = 0;
        var queueNotifications = 0;
        store.Changed += () => throw new InvalidOperationException("模拟界面刷新异常");
        store.Changed += () => changeNotifications++;
        store.LocalChanged += () => queueNotifications++;
        var copy = store.Save(draft with { Id = Codec.Id(), SourceId = original.Id }, draftKey: "conflict");
        check("另存提交后的通知异常不影响后续通知或另存成功结果",
            store.Get(copy.Id)?.Body == draft.Body && store.GetDraft("conflict") == null &&
            store.Versions(original.Id).Count == 2 && store.PendingCount == pending + 1 &&
            changeNotifications == 1 && queueNotifications == 1);
    }

    private static void PinnedProjection(string root, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "data-pinned-projection"));
        var now = Codec.Now;
        var image = store.Assets.Put(SampleImage);
        var older = store.Save(new Entry { Body = "Épinglé avec image", Format = "image", Assets = [image], CreatedAt = now - 2000 });
        var newer = store.Save(new Entry { Body = "Épinglé récent", Pinned = true, CreatedAt = now - 1000 });
        var ordinary = store.Save(new Entry { Body = "Épinglé texte ordinaire", CreatedAt = now });
        store.Save(new Entry { Kind = "todo", Title = "Épinglé tâche", Pinned = true, CreatedAt = now });
        var removed = store.Save(new Entry { Body = "Épinglé supprimé", Pinned = true, CreatedAt = now });
        store.Delete(removed.Id);
        store.SetPinned(older.Id, true);
        var pending = store.PendingCount;
        var eventCount = store.AllChanges().Count;
        var pinned = store.List("clip", "ÉPINGLÉ", limit: 2, pinnedOnly: true);
        check("独立置顶列表保留原记录顺序、标识与附件引用",
            pinned.Select(entry => entry.Id).SequenceEqual([newer.Id, older.Id]) &&
            pinned[1].CreatedAt == older.CreatedAt && pinned[1].Assets.SequenceEqual([image]) &&
            store.Assets.Read(image).SequenceEqual(SampleImage) &&
            store.PendingCount == pending && store.AllChanges().Count == eventCount);

        store.SetPinned(older.Id, false);
        check("取消置顶只移出独立置顶列表，原记录与附件仍在历史中",
            store.List("clip", pinnedOnly: true).Select(entry => entry.Id).SequenceEqual([newer.Id]) &&
            store.List("clip").Select(entry => entry.Id).SequenceEqual([ordinary.Id, newer.Id, older.Id]) &&
            store.Get(older.Id)?.Assets.SequenceEqual([image]) == true);

        var firstCapture = store.Save(new Entry { Body = "连续相同剪贴去重", CreatedAt = now + 100 }, "capture");
        var secondCapture = store.Save(new Entry { Body = "连续相同剪贴去重", CreatedAt = now + 200 }, "capture");
        check("连续相同捕获复用同一记录并刷新时间",
            firstCapture.Id == secondCapture.Id && store.List("clip").Count(entry => entry.Id == firstCapture.Id) == 1);
    }

    private static void LegacyWindowSettings(string root, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "data-legacy-window-settings"));
        store.SetMeta("settings", """
            {"hotkey":"Ctrl+Alt+F9","paused":true,"windows":{"history":{"left":20,"top":32,"width":560,"height":640,"topmost":true,"open":true,"screenX":-1920,"screenY":0}}}
            """);
        var settings = store.Settings;
        check("旧本机设置保留热键和布局，新热键与窗口选项使用兼容默认值",
            settings.Hotkey == "Ctrl+Alt+F9" && settings.Paused && settings.PinnedHotkey == "" &&
            settings.TodoHotkey == "" && settings.MemoHotkey == "" &&
            settings.Windows.GetValueOrDefault("history") is
            { Left: 20, Top: 32, Width: 560, Height: 640, Topmost: true, Open: true, ScreenX: -1920, ScreenY: 0, Opacity: 1, KeepVisible: null });
    }

    private static Change Version(string device, long sequence, Entry entry) => new()
    {
        DeviceId = device, Sequence = sequence, Clock = new() { [device] = sequence }, Value = entry
    };

    private static void WriteLegacyBackup(string path, BackupData data, string asset)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("data.json").Open(), Encoding.UTF8))
            writer.Write(Codec.Json(data));
        using var image = zip.CreateEntry("assets/" + asset + ".png").Open();
        image.Write(SampleImage);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }
}
