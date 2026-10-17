// V6主题版 - windowless launcher for the VOCALOID6 theme hook.
//
// WHY THIS EXISTS: DOTNET_STARTUP_HOOKS has to name a real DLL on disk, so
// something must set that variable and then start VOCALOID6.exe. A .bat works
// but leaves a console window on screen (and `start "" cmd /c ...` kept it there
// for the whole session, because cmd /c waits for its child). This launcher is a
// WinExe, so there is no console at all.
//
// It stays a LAUNCHER rather than absorbing the DLL:
//   * the hook must exist as a file with a stable path
//   * a single-file exe would unpack itself to %TEMP% on every run, which both
//     slows startup and tends to trip antivirus heuristics
// So this exe, V6ThemeHook.dll and 0Harmony.dll sit side by side, and the exe
// adds nothing to disk at runtime.
//
// Everything is failure-tolerant in the same spirit as the hook: if anything
// goes wrong it shows a message and exits rather than starting a half-configured
// editor.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private const string HookName = "V6ThemeHook.dll";
    private const string EditorDefault = @"C:\Program Files\VOCALOID6\Editor\VOCALOID6.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var hook = Path.Combine(dir, HookName);

            if (!File.Exists(hook))
            {
                Fail("找不到 " + HookName + "\n\n" + hook +
                     "\n\n请把本程序与 " + HookName + " 放在同一个文件夹。");
                return 1;
            }

            var editor = Environment.GetEnvironmentVariable("V6_EXE");
            if (string.IsNullOrEmpty(editor)) editor = EditorDefault;
            if (!File.Exists(editor))
            {
                Fail("找不到 VOCALOID6 编辑器\n\n" + editor +
                     "\n\n如果装在别处，请设置环境变量 V6_EXE 指向 VOCALOID6.exe。");
                return 1;
            }

            // Set the hook in THIS process, then start the editor through the
            // shell. Verified on this machine:
            //
            //   UseShellExecute = true  + EnvironmentVariables[...]  -> throws
            //       "Process 对象必须将 UseShellExecute 属性设置为 False 才能使用环境变量"
            //   UseShellExecute = true  + our own Environment.SetVariable    -> the
            //       child DOES inherit it (measured with a cmd /c set probe)
            //
            // So: mutate our own environment, then let the shell start the editor
            // exactly as Explorer would. That matters because UseShellExecute=false
            // gave the child no console, and the old VOCALOIDPatcher calls
            // Console.OutputEncoding at startup - without a console it throws
            // "invalid handle" and blocks the editor behind a modal error.
            // Shell-starting avoids the whole problem: no cmd, no console, no error.
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", hook);

            var psi = new ProcessStartInfo
            {
                FileName = editor,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(editor),
            };
            if (args.Length > 0)
                psi.Arguments = string.Join(" ", Array.ConvertAll(args, Quote));

            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            Fail("启动失败：\n" + ex.Message);
            return 1;
        }
    }

    private static void Fail(string msg)
    {
        // Raw MessageBox instead of System.Windows.Forms: the launcher does not
        // otherwise touch WinForms, and dragging that dependency in for one
        // dialog would bloat the exe.
        MessageBoxW(IntPtr.Zero, msg, "V6 主题版", 0x00040030 /* MB_ICONWARNING|MB_TOPMOST */);
    }

    // Quote for cmd.exe: wrap in quotes and escape embedded quotes.
    private static string Quote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
