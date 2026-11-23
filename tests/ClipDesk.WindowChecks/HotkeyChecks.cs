using System.IO;
using ClipDesk.App;
using ClipDesk.Core;

namespace ClipDesk.WindowChecks;

internal static class HotkeyChecks
{
    private const uint NoRepeat = 0x4000;
    private static readonly LocalSettings Four = new()
    {
        Hotkey = "Ctrl+Shift+V",
        PinnedHotkey = "Ctrl+Alt+1",
        TodoHotkey = "Ctrl+Alt+2",
        MemoHotkey = "Ctrl+Alt+3"
    };

    public static void Run(Action<string, bool> check)
    {
        ParseAndExchange(check);
        NewBindingFailure(check);
        RestoreOldBindings(check);
        RestoreMissingBinding(check);
        CleanupUncommittedBinding(check);
        StartupConflicts(check);
    }

    private static void ParseAndExchange(Action<string, bool> check)
    {
        var native = new HotkeyBackend();
        using var hotkeys = native.Create();
        hotkeys.Register(Four);
        var clipboard = native.Id('V');
        var pinned = native.Id('1');
        var todo = native.Id('2');
        var memo = native.Id('3');
        check("模拟热键四窗口独立分派，支持数字和 MOD_NOREPEAT",
            hotkeys.ActionFor(clipboard) == "clipboard" && hotkeys.ActionFor(pinned) == "pinned" &&
            hotkeys.ActionFor(todo) == "todo" && hotkeys.ActionFor(memo) == "memo" &&
            native.Active.Values.All(chord => (chord.Modifiers & NoRepeat) != 0));
        var calls = native.Calls;
        check("模拟热键在原生调用前拒绝等价组合重复",
            Fails(() => hotkeys.Register(Four with { TodoHotkey = "alt + control + D1" }), "待办窗口") &&
            native.Calls == calls && hotkeys.ActionFor(todo) == "todo");
        check("模拟热键拒绝单修饰、重复修饰、F12、Windows 和 Mac 键",
            new[] { "Ctrl", "Ctrl+Ctrl+V", "Ctrl+F12", "Alt+LWin", "Ctrl+Mac", "Ctrl+1+2", "Ctrl++0" }
                .All(chord => Fails(() => hotkeys.Register(Four with { MemoHotkey = chord }), "备忘窗口")) &&
            native.Calls == calls);

        hotkeys.Register(Four with { Hotkey = Four.PinnedHotkey, PinnedHotkey = Four.Hotkey });
        check("模拟交换两窗口热键复用原生 ID，仅切换动作",
            native.Calls == calls && hotkeys.ActionFor(clipboard) == "pinned" && hotkeys.ActionFor(pinned) == "clipboard");
        hotkeys.Register("  ");
        check("模拟清空剪贴热键，旧 ID 不分派且保留其他窗口",
            hotkeys.ActionFor(pinned) == null && hotkeys.ActionFor(clipboard) == "pinned" &&
            hotkeys.ActionFor(todo) == "todo" && hotkeys.ActionFor(memo) == "memo");
        hotkeys.Dispose();
        check("模拟退出释放所有热键", native.Active.Count == 0);
    }

    private static void NewBindingFailure(Action<string, bool> check)
    {
        var native = new HotkeyBackend();
        using var hotkeys = native.Create();
        hotkeys.Register(Four);
        var before = native.Active.ToDictionary(pair => pair.Key, pair => pair.Value);
        native.UnavailableKeys.Add('6');
        check("模拟后续新增冲突撤销先注册的新组合并保留整组旧绑定",
            Fails(() => hotkeys.Register(Four with { Hotkey = "Ctrl+5", TodoHotkey = "Ctrl+6" }), "待办窗口") &&
            SameBindings(before, native.Active) && hotkeys.ActionFor(native.Id('V')) == "clipboard" &&
            hotkeys.ActionFor(native.Id('2')) == "todo");
    }

    private static void RestoreOldBindings(Action<string, bool> check)
    {
        var native = new HotkeyBackend();
        using var hotkeys = native.Create();
        hotkeys.Register(Four);
        var before = native.Active.ToDictionary(pair => pair.Key, pair => pair.Value);
        var pinned = native.Id('1');
        native.ThrowAfterRemovalOnce = pinned;
        check("模拟注销故障恢复已经释放的原绑定并撤销新增",
            Fails(() => hotkeys.Register(Four with { Hotkey = "Ctrl+5", PinnedHotkey = "Ctrl+6" }), "置顶窗口") &&
            SameBindings(before, native.Active) && hotkeys.ActionFor(pinned) == "pinned");
    }

    private static void RestoreMissingBinding(Action<string, bool> check)
    {
        var native = new HotkeyBackend();
        using var hotkeys = native.Create();
        hotkeys.Register(Four);
        var old = native.Id('1');
        native.MissingOnce = old;
        native.UnavailableKeys.Add('1');
        check("模拟原热键已消失且恢复失败时报告并停止分派该旧 ID",
            Fails(() => hotkeys.Register(Four with { PinnedHotkey = "Ctrl+5" }), "部分热键状态无法恢复") &&
            hotkeys.ActionFor(old) == null && hotkeys.ActionFor(native.Id('2')) == "todo" && native.Active.Count == 3);
        native.UnavailableKeys.Clear();
        hotkeys.Register(Four);
        check("模拟恢复失败后重新应用配置可补齐缺失绑定",
            native.Active.Count == 4 && hotkeys.ActionFor(native.Id('1')) == "pinned");
    }

    private static void CleanupUncommittedBinding(Action<string, bool> check)
    {
        var native = new HotkeyBackend();
        using var hotkeys = native.Create();
        hotkeys.Register(Four);
        native.UnavailableKeys.Add('6');
        native.BlockRemoveKey = '5';
        check("模拟撤销新增失败时残留 ID 不分派动作",
            Fails(() => hotkeys.Register(Four with { Hotkey = "Ctrl+5", TodoHotkey = "Ctrl+6" }), "部分热键状态无法恢复") &&
            hotkeys.ActionFor(native.Id('5')) == null && hotkeys.ActionFor(native.Id('V')) == "clipboard");
        native.BlockRemoveKey = 0;
        hotkeys.Register(Four);
        check("模拟下一次成功应用清理未提交的残留绑定", native.Active.Count == 4);
    }

    private static void StartupConflicts(Action<string, bool> check)
    {
        var native = new HotkeyBackend();
        using var hotkeys = native.Create();
        native.UnavailableKeys.Add('1');
        var problems = hotkeys.RegisterStartup(Four with { TodoHotkey = "shift+ctrl+v" });
        check("模拟启动冲突与重复单独报告，其他窗口热键可用",
            problems.Count == 2 && problems.Any(problem => problem.Contains("置顶窗口", StringComparison.Ordinal)) &&
            problems.Any(problem => problem.Contains("待办窗口", StringComparison.Ordinal)) &&
            native.Active.Count == 2 && hotkeys.ActionFor(native.Id('V')) == "clipboard" &&
            hotkeys.ActionFor(native.Id('3')) == "memo");
    }

    private static bool SameBindings(Dictionary<int, (uint Modifiers, uint Key)> before,
        Dictionary<int, (uint Modifiers, uint Key)> after)
        => before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out var chord) && chord == item.Value);

    private static bool Fails(Action operation, string message)
    {
        try { operation(); }
        catch (InvalidOperationException ex) { return ex.Message.Contains(message, StringComparison.Ordinal); }
        return false;
    }

    private sealed class HotkeyBackend
    {
        internal Dictionary<int, (uint Modifiers, uint Key)> Active { get; } = [];
        internal HashSet<uint> UnavailableKeys { get; } = [];
        internal int Calls { get; private set; }
        internal int? ThrowAfterRemovalOnce { get; set; }
        internal int? MissingOnce { get; set; }
        internal uint BlockRemoveKey { get; set; }

        internal GlobalHotkeys Create() => new(Register, Unregister);
        internal int Id(uint key) => Active.Single(item => item.Value.Key == key).Key;

        private bool Register(int id, uint modifiers, uint key)
        {
            Calls++;
            return !UnavailableKeys.Contains(key) && !Active.ContainsValue((modifiers, key)) &&
                Active.TryAdd(id, (modifiers, key));
        }

        private HotkeyRemoval Unregister(int id)
        {
            Calls++;
            if (ThrowAfterRemovalOnce == id)
            {
                ThrowAfterRemovalOnce = null;
                Active.Remove(id);
                throw new IOException("模拟原生注销已生效但返回异常");
            }
            if (MissingOnce == id) { MissingOnce = null; Active.Remove(id); return HotkeyRemoval.Missing; }
            if (!Active.TryGetValue(id, out var chord)) return HotkeyRemoval.Missing;
            if (chord.Key == BlockRemoveKey) return HotkeyRemoval.Failed;
            Active.Remove(id);
            return HotkeyRemoval.Removed;
        }
    }
}
