using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipDesk.App;

/// <summary>在专用非交互窗口站中验证真实 Win32 剪贴板，未注入输入。</summary>
internal static class NativeChecks
{
    internal static void Run(string[] args)
    {
        // The entry thread may already own a WPF/COM window. Windows only allows a
        // thread with no windows or hooks to attach to a newly created desktop.
        var thread = new Thread(() => RunIsolated(args));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    internal static void RunIsolated(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("CLIPDESK_DATA");
        if (string.IsNullOrWhiteSpace(root))
        {
            Console.Error.WriteLine("原生检查需要指定 CLIPDESK_DATA");
            Environment.ExitCode = 1;
            return;
        }
        Directory.CreateDirectory(root);
        var report = Path.Combine(root, "native-check.json");
        var result = new Dictionary<string, object>();
        try
        {
            var isolation = WindowStation.Enter();
            result["初始窗口站"] = isolation.InitialStation;
            result["独立窗口站"] = isolation.Station;
            result["独立桌面"] = isolation.Desktop;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _ = app.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    isolation.Verify();
                    result["隔离已核验"] = true;
                    File.WriteAllText(report, Codec.Json(result));
                    await CheckClipboard(Path.Combine(root, "isolated-" + Codec.Id()), result);
                    result["交互桌面焦点及按键注入"] = "未执行";
                }
                catch (Exception ex) { result["error"] = ex.ToString(); }
                finally
                {
                    File.WriteAllText(report, Codec.Json(result));
                    app.Shutdown(result.ContainsKey("error") ? 1 : 0);
                }
            });
            Environment.ExitCode = app.Run();
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
            File.WriteAllText(report, Codec.Json(result));
            Environment.ExitCode = 1;
        }
    }

    private static async Task CheckClipboard(string dataPath, Dictionary<string, object> result)
    {
        using var writer = new HwndSource(new HwndSourceParameters("ClipDesk 隔离检查")
        {
            ParentWindow = new nint(-3), Width = 0, Height = 0
        });
        string capturedId, imageId;
        using (var store = new Store(dataPath))
        using (var service = new ClipboardService(store))
        {
            var text = "ClipDesk 隔离验收 " + Codec.Id() + "\n第二行：你好，桌面。";
            const string html = "<html><body><b>你好，桌面</b></body></html>";
            const string rtf = @"{\rtf1\ansi Windows \u27979?\u35797?}";
            var fixture = new System.Windows.DataObject();
            fixture.SetData(DataFormats.Html, html);
            fixture.SetData(DataFormats.Rtf, rtf);
            fixture.SetText(text);
            Clipboard.SetDataObject(fixture, true);
            var captured = await FindEntry(store, entry => entry.Body == text, "监听合成文字");
            capturedId = captured.Id;
            using var process = Process.GetCurrentProcess();
            Check(result, "实际剪贴板所有者与 Unicode 文字", captured.SourceApp == process.ProcessName);
            Check(result, "捕获 HTML 与 RTF", captured.Html == html && captured.Rtf == rtf);

            var before = store.List("clip").Count;
            var copied = await service.Copy(captured);
            Check(result, "写回返回本次 Win32 序号", copied != 0 && copied == Native.GetClipboardSequenceNumber());
            var data = Clipboard.GetDataObject();
            Check(result, "写回 HTML、RTF 与 Unicode", data != null && data.GetData(DataFormats.Html) as string == html
                && data.GetData(DataFormats.Rtf) as string == rtf && Clipboard.GetText() == text);
            await Task.Delay(250);
            Check(result, "自身写回未重复收录", store.List("clip").Count == before);

            var changed = captured with { Body = "修改后的合成文字", Protected = true };
            store.Save(changed);
            Check(result, "保存未改系统剪贴板", Clipboard.GetText() == text);

            var absent = new string('a', 64);
            await service.Copy(captured with { Assets = [absent] }, plain: true);
            Check(result, "缺图仍可复制纯文本", Clipboard.GetText() == text
                && Clipboard.GetDataObject()?.GetDataPresent(DataFormats.Html, false) == false
                && Clipboard.GetDataObject()?.GetDataPresent(DataFormats.Rtf, false) == false);

            await CheckPrivacy(writer.Handle, store, process.ProcessName, result);
            await CheckCompetition(writer.Handle, store, service, captured, result);


            var pixels = Enumerable.Repeat((byte)200, 48 * 32 * 4).ToArray();
            var bitmap = BitmapSource.Create(48, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 48 * 4);
            bitmap.Freeze();
            Clipboard.SetImage(bitmap);
            _ = await FindEntry(store, entry => entry.Assets.Count > 0, "监听系统位图");
            result["实际系统位图进入历史"] = true;

            var png = ClipboardService.ImageBytes(bitmap);
            var pngFormat = Native.ClipboardFormat("PNG");
            var pngId = AssetStore.Hash(png);
            WriteFixture(writer.Handle, payload => payload.Add(pngFormat, png));
            var image = await FindEntry(store, entry => entry.Assets.Contains(pngId), "监听原始 PNG");
            Check(result, "原始 PNG 字节保持一致", store.Assets.Read(pngId).SequenceEqual(png));

            await service.Copy(image with { Assets = [pngId, absent] });
            var readback = Clipboard.GetImage();
            Check(result, "首图可独立写回且系统位图尺寸一致", readback?.PixelWidth == 48 && readback.PixelHeight == 32);
            Check(result, "写回 PNG 字节保持一致", ReadPng(writer.Handle, pngFormat) is { } raw && raw.AsSpan().StartsWith(png));

            var memo = store.Convert(image, "memo", "isolated-native-image");
            imageId = memo.Id;
            store.Delete(image.Id);
            Check(result, "删除来源后独立附件有效", store.Assets.Exists(pngId));
            CheckHotkeys(writer.Handle, service, result);
        }
        using var reopened = new Store(dataPath);
        Check(result, "隔离数据库重开后仍存在", reopened.Get(capturedId)?.Body == "修改后的合成文字"
            && reopened.Get(imageId)?.Assets.Count == 1);
    }

    private static async Task CheckPrivacy(nint writer, Store store, string process, Dictionary<string, object> result)
    {
        var flags = new (string Format, byte[] Data)[]
        {
            ("Clipboard Viewer Ignore", [1]),
            ("ExcludeClipboardContentFromMonitorProcessing", [1]),
            ("CanIncludeInClipboardHistory", BitConverter.GetBytes(0u)),
            ("CanUploadToCloudClipboard", BitConverter.GetBytes(0u)),
            ("CanIncludeInClipboardHistory", [0xff])
        };
        foreach (var (format, value) in flags)
        {
            var count = store.List("clip").Count;
            var outbox = store.PendingCount;
            WriteFixture(writer, payload =>
            {
                AddText(payload, "合成敏感标记测试 " + Codec.Id());
                payload.Add(Native.ClipboardFormat(format), value);
            });
            await Task.Delay(500);
            if (store.List("clip").Count != count || store.PendingCount != outbox)
                throw new InvalidOperationException("敏感标记未阻止历史或同步队列：" + format);
        }
        result["四种敏感标记及异常 DWORD 跳过入库和同步"] = true;

        var allowed = "合成 DWORD 1 测试 " + Codec.Id();
        WriteFixture(writer, payload =>
        {
            AddText(payload, allowed);
            payload.Add(Native.ClipboardFormat("CanIncludeInClipboardHistory"), BitConverter.GetBytes(1u));
            payload.Add(Native.ClipboardFormat("CanUploadToCloudClipboard"), BitConverter.GetBytes(1u));
        });
        _ = await FindEntry(store, entry => entry.Body == allowed, "DWORD 1 放行");
        result["允许值 DWORD 1 正常收录"] = true;

        var settings = store.Settings;
        store.SaveSettings(settings with { ExcludedApps = process + ".exe" });
        var existing = store.List("clip").Count;
        WriteFixture(writer, payload => AddText(payload, "合成来源排除测试 " + Codec.Id()));
        await Task.Delay(500);
        Check(result, "按实际所有者进程排除", store.List("clip").Count == existing);
        store.SaveSettings(settings);
    }

    private static async Task CheckCompetition(nint writer, Store store, ClipboardService service, Entry entry,
        Dictionary<string, object> result)
    {
        using var stale = new Native.ClipboardWrite();
        AddText(stale, "合成旧复制");
        var previous = Native.GetClipboardSequenceNumber();
        var newer = "合成新复制 " + Codec.Id();
        WriteFixture(writer, payload => AddText(payload, newer));
        Check(result, "原生序号改变时旧写回让位", stale.TryCommit(writer, previous, out var written)
            && written == 0 && Clipboard.GetText() == newer);
        _ = await FindEntry(store, candidate => candidate.Body == newer, "收录较新剪贴板");

        var settings = store.Settings;
        store.SaveSettings(settings with { Sync = settings.Sync with { Enabled = true, ReceiveClipboard = true } });
        var checks = 0;
        var invalidated = await service.Copy(entry, remoteCopyTime: service.LastLocalCopy + 3000,
            remoteStillCurrent: () => ++checks == 1);
        Check(result, "远端写回提交前复核连接", checks >= 2 && invalidated == 0 && Clipboard.GetText() == newer);

        checks = 0;
        var local = "远端提交前发生的合成本地复制 " + Codec.Id();
        var superseded = await service.Copy(entry, remoteCopyTime: service.LastLocalCopy + 3000,
            remoteStillCurrent: () =>
            {
                if (++checks == 2) WriteFixture(writer, payload => AddText(payload, local));
                return true;
            });
        Check(result, "远端写回让位于较新本地复制", checks >= 2 && superseded == 0 && Clipboard.GetText() == local);
        _ = await FindEntry(store, candidate => candidate.Body == local, "保留本地新复制");

        var count = store.List("clip").Count;
        var outbox = store.PendingCount;
        var copied = await service.Copy(entry, remoteCopyTime: service.LastLocalCopy + 3000);
        await Task.Delay(250);
        Check(result, "远端写回成功且没有回环", copied != 0 && Clipboard.GetText() == entry.Body
            && store.List("clip").Count == count && store.PendingCount == outbox);
        store.SaveSettings(settings);
    }

    private static void CheckHotkeys(nint writer, ClipboardService service, Dictionary<string, object> result)
    {
        const uint ctrlAlt = 0x0003;
        const uint f10 = 0x79, f11 = 0x7A;
        if (!Native.RegisterHotKey(writer, 5, ctrlAlt, f11))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 1459) throw new Win32Exception(error, "无法在独立站预检测试热键");
            result["独立窗口站热键注册"] = "未执行，系统要求交互窗口站，Win32 1459";
            return;
        }
        Native.UnregisterHotKey(writer, 5);
        service.Register("Ctrl+Alt+F11");
        service.Register("ctrl+alt+f11");
        result["重复设置同一组合幂等"] = true;
        if (!Native.RegisterHotKey(writer, 3, ctrlAlt, f10))
            throw new InvalidOperationException("隔离检查无法预占合成测试组合");
        try
        {
            var conflict = false;
            try { service.Register("Ctrl+Alt+F10"); }
            catch (InvalidOperationException) { conflict = true; }
            var originalReleased = Native.RegisterHotKey(writer, 4, ctrlAlt, f11);
            if (originalReleased) Native.UnregisterHotKey(writer, 4);
            Check(result, "热键冲突保留原注册", conflict && !originalReleased);
        }
        finally { Native.UnregisterHotKey(writer, 3); }
    }

    private static byte[]? ReadPng(nint writer, uint format)
    {
        if (!Native.OpenClipboard(writer)) throw new InvalidOperationException("隔离检查无法打开剪贴板");
        try
        {
            return Native.ReadClipboardBytes(format, AssetStore.MaxBytes + 16);
        }
        finally { Native.CloseClipboard(); }
    }

    private static void AddText(Native.ClipboardWrite payload, string text) =>
        payload.Add(Native.ClipboardUnicodeText, Encoding.Unicode.GetBytes(text + '\0'));

    private static void WriteFixture(nint owner, Action<Native.ClipboardWrite> setup)
    {
        using var payload = new Native.ClipboardWrite();
        setup(payload);
        if (!payload.TryCommit(owner, Native.GetClipboardSequenceNumber(), out var sequence) || sequence == 0)
            throw new InvalidOperationException("无法写入隔离的剪贴板检查数据");
    }

    private static async Task<Entry> FindEntry(Store store, Func<Entry, bool> match, string name)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (store.List("clip").FirstOrDefault(match) is { } found) return found;
            await Task.Delay(40);
        }
        throw new InvalidOperationException(name + "超时");
    }

    private static void Check(Dictionary<string, object> result, string name, bool passed)
    {
        result[name] = passed;
        if (!passed) throw new InvalidOperationException(name + "未通过");
    }

    private sealed record WindowStation(string InitialStation, string Station, string Desktop)
    {
        private const uint CreateOnly = 1;
        private const uint WindowStationAccess = 0x037F, DesktopAccess = 0x01FF;
        private const int ObjectFlags = 1, ObjectName = 2;
        private const uint Visible = 1;
        [StructLayout(LayoutKind.Sequential)]
        private struct UserObjectFlags { internal int Inherit, Reserved; internal uint Flags; }

        [DllImport("user32.dll", EntryPoint = "CreateWindowStationW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern nint CreateWindowStation(string? name, uint flags, uint access, nint attributes);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessWindowStation(nint station);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetProcessWindowStation();
        [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint attributes);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(nint desktop);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool GetObjectName(nint handle, int index, StringBuilder value, uint size, out uint needed);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, SetLastError = true)]
        private static extern bool GetObjectFlags(nint handle, int index, out UserObjectFlags value, uint size, out uint needed);

        internal static WindowStation Enter()
        {
            var original = Name(GetProcessWindowStation());
            // 使用系统按登录会话分配的名称，CREATE_ONLY 拒绝复用现有窗口站。
            var station = CreateWindowStation(null, CreateOnly, WindowStationAccess, 0);
            if (station == 0) throw Failure("无法新建独立窗口站，已中止剪贴板检查");
            var name = Name(station);
            if (name.Equals(original, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("窗口站名称与初始站相同，已中止剪贴板检查");
            VerifyNonInteractive(station);
            if (!SetProcessWindowStation(station)) throw Failure("无法附加到独立窗口站，已中止剪贴板检查");
            const string desktopName = "ClipDeskSynthetic";
            var desktop = CreateDesktop(desktopName, 0, 0, 0, DesktopAccess, 0);
            if (desktop == 0) throw Failure("无法新建独立桌面，已中止剪贴板检查");
            if (!SetThreadDesktop(desktop)) throw Failure("无法附加到独立桌面，已中止剪贴板检查");
            // 检查进程退出时由系统释放站和桌面句柄。进程始终停留在独立站。
            var result = new WindowStation(original, name, desktopName);
            result.Verify();
            return result;
        }

        internal void Verify()
        {
            var station = GetProcessWindowStation();
            if (!Name(station).Equals(Station, StringComparison.OrdinalIgnoreCase)
                || InitialStation.Equals(Station, StringComparison.OrdinalIgnoreCase)
                || !Name(GetThreadDesktop(GetCurrentThreadId())).Equals(Desktop, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("实际窗口站或桌面与隔离配置不一致，已中止剪贴板检查");
            VerifyNonInteractive(station);
        }

        private static void VerifyNonInteractive(nint station)
        {
            if (!GetObjectFlags(station, ObjectFlags, out var flags, (uint)Marshal.SizeOf<UserObjectFlags>(), out _))
                throw Failure("无法核对独立窗口站的可见状态，已中止剪贴板检查");
            if ((flags.Flags & Visible) != 0)
                throw new InvalidOperationException("窗口站可见，已中止剪贴板检查");
        }

        private static string Name(nint handle)
        {
            var value = new StringBuilder(256);
            if (handle == 0 || !GetObjectName(handle, ObjectName, value, (uint)value.Capacity * sizeof(char), out _))
                throw Failure("无法核对窗口站或桌面名称");
            return value.ToString();
        }

        private static Win32Exception Failure(string message) => new(Marshal.GetLastWin32Error(), message);
    }
}
