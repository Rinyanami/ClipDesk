using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ClipDesk.WindowChecks")]

namespace ClipDesk.App;

internal enum HotkeyRemoval
{
    Removed,
    Missing,
    Failed
}

internal sealed class GlobalHotkeys(Func<int, uint, uint, bool> register, Func<int, HotkeyRemoval> unregister) : IDisposable
{
    private const uint Alt = 0x0001, Ctrl = 0x0002, Shift = 0x0004, NoRepeat = 0x4000;
    private const int FirstId = 40, LastId = 0xBFFF;

    private readonly record struct Chord(uint Modifiers, uint Key);
    private sealed record Shortcut(string Action, string Window, string Text, Chord Chord);
    private sealed record Binding(int Id, Shortcut Shortcut);

    private readonly Dictionary<Chord, Binding> registered = [];
    private readonly Dictionary<int, Binding> uncertain = [];
    private Dictionary<int, string> actions = [];
    private LocalSettings current = new() { Hotkey = "" };
    private int nextId = FirstId;
    private bool disposed;

    internal string? ActionFor(nint id) =>
        id >= FirstId && id <= LastId ? actions.GetValueOrDefault((int)id) : null;

    internal void Register(string clipboard) => Register(current with { Hotkey = clipboard });

    internal IReadOnlyList<string> RegisterStartup(LocalSettings settings)
    {
        var problems = new List<string>();
        TryAdd(current with { Hotkey = settings.Hotkey });
        TryAdd(current with { PinnedHotkey = settings.PinnedHotkey });
        TryAdd(current with { TodoHotkey = settings.TodoHotkey });
        TryAdd(current with { MemoHotkey = settings.MemoHotkey });
        return problems;

        void TryAdd(LocalSettings desired)
        {
            try { Register(desired); }
            catch (InvalidOperationException ex) { problems.Add(ex.Message); }
        }
    }

    internal void Register(LocalSettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var desired = Parse(settings);
        foreach (var binding in uncertain.Values.ToArray())
        {
            if (TryUnregister(binding.Id) == HotkeyRemoval.Failed)
                throw new InvalidOperationException($"无法清理{binding.Shortcut.Window}快捷键，请退出 ClipDesk 后重试。");
            uncertain.Remove(binding.Id);
        }

        var added = new List<Binding>();
        var removed = new List<Binding>();
        foreach (var shortcut in desired.Values)
        {
            if (registered.ContainsKey(shortcut.Chord)) continue;
            var id = AllocateId();
            if (id == 0) throw RollBack($"没有可用的{shortcut.Window}快捷键标识。", added, removed);
            var binding = new Binding(id, shortcut);
            if (!TryRegister(binding, out var error))
            {
                // 原生失败返回 false。异常发生时无法确定注册是否已生效。
                if (error != null && TryUnregister(id) == HotkeyRemoval.Failed) uncertain.Add(id, binding);
                throw RollBack($"无法注册{shortcut.Window}快捷键“{shortcut.Text}”，请更换组合。", added, removed, error);
            }
            registered.Add(shortcut.Chord, binding);
            added.Add(binding);
        }

        foreach (var binding in registered.Values.Where(item => !desired.ContainsKey(item.Shortcut.Chord)).ToArray())
        {
            var result = TryUnregister(binding.Id);
            if (result == HotkeyRemoval.Removed)
            {
                registered.Remove(binding.Shortcut.Chord);
                removed.Add(binding);
                continue;
            }

            // 先确认旧组合是否仍在，再决定是否重新注册。
            if (result == HotkeyRemoval.Failed) result = TryUnregister(binding.Id);
            if (result != HotkeyRemoval.Failed)
            {
                registered.Remove(binding.Shortcut.Chord);
                removed.Add(binding);
            }
            throw RollBack($"无法注销{binding.Shortcut.Window}快捷键“{binding.Shortcut.Text}”。", added, removed,
                originalStateUncertain: result == HotkeyRemoval.Failed);
        }

        var newActions = new Dictionary<int, string>();
        foreach (var shortcut in desired.Values)
        {
            var binding = registered[shortcut.Chord];
            registered[shortcut.Chord] = binding with { Shortcut = shortcut };
            newActions.Add(binding.Id, shortcut.Action);
        }
        actions = newActions;
        current = settings;
    }

    private static Dictionary<Chord, Shortcut> Parse(LocalSettings settings)
    {
        var configured = new[]
        {
            (Action: "clipboard", Window: "剪贴板窗口", Text: settings.Hotkey),
            (Action: "pinned", Window: "置顶窗口", Text: settings.PinnedHotkey),
            (Action: "todo", Window: "待办窗口", Text: settings.TodoHotkey),
            (Action: "memo", Window: "备忘窗口", Text: settings.MemoHotkey)
        };
        var result = new Dictionary<Chord, Shortcut>();
        foreach (var (action, window, text) in configured)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var chord = ParseChord(window, text);
            if (result.TryGetValue(chord, out var existing))
                throw new InvalidOperationException($"{existing.Window}和{window}使用了同一快捷键“{text.Trim()}”。");
            result.Add(chord, new(action, window, text.Trim(), chord));
        }
        return result;
    }

    private static Chord ParseChord(string window, string text)
    {
        uint modifiers = 0;
        uint key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "ALT" => Alt,
                "CTRL" or "CONTROL" => Ctrl,
                "SHIFT" => Shift,
                _ => 0u
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) throw InvalidChord(window);
                modifiers |= modifier;
            }
            else
            {
                if (key != 0) throw InvalidChord(window);
                key = ParseKey(part);
                if (key == 0) throw InvalidChord(window);
            }
        }
        if (modifiers == 0 || key == 0) throw InvalidChord(window);
        return new(modifiers, key);
    }

    private static uint ParseKey(string part)
    {
        if (part.Length == 1 && char.IsAsciiDigit(part[0])) return part[0];
        if (part.Length == 0 || !char.IsAsciiLetter(part[0]) || !part.All(char.IsAsciiLetterOrDigit) ||
            !Enum.TryParse<Key>(part, ignoreCase: true, out var key) || !Enum.IsDefined(key))
            return 0;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0x7B or 0xE5 or 0xE7 or (>= 0xA0 and <= 0xA5)
            ? 0 : vk;
    }

    private static InvalidOperationException InvalidChord(string window) =>
        new($"{window}快捷键格式无效。请使用 Ctrl、Alt 或 Shift 加一个主键，例如 Ctrl+Shift+V。F12 和 Windows 键不可用。");

    private int AllocateId()
    {
        for (var attempt = FirstId; attempt <= LastId; attempt++)
        {
            var id = nextId++;
            if (nextId > LastId) nextId = FirstId;
            if (!uncertain.ContainsKey(id) && !registered.Values.Any(binding => binding.Id == id))
                return id;
        }
        return 0;
    }

    private bool TryRegister(Binding binding, out Exception? error)
    {
        try
        {
            error = null;
            var chord = binding.Shortcut.Chord;
            return register(binding.Id, chord.Modifiers | NoRepeat, chord.Key);
        }
        catch (Exception ex) { error = ex; return false; }
    }

    private HotkeyRemoval TryUnregister(int id)
    {
        try { return unregister(id); }
        catch (Exception) { return HotkeyRemoval.Failed; }
    }

    private InvalidOperationException RollBack(string message, List<Binding> added, List<Binding> removed,
        Exception? inner = null, bool originalStateUncertain = false)
    {
        var complete = !originalStateUncertain;
        for (var index = added.Count - 1; index >= 0; index--)
        {
            var binding = added[index];
            if (TryUnregister(binding.Id) == HotkeyRemoval.Failed) complete = false;
            else registered.Remove(binding.Shortcut.Chord);
        }
        foreach (var binding in removed)
        {
            if (TryRegister(binding, out var error)) registered.Add(binding.Shortcut.Chord, binding);
            else
            {
                if (error != null) uncertain.Add(binding.Id, binding);
                complete = false;
            }
        }

        var registeredIds = registered.Values.Select(binding => binding.Id).ToHashSet();
        actions = actions.Where(pair => registeredIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var suffix = complete && uncertain.Count == 0
            ? "原快捷键仍保留。"
            : "部分热键状态无法恢复，请重新应用设置，或退出 ClipDesk 后重试。";
        return new InvalidOperationException(message + suffix, inner);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var binding in registered.Values) TryUnregister(binding.Id);
        foreach (var id in uncertain.Keys) TryUnregister(id);
        registered.Clear();
        uncertain.Clear();
        actions.Clear();
    }
}
