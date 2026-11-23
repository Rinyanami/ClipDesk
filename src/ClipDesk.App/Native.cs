using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ClipDesk.App;

internal static class Native
{
    internal const uint ClipboardUnicodeText = 13;
    internal const uint ClipboardDibV5 = 17;
    private const uint ClipboardBitmap = 2;
    private const uint MoveableMemory = 0x0002;

    [DllImport("user32.dll")] internal static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", ExactSpelling = true)]
    private static extern int GetBitmapObject(nint bitmap, int size, out BitmapObject info);

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowStyle(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { internal uint Size, Time; }
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint id, ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct BitmapObject
    { internal int Type, Width, Height, WidthBytes; internal ushort Planes, BitsPixel; internal nint Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { internal int Size; internal uint Flags; internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; internal Rect32 CaretRect; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] internal KeyboardInput Keyboard; [FieldOffset(0)] internal MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }

    internal static uint ClipboardFormat(string name)
    {
        var format = RegisterClipboardFormat(name);
        return format != 0 ? format : throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册剪贴板格式");
    }

    internal static string? ProcessName(nint window)
    {
        if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0) return null;
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return null; }
    }

    // 这些读取都由调用方在同一次 OpenClipboard / CloseClipboard 内完成。
    internal static bool IsPrivateClipboard(uint ignore, uint exclude, uint history, uint cloud) =>
        IsClipboardFormatAvailable(ignore) || IsClipboardFormatAvailable(exclude)
        || DoesNotAllowClipboardProcessing(history) || DoesNotAllowClipboardProcessing(cloud);

    private static bool DoesNotAllowClipboardProcessing(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return false;
        var memory = GetClipboardData(format);
        if (memory == 0 || GlobalSize(memory) < sizeof(uint)) return true;
        var pointer = GlobalLock(memory);
        if (pointer == 0) return true;
        try { return Marshal.ReadInt32(pointer) != 1; }
        finally { GlobalUnlock(memory); }
    }

    internal static byte[]? ReadClipboardBytes(uint format, int maxBytes)
    {
        if (!IsClipboardFormatAvailable(format)) return null;
        var memory = GetClipboardData(format);
        if (memory == 0) throw new ExternalException("剪贴板内容尚不可读");
        var size = GlobalSize(memory);
        if (size == 0) return null;
        if (size > (nuint)maxBytes) throw new InvalidDataException("剪贴板内容超过大小限制");
        var pointer = GlobalLock(memory);
        if (pointer == 0) throw new ExternalException("剪贴板内容尚不可读");
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(memory); }
    }

    internal static BitmapSource? ReadClipboardBitmap(long maxPixels)
    {
        if (!IsClipboardFormatAvailable(ClipboardBitmap)) return null;
        var bitmap = GetClipboardData(ClipboardBitmap);
        if (bitmap == 0 || GetBitmapObject(bitmap, Marshal.SizeOf<BitmapObject>(), out var info) == 0)
            throw new ExternalException("剪贴板图片尚不可读");
        if ((long)info.Width * Math.Abs((long)info.Height) > maxPixels)
            throw new InvalidDataException("图片超过 6400 万像素，请缩小后导入");
        var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        return image;
    }

    internal sealed class ClipboardWrite : IDisposable
    {
        private readonly List<(uint Format, nint Handle)> formats = [];

        internal void Add(uint format, byte[] bytes) =>
            Add(format, bytes.Length, pointer => Marshal.Copy(bytes, 0, pointer, bytes.Length));

        internal void Add(uint format, int size, Action<nint> prepare)
        {
            var handle = GlobalAlloc(MoveableMemory, (nuint)size);
            if (handle == 0) throw new OutOfMemoryException("无法准备剪贴板数据");
            try
            {
                var pointer = GlobalLock(handle);
                if (pointer == 0) throw new InvalidOperationException("无法准备剪贴板数据");
                try { prepare(pointer); }
                finally { GlobalUnlock(handle); }
                formats.Add((format, handle));
            }
            catch
            {
                GlobalFree(handle);
                throw;
            }
        }

        // 序号核对和写入共用系统剪贴板锁。已转移给系统的句柄不再释放。
        internal bool TryCommit(nint owner, uint expected, out uint written)
        {
            written = 0;
            if (!OpenClipboard(owner)) return false;
            try
            {
                if (GetClipboardSequenceNumber() != expected) return true;
                if (!EmptyClipboard()) throw new InvalidOperationException("无法清空剪贴板以复制所选内容");
                for (var i = 0; i < formats.Count; i++)
                {
                    var (format, handle) = formats[i];
                    if (SetClipboardData(format, handle) == 0)
                        throw new InvalidOperationException("剪贴板写入未完成，请重新复制");
                    formats[i] = (format, 0);
                }
            }
            finally { CloseClipboard(); }

            // Closing can synthesize additional formats and advance the sequence. A sequence
            // observed while the clipboard is still open can immediately invalidate our own copy.
            var sequence = GetClipboardSequenceNumber();
            if (sequence != 0 && GetClipboardOwner() == owner && GetClipboardSequenceNumber() == sequence)
                written = sequence;
            return true;
        }

        public void Dispose()
        {
            foreach (var (_, handle) in formats)
                if (handle != 0) GlobalFree(handle);
            formats.Clear();
        }
    }

    internal static uint? LastInputTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : null;
    }
    internal static bool PasteKeysReleased() => !new[] { 0x0D, 0x10, 0x11, 0x12, 0x56, 0x5B, 0x5C }
        .Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    internal static bool? StandardEditIsWritable(nint focus)
    {
        var name = new StringBuilder(64);
        if (GetClassName(focus, name, name.Capacity) == 0) return null;
        if (name.ToString().ToUpperInvariant() is not ("EDIT" or "RICHEDIT" or "RICHEDIT20A" or "RICHEDIT20W" or "RICHEDIT50W")) return null;
        const int styleIndex = -16, readOnlyOrPassword = 0x0800 | 0x0020;
        var style = GetWindowStyle(focus, styleIndex);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) return false;
        return IsWindowEnabled(focus) && (style & readOnlyOrPassword) == 0;
    }

    internal static bool TryGetForegroundFocus(nint window, uint thread, uint process, out nint focus)
    {
        const uint rootWindow = 2;
        const uint blockedModes = 0x001e; // 移动/调整大小、普通菜单、系统菜单、弹出菜单。
        focus = 0;
        if (window == 0 || thread == 0 || process == 0 || GetForegroundWindow() != window
            || GetWindowThreadProcessId(window, out var activeProcess) != thread || activeProcess != process)
            return false;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || (info.Flags & blockedModes) != 0
            || info.Active != window || info.Focus == 0 || GetAncestor(info.Focus, rootWindow) != window)
            return false;
        if (GetForegroundWindow() != window) return false;
        focus = info.Focus;
        return true;
    }

    internal static bool PasteOnce(nint window, uint thread, uint process, nint focus, uint sequence)
    {
        // Enter 的物理按键也需释放，避免聊天窗口收到后续按键重复。
        if (!PasteKeysReleased()
            || !TryGetForegroundFocus(window, thread, process, out var current) || current != focus
            || sequence == 0 || GetClipboardSequenceNumber() != sequence)
            return false;
        Input Make(ushort key, uint flags) => new() { Type = 1, Union = new() { Keyboard = new() { Key = key, Flags = flags } } };
        var ctrlUp = Make(0x11, 2);
        var vUp = Make(0x56, 2);
        var sent = SendInput(4, [Make(0x11, 0), Make(0x56, 0), vUp, ctrlUp], Marshal.SizeOf<Input>());
        if (sent is 1 or 3) SendInput(1, [ctrlUp], Marshal.SizeOf<Input>());
        else if (sent == 2) SendInput(2, [vUp, ctrlUp], Marshal.SizeOf<Input>());
        // V 按下已入队时不要再提示重试粘贴，避免目标收到两份内容。
        return sent >= 2;
    }

    internal static bool IsEnterDown() => (GetAsyncKeyState(0x0D) & 0x8000) != 0;
}

/// <summary>AK、SK 和同步密钥分别存入 Windows 凭据管理器，不进入 SQLite 或备份。</summary>
internal sealed class Vault(string scope)
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Credential
    {
        public uint Flags, Type; public string TargetName; public string? Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public nint CredentialBlob; public uint Persist, AttributeCount; public nint Attributes; public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, uint type, uint flags, out nint pointer);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint pointer);
    private string Name(string name) => "ClipDesk/" + scope + "/" + name;
    internal void Put(string name, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length > 2400) throw new InvalidOperationException("凭据过长");
        var ptr = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Name(name), CredentialBlobSize = (uint)bytes.Length, CredentialBlob = ptr, Persist = 2, UserName = "ClipDesk" };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("无法写入 Windows 凭据管理器，配置未保存");
        }
        finally { for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(ptr, i, 0); Marshal.FreeCoTaskMem(ptr); System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    internal string? Get(string name)
    {
        if (!CredRead(Name(name), 1, 0, out var pointer))
        { if (Marshal.GetLastWin32Error() == 1168) return null; throw new InvalidOperationException("无法读取 Windows 凭据管理器"); }
        try { var credential = Marshal.PtrToStructure<Credential>(pointer); var data = new byte[credential.CredentialBlobSize]; Marshal.Copy(credential.CredentialBlob, data, 0, data.Length); return Encoding.UTF8.GetString(data); }
        finally { CredFree(pointer); }
    }
    internal void Delete(string name)
    {
        if (name is not ("kodo-ak" or "kodo-sk")) throw new ArgumentException("只能删除本应用的七牛凭据", nameof(name));
        if (!CredDelete(Name(name), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("无法回滚 Windows 凭据管理器中的七牛凭据");
    }
}
