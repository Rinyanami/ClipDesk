using System.IO.Compression;
using ClipDesk.Core;

public static class AdversarialChecks
{
    private static readonly byte[] RedImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAOSURBVBhXY2BABv+BCAAFDQH/o+SeOAAAAABJRU5ErkJggg==");
    private static readonly byte[] BlueImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAOSURBVBhXY2BABf//AwADDwH/ahZy+gAAAABJRU5ErkJggg==");

    public static void Run(string root) => Run(root, (name, ok) => Console.WriteLine((ok ? "通过：" : "失败：") + name));

    public static void Run(string root, Action<string, bool> report)
    {
        var work = Path.Combine(root, "adversarial-" + Codec.Id());
        var cases = new (string Name, Action Check)[]
        {
            ("同机删除后保存旧编辑须保留删除状态和草稿", () => StaleDraftAfterDelete(Path.Combine(work, "deleted-draft"))),
            ("草稿保存保留同机随后更改的置顶与完成状态", () => ConcurrentLocalState(Path.Combine(work, "local-state"))),
            ("恢复备份只进入历史，正常在线捕获仍可接收", () => RestoredCaptureIsHistoryOnly(Path.Combine(work, "backup-sync")).GetAwaiter().GetResult()),
            ("有效备份可修复已有的损坏原图", () => BackupRepairsLocalImage(Path.Combine(work, "repair-image"))),
            ("备份后部附件损坏时不留下前部孤立原图", () => FailedImportLeavesNoImage(Path.Combine(work, "rejected-backup"))),
            ("历史和回收站支持 Unicode 大小写搜索", () => UnicodeSearch(Path.Combine(work, "search"))),
            ("待办按完成状态筛选后再应用列表上限", () => FilterBeforeLimit(Path.Combine(work, "todo-filter")))
        };
        var failures = new List<Exception>();
        foreach (var (name, check) in cases)
        {
            var ok = false;
            try
            {
                check();
                ok = true;
            }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException(name, error));
            }
            report(name, ok);
        }
        if (failures.Count > 0) throw new AggregateException("独立回归检查未通过", failures);
    }

    private static void StaleDraftAfterDelete(string root)
    {
        using var store = new Store(root);
        var original = store.Save(new Entry { Body = "已保存的内容" });
        var draft = new Draft(original with { Body = "尚未提交的修改" }, store.Clock(original.Id));
        store.Draft(original.Id, draft);
        store.Delete(original.Id);

        var rejected = false;
        try { store.Save(draft.Value, baseClock: draft.BaseClock, draftKey: original.Id); }
        catch (InvalidOperationException) { rejected = true; }
        Require(store.Get(original.Id)?.Deleted == true, "旧草稿意外恢复了已删除条目");
        Require(rejected, "已删除条目的旧草稿保存没有报告失败");
        Require(store.GetDraft(original.Id)?.Value.Body == draft.Value.Body, "保存被拒绝后草稿丢失");
        store.Restore(original.Id);
        Require(store.Get(original.Id)?.Deleted == false, "显式恢复没有生效");
    }

    private static void ConcurrentLocalState(string root)
    {
        using var store = new Store(root);
        var clip = store.Save(new Entry { Body = "置顶前的正文" });
        var clipClock = store.Clock(clip.Id);
        var clipDraft = clip with { Body = "置顶后保存的正文" };
        store.Draft(clip.Id, new Draft(clipDraft, clipClock));
        store.SetPinned(clip.Id, true);
        store.Save(clipDraft, baseClock: clipClock, draftKey: clip.Id);
        var clipPreserved = store.Get(clip.Id) is { Pinned: true } currentClip
            && currentClip.Body == clipDraft.Body && currentClip.CreatedAt == clip.CreatedAt;

        var todo = store.Save(new Entry { Kind = "todo", Title = "待核对" });
        var todoClock = store.Clock(todo.Id);
        var todoDraft = todo with { Title = "已补充标题" };
        store.Draft(todo.Id, new Draft(todoDraft, todoClock));
        store.SetDone(todo.Id, true);
        store.Save(todoDraft, baseClock: todoClock, draftKey: todo.Id);
        var todoPreserved = store.Get(todo.Id) is { Done: true } currentTodo && currentTodo.Title == todoDraft.Title;
        Require(clipPreserved && todoPreserved, $"旧草稿保存后的状态验证失败（剪贴置顶与时间 {clipPreserved}，待办完成 {todoPreserved}）");
    }

    private static async Task RestoredCaptureIsHistoryOnly(string root)
    {
        using var origin = new Store(Path.Combine(root, "origin"));
        using var publisher = new Store(Path.Combine(root, "publisher"));
        using var receiver = new Store(Path.Combine(root, "receiver"));
        var crypto = new SyncCrypto(SyncCrypto.GenerateKey());
        using var remote = new MemoryRemote(crypto);
        var publish = new SyncEngine(publisher, remote, crypto, "test/");
        var receive = new SyncEngine(receiver, remote, crypto, "test/");
        var liveIds = new List<string>();
        receive.LiveCopy += change => liveIds.Add(change.Value.Id);

        await publish.Run();
        await receive.Run();
        await receive.Run();
        await Task.Delay(20);
        var captured = origin.Save(new Entry { Body = "备份里的近期捕获" }, "capture");
        var path = Path.Combine(root, "recent.clipdesk.zip");
        Backup.Export(origin, path);
        Require(Backup.Import(publisher, path) == 1, "恢复测试未导入源变更");
        await publish.Run();
        await receive.Run();
        Require(receiver.Get(captured.Id)?.Body == captured.Body, "恢复的记录没有到达接收者的历史");
        Require(liveIds.Count == 0, "恢复的捕获事件触发了在线复制");

        await Task.Delay(20);
        var fresh = publisher.Save(new Entry { Body = "在线新捕获" }, "capture");
        await publish.Run();
        await receive.Run();
        Require(liveIds.SequenceEqual([fresh.Id]), "真正的在线捕获未单独通知接收者");
    }

    private static void BackupRepairsLocalImage(string root)
    {
        using var origin = new Store(Path.Combine(root, "source"));
        using var destination = new Store(Path.Combine(root, "destination"));
        var id = origin.Assets.Put(RedImage);
        var entry = origin.Save(new Entry { Format = "image", Assets = [id] });
        var path = Path.Combine(root, "valid.clipdesk.zip");
        Backup.Export(origin, path);
        Backup.Import(destination, path);
        File.WriteAllBytes(destination.Assets.PathFor(id), BlueImage);

        Require(Backup.Import(destination, path) == 0, "重复恢复不应再生成记录版本");
        Require(destination.Get(entry.Id)?.Assets.SequenceEqual([id]) == true, "备份恢复修改了附件标识");
        Require(destination.Assets.Read(id).SequenceEqual(RedImage), "有效备份未替换已损坏的本机原图");
    }

    private static void FailedImportLeavesNoImage(string root)
    {
        using var origin = new Store(Path.Combine(root, "source"));
        using var destination = new Store(Path.Combine(root, "destination"));
        var first = origin.Assets.Put(RedImage);
        var second = origin.Assets.Put(BlueImage);
        origin.Save(new Entry { Format = "image", Assets = [first, second] });
        var path = Path.Combine(root, "damaged.clipdesk.zip");
        Backup.Export(origin, path);
        var secondPath = "assets/" + second + ".png";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry(secondPath)!.Delete();
            using var brokenImage = zip.CreateEntry(secondPath).Open();
            brokenImage.Write(RedImage);
        }

        ExpectFailure<InvalidDataException>(() => Backup.Import(destination, path));
        Require(destination.AllChanges().Count == 0 && destination.PendingCount == 0, "损坏备份留下了记录或待上传事件");
        Require(!File.Exists(destination.Assets.PathFor(first)), "损坏备份留下了前部的孤立原图");
    }

    private static void UnicodeSearch(string root)
    {
        using var store = new Store(root);
        var entry = store.Save(new Entry { Title = "ÉCOLE", Body = "双语剪贴" });
        Require(store.List("clip", "école").Any(item => item.Id == entry.Id), "历史搜索漏掉 Unicode 大小写匹配");
        store.Delete(entry.Id);
        Require(store.List(search: "école", deleted: true).Any(item => item.Id == entry.Id), "回收站搜索漏掉 Unicode 大小写匹配");
    }

    private static void FilterBeforeLimit(string root)
    {
        using var store = new Store(root);
        var now = Codec.Now;
        var oldTask = store.Save(new Entry { Kind = "todo", Title = "较早的未完成任务", CreatedAt = now - 1000 });
        var newTask = store.Save(new Entry { Kind = "todo", Title = "较新的已完成任务", CreatedAt = now, Done = true });
        Require(store.List("todo", limit: 1, done: false).SingleOrDefault()?.Id == oldTask.Id, "已完成任务占用了未完成视图的限额");
        Require(store.List("todo", limit: 1, done: true).SingleOrDefault()?.Id == newTask.Id, "已完成任务筛选结果不正确");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectFailure<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new InvalidOperationException("预期操作失败：" + typeof(T).Name);
    }
}
