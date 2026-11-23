using ClipDesk.App;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClipDesk.WindowChecks;

internal static class NativeEvidenceChecks
{
    private const uint Child = 0x40000000;
    private const uint Disabled = 0x08000000;
    private const uint ReadOnly = 0x0800;
    private const uint Password = 0x0020;

    internal static void Run(Action<string, bool> check)
    {
        using var source = new HwndSource(new HwndSourceParameters("ClipDesk 隐藏控件自检")
        {
            Width = 120, Height = 24, WindowStyle = unchecked((int)0x80000000)
        });

        Verify("原生 Win32 普通 EDIT 提供可写证据", "EDIT", 0, true);
        Verify("原生 Win32 只读 EDIT 拒绝可写证据", "EDIT", ReadOnly, false);
        Verify("原生 Win32 密码 EDIT 拒绝可写证据", "EDIT", Password, false);
        Verify("原生 Win32 禁用 EDIT 拒绝可写证据", "EDIT", Disabled, false);
        Verify("原生 Win32 非编辑 STATIC 无可写证据", "STATIC", 0, null);

        void Verify(string name, string windowClass, uint style, bool? expected)
        {
            var window = CreateWindowEx(0, windowClass, "", Child | style, 0, 0, 120, 24, source.Handle, 0, 0, 0);
            if (window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法创建隔离 Win32 测试控件");
            try { check(name, Native.StandardEditIsWritable(window) == expected); }
            finally { DestroyWindow(window); }
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string windowClass, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);
}
