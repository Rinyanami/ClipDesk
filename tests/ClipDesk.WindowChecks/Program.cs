using System.Text;

namespace ClipDesk.WindowChecks;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var passed = 0;
        void Check(string name, bool condition)
        {
            if (!condition) throw new InvalidOperationException(name);
            passed++;
            Console.WriteLine("通过：" + name);
        }

        try
        {
            HotkeyChecks.Run(Check);
            NativeEvidenceChecks.Run(Check);
            Console.WriteLine($"完成 {passed} 项窗口检查。热键使用模拟后端。Win32 控件检查仅使用本进程隐藏窗口。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败：{ex}");
            return 1;
        }
    }
}
