// Minimal stubs so the REAL Program.cs + SmoothScroll.cs can be compiled into
// the wheel-lab harness without pulling in the settings window / menu code.
// These files replace src\ThemeMenu.cs and src\ThemeWindow.cs in the lab build
// only; the shipped hook DLL is untouched.
namespace V6RamTheme
{
    internal static class ThemeMenu
    {
        internal const string Marker = "v6-ram-theme-menu";
        public static string Dir => null;
        public static void Install(string dir) { }
    }

    internal static class ThemeWindow
    {
        public static void ShowOrFocus(string dir) { }
    }
}
