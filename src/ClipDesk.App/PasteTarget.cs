using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Interop;

namespace ClipDesk.App;

internal enum PasteResult
{
    Requested,
    PickerInactive,
    ClipboardChanged,
    TargetUnavailable,
    FocusNotRestored,
    InputBusy,
    InputRejected
}

internal sealed class PasteTarget
{
    private const uint RootWindow = 2;
    private const int RestoreTimeoutMs = 300;
    private readonly nint window, focusWindow;
    private readonly uint thread, process, focusThread, focusProcess;
    private readonly int[]? runtimeId;
    private readonly bool standardEdit;
    private readonly TextBox? localInput;
    private readonly EditorWindow? localWindow;
    private readonly string? localText;
    private readonly int selectionStart, selectionLength;
    private bool used;

    private PasteTarget(nint window, nint focusWindow, uint thread, uint process,
        uint focusThread, uint focusProcess, int[]? runtimeId = null, bool standardEdit = false,
        TextBox? localInput = null, EditorWindow? localWindow = null)
    {
        this.window = window;
        this.focusWindow = focusWindow;
        this.thread = thread;
        this.process = process;
        this.focusThread = focusThread;
        this.focusProcess = focusProcess;
        this.runtimeId = runtimeId;
        this.standardEdit = standardEdit;
        this.localInput = localInput;
        this.localWindow = localWindow;
        localText = localInput?.Text;
        selectionStart = localInput?.SelectionStart ?? 0;
        selectionLength = localInput?.SelectionLength ?? 0;
    }

    // A stalled accessibility provider must not queue further paste probes.
    private static readonly SemaphoreSlim probeSlot = new(1, 1);

    internal static PasteTarget? Capture()
    {
        var window = Native.GetForegroundWindow();
        var thread = Native.GetWindowThreadProcessId(window, out var process);
        if (!Native.TryGetForegroundFocus(window, thread, process, out var focus)) return null;
        var focusThread = Native.GetWindowThreadProcessId(focus, out var focusProcess);
        if (focusThread == 0 || focusProcess == 0) return null;
        if (process == Environment.ProcessId)
        {
            if (Keyboard.FocusedElement is TextBox box && box.IsVisible && box.IsEnabled && !box.IsReadOnly
                && Window.GetWindow(box) is EditorWindow editor && editor.Kind is "todo" or "memo"
                && (ReferenceEquals(box, editor.BodyInput) || box.GetType() == typeof(TextBox))
                && new WindowInteropHelper(editor).Handle == window)
                return new(window, focus, thread, process, focusThread, focusProcess, localInput: box, localWindow: editor);
            return null;
        }
        return CaptureExternal(window, thread, process, focus, focusThread, focusProcess);
    }

    private static PasteTarget? CaptureExternal(nint window, uint thread, uint process, nint focus, uint focusThread, uint focusProcess)
    {
        try
        {
            var native = Native.StandardEditIsWritable(focus);
            if (native == false) return null;
            if (native == true)
            {
                return Native.TryGetForegroundFocus(window, thread, process, out var currentFocus) && currentFocus == focus
                    && Native.GetWindowThreadProcessId(focus, out var currentPid) == focusThread && currentPid == focusProcess
                    ? new(window, focus, thread, process, focusThread, focusProcess, null, standardEdit: true)
                    : null;
            }

            int[]? id = null;
            if (probeSlot.Wait(0))
            {
                var probe = Task.Run(() =>
                {
                    try
                    {
                        var request = new CacheRequest();
                        request.Add(AutomationElement.RuntimeIdProperty);
                        request.Add(AutomationElement.ProcessIdProperty);
                        request.Add(AutomationElement.HasKeyboardFocusProperty);
                        request.Add(AutomationElement.IsPasswordProperty);
                        request.Add(AutomationElement.IsEnabledProperty);
                        request.Add(AutomationElement.ControlTypeProperty);
                        AutomationElement? element;
                        using (request.Activate()) element = AutomationElement.FocusedElement;
                        if (element == null) return (Rejected: false, Id: (int[]?)null);
                        var provider = element.Cached.ProcessId;
                        if (provider != process && provider != focusProcess) return (Rejected: false, Id: (int[]?)null);
                        if (!element.Cached.IsEnabled || element.Cached.IsPassword) return (Rejected: true, Id: (int[]?)null);
                        var runtime = element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[];
                        return (Rejected: false, Id: runtime is { Length: > 0 } ? runtime : null);
                    }
                    catch { return (Rejected: false, Id: (int[]?)null); }
                    finally { probeSlot.Release(); }
                });
                if (probe.Wait(35))
                {
                    var result = probe.GetAwaiter().GetResult();
                    if (result.Rejected) return null;
                    id = result.Id;
                }
            }

            return Native.GetForegroundWindow() == window && Native.IsWindow(focus)
                && Native.GetWindowThreadProcessId(focus, out var currentProcess) == focusThread && currentProcess == focusProcess
                ? new(window, focus, thread, process, focusThread, focusProcess, id, standardEdit: false)
                : null;
        }
        catch { return null; }
    }

    private static bool EditableFocusedElement(nint window, nint focus, uint process, uint inputProcess, int[]? expectedRuntimeId)
    {
        var request = new CacheRequest();
        request.Add(AutomationElement.ProcessIdProperty);
        request.Add(AutomationElement.HasKeyboardFocusProperty);
        request.Add(AutomationElement.IsEnabledProperty);
        request.Add(AutomationElement.IsPasswordProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.NativeWindowHandleProperty);
        AutomationElement? element;
        using (request.Activate()) element = AutomationElement.FocusedElement;
        if (element == null) return focus != 0;
        var properties = element.Cached;
        var providerProcess = properties.ProcessId;
        if (providerProcess != process && providerProcess != inputProcess) return focus != 0;
        if (!properties.IsEnabled || properties.IsPassword) return false;
        var controlWindow = (nint)properties.NativeWindowHandle;
        if (controlWindow != 0 && (Native.GetAncestor(controlWindow, RootWindow) != window
            || controlWindow != focus && !Native.IsChild(focus, controlWindow) && !Native.IsChild(controlWindow, focus)))
            return false;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            return !((ValuePattern)value).Current.IsReadOnly;
        var type = properties.ControlType;
        if ((type == ControlType.Edit || type == ControlType.Document || type == ControlType.Custom)
            && element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
        {
            var selections = ((TextPattern)text).GetSelection();
            if (selections.Length > 0)
                return selections.All(range => range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not true);
        }
        if (expectedRuntimeId != null && element.GetRuntimeId() is { Length: > 0 } id && id.SequenceEqual(expectedRuntimeId))
            return true;
        return focus != 0;
    }

    internal async Task<PasteResult> PasteAfterHide(uint clipboardSequence, nint pickerWindow, Action hidePicker)
    {
        if (used || pickerWindow == 0 || !Native.IsWindowVisible(pickerWindow)
            || Native.GetForegroundWindow() != pickerWindow
            || Native.GetWindowThreadProcessId(pickerWindow, out var pickerProcess) == 0 || pickerProcess != Environment.ProcessId)
            return PasteResult.PickerInactive;
        used = true;
        if (!ClipboardMatches(clipboardSequence)) return PasteResult.ClipboardChanged;
        var keyWait = Stopwatch.StartNew();
        while (!Native.PasteKeysReleased())
        {
            if (keyWait.ElapsedMilliseconds >= 150) return PasteResult.InputBusy;
            await Task.Delay(15);
            if (!ClipboardMatches(clipboardSequence)) return PasteResult.ClipboardChanged;
        }
        try
        {
            hidePicker();
            if (!ClipboardMatches(clipboardSequence)) return PasteResult.ClipboardChanged;
            if (!WindowMatches()) return PasteResult.TargetUnavailable;
            var afterHide = Native.GetForegroundWindow();
            if (afterHide != window)
            {
                Native.SetForegroundWindow(window);
            }
            return await PasteRestored(clipboardSequence, pickerWindow, afterHide);
        }
        catch { return PasteResult.TargetUnavailable; }
    }

    internal async Task<bool> Paste(uint clipboardSequence)
    {
        if (used || Native.GetForegroundWindow() != window) return false;
        used = true;
        try { return await PasteRestored(clipboardSequence, 0, 0) == PasteResult.Requested; }
        catch { return false; }
    }

    private async Task<PasteResult> PasteRestored(uint sequence, nint picker, nint interim)
    {
        var elapsed = Stopwatch.StartNew();
        var targetWasForeground = false;
        var inputBusy = false;
        while (elapsed.ElapsedMilliseconds < RestoreTimeoutMs)
        {
            if (!ClipboardMatches(sequence)) return PasteResult.ClipboardChanged;
            if (!WindowMatches()) return PasteResult.TargetUnavailable;
            var foreground = Native.GetForegroundWindow();
            if (foreground != window)
            {
                if (targetWasForeground || foreground != 0 && foreground != picker && foreground != interim)
                    return PasteResult.FocusNotRestored;
            }
            else
            {
                targetWasForeground = true;
                if (Native.TryGetForegroundFocus(window, thread, process, out var current)
                    && (localInput != null || ExternalFocusMatches(current)))
                {
                    inputBusy = !Native.PasteKeysReleased();
                    if (!inputBusy)
                    {
                        if (localInput != null && localWindow != null)
                            return PasteLocal(localInput, localWindow, sequence) ? PasteResult.Requested : PasteResult.TargetUnavailable;
                        if (await ExternalEditorMatchesAsync(elapsed))
                        {
                            if (!ClipboardMatches(sequence)) return PasteResult.ClipboardChanged;
                            if (elapsed.ElapsedMilliseconds >= RestoreTimeoutMs) return PasteResult.FocusNotRestored;
                            return Native.PasteOnce(window, thread, process, focusWindow, sequence)
                                ? PasteResult.Requested : PasteResult.InputRejected;
                        }
                    }
                }
            }
            await Task.Delay(15);
        }
        return inputBusy ? PasteResult.InputBusy : PasteResult.FocusNotRestored;
    }

    private bool ExternalFocusMatches(nint current) => current == focusWindow
        && Native.GetWindowThreadProcessId(current, out var pid) == focusThread && pid == focusProcess;
    private async Task<bool> ExternalEditorMatchesAsync(Stopwatch elapsed)
    {
        var native = Native.StandardEditIsWritable(focusWindow);
        if (standardEdit) return native == true;
        if (native == false) return false;
        var remaining = Math.Min(65, RestoreTimeoutMs - (int)elapsed.ElapsedMilliseconds);
        if (remaining <= 0 || !probeSlot.Wait(0)) return focusWindow != 0;
        var probe = Task.Run(() =>
        {
            try
            {
                return EditableFocusedElement(window, focusWindow, process, focusProcess, runtimeId);
            }
            catch { return focusWindow != 0; }
            finally { probeSlot.Release(); }
        });
        try { return await probe.WaitAsync(TimeSpan.FromMilliseconds(remaining)); }
        catch (TimeoutException) { return focusWindow != 0; }
    }
    private bool PasteLocal(TextBox input, EditorWindow editor, uint sequence)
    {
        if (!editor.IsVisible || !input.IsVisible || !input.IsEnabled || input.IsReadOnly || input.Text != localText
            || new WindowInteropHelper(editor).Handle != window || Window.GetWindow(input) != editor)
            return false;
        if (!input.IsKeyboardFocused && !input.Focus()) return false;
        if (!input.IsKeyboardFocused || !TargetMatches(sequence) || Native.IsEnterDown()) return false;
        input.Select(selectionStart, selectionLength);
        var textAccepted = false;
        DataObjectPastingEventHandler validate = (_, args) =>
        {
            if (!input.IsKeyboardFocused || input.IsReadOnly || !TargetMatches(sequence) || Native.IsEnterDown()
                || args.DataObject.GetData(DataFormats.UnicodeText, false) is not string text || text.Length == 0
                || !TargetMatches(sequence))
            {
                args.CancelCommand();
                return;
            }
            var data = new System.Windows.DataObject();
            data.SetText(text, TextDataFormat.UnicodeText);
            args.DataObject = data;
            args.FormatToApply = DataFormats.UnicodeText;
            textAccepted = true;
        };
        System.Windows.DataObject.AddPastingHandler(input, validate);
        try { input.Paste(); return textAccepted; }
        finally { System.Windows.DataObject.RemovePastingHandler(input, validate); }
    }
    private bool WindowMatches() => Native.IsWindow(window) && Native.IsWindowVisible(window) && !Native.IsIconic(window)
        && Native.GetWindowThreadProcessId(window, out var pid) == thread && pid == process;
    private bool TargetMatches(uint sequence) => Native.TryGetForegroundFocus(window, thread, process, out _)
        && ClipboardMatches(sequence);
    private static bool ClipboardMatches(uint sequence) => sequence != 0 && Native.GetClipboardSequenceNumber() == sequence;
}
