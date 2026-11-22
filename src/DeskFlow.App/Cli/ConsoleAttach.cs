using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskFlow.App.Cli;

/// <summary>
/// WinExe 默认没有控制台。被 cmd / PowerShell / 计划任务拉起时附加到父进程控制台，
/// 命令行输出才看得见（否则只能读日志文件）。
/// </summary>
public static class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    public static void TryAttach()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            return;
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
    }

    public static void Detach() => FreeConsole();
}
