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

            // The editor MUST be given a console.
            //
            // VOCALOIDPatcher (a separate old injector that also lives in this
            // process) calls Console.OutputEncoding during Patcher.Initialize().
            // With no console that throws "invalid handle", it shows a modal
            // "VOCALOID Patcher Error" dialog, and VOCALOID6 never creates its
            // main window - the editor looks dead.
            //
            // Two previous attempts were wrong, and both were only caught because
            // the failure was reproduced by hand:
            //
            //   1. `cmd /c` via UseShellExecute=false, CreateNoWindow=false
            //      -> works, but leaves a black console window on screen for the
            //         whole session (cmd /c waits for its child).
            //
            //   2. `UseShellExecute = true` (what the official V6Launcher does)
            //      -> looked fine in testing, but ONLY because every test was
            //         launched from PowerShell, whose console the launcher
            //         inherited. Double-clicked from Explorer there is no console
            //         to inherit and the patcher error comes straight back.
            //         Testing from a console hid the bug - do not trust it.
            //
            // What actually works: cmd /c WITH an explicitly hidden console
            // (CREATE_NO_WINDOW). The child gets a real console handle, and no
            // window is ever shown.
            //
            // The hook must be set here rather than inside the cmd payload:
            // ProcessStartInfo.EnvironmentVariables is applied to the child's
            // environment directly, so the editor sees it without a second `set`.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(editor),
            };
            psi.EnvironmentVariables["DOTNET_STARTUP_HOOKS"] = hook;

            var cmd = "/c \"" + editor + "\"";
            foreach (var a in args) cmd += " " + Quote(a);
            psi.Arguments = cmd;

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
