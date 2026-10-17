// V6ThemeHook - a clean, file-free RAM theme injector for VOCALOID6 Editor.
// Mechanism: registered via DOTNET_STARTUP_HOOKS (official .NET runtime
// feature). When the VOCALOID6 process starts, this DLL hooks WPF's internal
// color parser (MS.Internal.Parsers.ParseColor) with Lib.Harmony and swaps
// original colors to target colors in memory only. Nothing on disk changes.
// All code must stay defensive: any failure only disables theming, never
// crashes the host application.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows;

// Startup-hook contract: the runtime looks for a TOP-LEVEL (global namespace)
// type named 'StartupHook' with a static 'Start()' method. A namespaced
// StartupHook.Program does NOT satisfy it (TypeLoadException seen in practice).
public static class StartupHook
{
    // .NET 8.0.21+ looks for static StartupHook.Initialize()
    public static void Initialize() => Run();

    // older runtimes look for StartupHook.Program.Start()
    public static class Program
    {
        public static void Start() => Run();
    }

    private static void Run()
    {
        try
        {
            V6RamTheme.Bootstrap.Run();
        }
        catch (Exception ex)
        {
            try { V6RamTheme.Log.Write("FATAL: " + ex); } catch { }
        }
    }
}

namespace V6RamTheme
{
    internal static class Bootstrap
    {
        internal static string Mode = "off";
        internal static string ActiveToken = "off";
        // tint mode: when set, every saturated color is re-hued to TintHue
        // (and its saturation pulled toward TintSat), preserving alpha,
        // lightness and structure. -1 = no tinting.
        internal static double TintHue = -1;
        internal static double TintSat = 0.85;
        // strength of hue-tinting applied to dark NEUTRAL panels (menu bar,
        // title bar, toolbars). 0 keeps them gray; 0.35 is a moderate tint.
        internal static double BgTint = 0.35;
        internal static string TintAccent = null;

        // Inertia wheel scrolling for the piano-roll (and any other stock
        // ScrollViewer). Unlike the colors, this is live behaviour rather than
        // BAML-parsed state, so toggling it applies without restarting (see
        // SmoothScroll.cs). Off by default - we do not change how the editor
        // scrolls unless the user asks for it.
        internal static bool Smooth = false;

        // "平滑播放": when the view jumps to follow the playhead (or to a
        // selection), glide it there instead of teleporting. Same live-not-cached
        // reasoning as Smooth, so it toggles without a restart. Off by default.
        internal static bool SmoothPlay = false;

        // Frame-rate cap for the glides, in Hz. 0 = uncapped, i.e. follow the
        // panel (a 165 Hz display drives 165 steps/s). On a high-refresh panel
        // every extra step is an extra host layout pass, so capping to 60 or 120
        // trades a little smoothness for less stutter on the big virtualized
        // canvases. Live switch, applies to the next glide.
        internal static double MaxFps = 0;

        // Single source of truth for "what does this accent actually look like".
        //
        // The tint algorithm keeps each source color's LIGHTNESS and only swaps
        // hue/saturation - so a raw #RRGGBB swatch and a tinted UI element are
        // NOT the same color (they differ in lightness). Anything that displays
        // the accent itself (preset swatch, save button, preview) must go
        // through this resolver so every surface agrees.
        internal static Color? ResolveAccent(string token)
        {
            if (string.IsNullOrEmpty(token) || token == "off") return null;
            if (token.Length != 7 || token[0] != '#') return null;
            try
            {
                byte r = Convert.ToByte(token.Substring(1, 2), 16);
                byte g = Convert.ToByte(token.Substring(3, 2), 16);
                byte b = Convert.ToByte(token.Substring(5, 2), 16);
                return Color.FromRgb(r, g, b);
            }
            catch { return null; }
        }

        // Tolerant bool parser for v6.cfg / env values: 1|true|on|yes => true,
        // 0|false|off|no => false; anything unrecognised => false (safe default).
        internal static bool ParseBool(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            switch (v.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "on":
                case "yes":
                    return true;
                default:
                    return false;
            }
        }


        // Apply a theme token at runtime (hot-switch support). Mirrors the
        // startup parsing in Run() but touches only the live switches; the
        // process keeps running. Invalid tokens fall back to "off" (original).
        internal static void ApplyToken(string token, double sat, double bg)
        {
            if (string.IsNullOrEmpty(token) || token == "off")
            {
                Mode = "off";
                TintHue = -1;
                TintAccent = null;
                return;
            }
            if (token.StartsWith("#") && token.Length == 7 && TryAccentHue(token, out var hue))
            {
                Mode = "tint";
                TintHue = hue;
                TintAccent = token;
                TintSat = Math.Min(1.0, Math.Max(0.3, sat));
                BgTint = Math.Min(1.0, Math.Max(0.0, bg));
            }
            else
            {
                Mode = "off";
                TintHue = -1;
                TintAccent = null;
            }
        }
        internal static bool TryAccentHue(string hex, out double hue)
        {
            hue = -1;
            try
            {
                byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                ColorPatch.RgbToHsl(r, g, b, out hue, out var s, out _);
                return s > 0.03; // a gray accent would tint nothing
            }
            catch { return false; }
        }

        private static bool _patchTried;
        private static readonly object _lock = new object();

        public static void Run()
        {
            var ownDir = Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location);
            Log.Init(ownDir);

            if (Environment.GetEnvironmentVariable("V6_THEME_DISABLE") == "1")
            {
                Log.Write("disabled via V6_THEME_DISABLE=1");
                return;
            }

            // Resolve sibling DLLs (0Harmony) from our own folder. Our folder is
            // not in the app's probing path, and we must not copy anything into it.
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    var name = new AssemblyName(e.Name).Name;
                    var f = Path.Combine(ownDir, name + ".dll");
                    if (File.Exists(f)) return Assembly.LoadFrom(f);
                }
                catch (Exception ex) { Log.Write("resolve fail " + ex.Message); }
                return null;
            };

            string token = (Environment.GetEnvironmentVariable("V6_ACCENT") ?? "").Trim();
            if (token.Length == 0)
            {
                var f = Path.Combine(ownDir, "v6.theme");
                if (File.Exists(f))
                {
                    try { token = (File.ReadAllLines(f).FirstOrDefault() ?? "").Trim(); } catch { }
                }
            }
            if (token.Length == 0) token = "off";
            ActiveToken = token;

            // strength defaults come from v6.cfg (written by ThemeWindow),
            // environment variables override them.
            var cfgFile = Path.Combine(ownDir, "v6.cfg");
            if (File.Exists(cfgFile))
            {
                try
                {
                    foreach (var ln in File.ReadAllLines(cfgFile))
                    {
                        var kv = ln.Split('=');
                        if (kv.Length != 2) continue;
                        if (kv[0].Trim() == "sat" && double.TryParse(kv[1].Trim(),
                                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s0))
                            TintSat = Math.Min(1.0, Math.Max(0.3, s0));
                        if (kv[0].Trim() == "bg" && double.TryParse(kv[1].Trim(),
                                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b0))
                            BgTint = Math.Min(1.0, Math.Max(0.0, b0));
                        if (kv[0].Trim() == "smooth")
                            Smooth = ParseBool(kv[1].Trim());
                        if (kv[0].Trim() == "smoothplay")
                            SmoothPlay = ParseBool(kv[1].Trim());
                        if (kv[0].Trim() == "fps" && double.TryParse(kv[1].Trim(),
                                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f0))
                            MaxFps = Math.Max(0.0, f0);
                    }
                }
                catch { }
            }

            Log.Write("cfg read: file=" + cfgFile + " exists=" + File.Exists(cfgFile)
                + " smooth=" + (Smooth ? "on" : "off")
                + " smoothplay=" + (SmoothPlay ? "on" : "off")
                + " sat=" + TintSat.ToString("F2") + " bg=" + BgTint.ToString("F2"));

            // Environment override works in every mode (scrolling is independent
            // of the colour mode).
            var smoothEnv = (Environment.GetEnvironmentVariable("V6_SMOOTH") ?? "").Trim();
            if (smoothEnv.Length > 0) Smooth = ParseBool(smoothEnv);
            var smoothPlayEnv = (Environment.GetEnvironmentVariable("V6_SMOOTHPLAY") ?? "").Trim();
            if (smoothPlayEnv.Length > 0) SmoothPlay = ParseBool(smoothPlayEnv);
            var fpsEnv = (Environment.GetEnvironmentVariable("V6_MAXFPS") ?? "").Trim();
            if (double.TryParse(fpsEnv, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var fv))
                MaxFps = Math.Max(0.0, fv);

            if (token.StartsWith("#") && token.Length == 7)
            {
                // ---- full-page tint mode: recolor whole UI around one accent ----
                if (!TryAccentHue(token, out var hue))
                {
                    Log.Write("bad accent value: " + token + " (need #RRGGBB)");
                    token = "off";
                    Mode = "off";
                    ActiveToken = token;
                    Log.Write("mode=off after bad accent");
                }
                else
                {
                    Mode = "tint";
                    TintHue = hue;
                    TintAccent = token;
                    var satEnv = (Environment.GetEnvironmentVariable("V6_TINT_SAT") ?? "").Trim();
                    if (double.TryParse(satEnv, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var sv))
                    {
                        TintSat = Math.Min(1.0, Math.Max(0.3, sv));
                    }
                    var bgEnv = (Environment.GetEnvironmentVariable("V6_BG_TINT") ?? "").Trim();
                    if (double.TryParse(bgEnv, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var bv))
                    {
                        BgTint = Math.Min(1.0, Math.Max(0.0, bv));
                    }
                    Log.Write("mode=tint accent=" + token + " hue=" + hue.ToString("F1") + " sat=" + TintSat.ToString("F2") + " bg=" + BgTint.ToString("F2") + " smooth=" + (Smooth ? "on" : "off") + " smoothplay=" + (SmoothPlay ? "on" : "off"));
                }
            }
            else
            {
                Mode = "off";
                Log.Write("mode=off (original colors; menu still installed; smooth=" + (Smooth ? "on" : "off") + " smoothplay=" + (SmoothPlay ? "on" : "off") + ")");
            }

            // Register the wheel class handler BEFORE ThemeMenu.Install(). The
            // startup hook runs on the host's main thread, which is the thread
            // WPF later uses as its Dispatcher - so the class handler lands on
            // the Dispatcher that will actually route input. ThemeMenu calls
            // Install() again once the UI is confirmed up; the call is
            // idempotent, so the second one is only a safety net.
            SmoothScroll.Install();
            PlaybackSmooth.Install();

            // Usage clock: starts before the menu is built so the displayed total
            // is live from the first frame the item exists. Wrapped internally;
            // a failure here must never stop the theme from working.
            try { UsageStats.Install(ownDir); } catch (Exception ex) { Log.Write("usage install fail: " + ex.Message); }

            // Per-project stopwatch (user-picked target; see ProjectTimer for why
            // it cannot be automatic).
            try { ProjectTimer.Install(ownDir); } catch (Exception ex) { Log.Write("project timer install fail: " + ex.Message); }

            // install the "主题" menu into the editor menu bar (works in every
            // mode including off, so the menu can never lock itself out)
            ThemeMenu.Install(ownDir);

            // PresentationCore may already be loaded (unlikely) or load later.
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "PresentationCore") { TryPatch(a); break; }

            AppDomain.CurrentDomain.AssemblyLoad += (s, e) =>
            {
                if (e.LoadedAssembly.GetName().Name == "PresentationCore") TryPatch(e.LoadedAssembly);
            };

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                Log.Write("exit. replacements=" + ColorPatch.Count);
                // Final checkpoint: the 30s cadence means an abrupt exit would
                // otherwise drop up to 30s of the session from the total.
                try { UsageStats.Flush(); } catch { }
            };
        }

        private static void TryPatch(Assembly presentationCore)
        {
            lock (_lock) { if (_patchTried) return; _patchTried = true; }
            try
            {
                var parsers = presentationCore.GetType("MS.Internal.Parsers", false);
                if (parsers == null) { Log.Write("MS.Internal.Parsers not found"); return; }

                // Verified signatures (net8.0 WPF):
                //   ParseColor(string, IFormatProvider) -> Color
                //   ParseColor(string, IFormatProvider, ITypeDescriptorContext) -> Color
                //   ParseHexColor(string) -> Color   (called internally by the above)
                // We patch all Color-returning parse methods. Double-patching is
                // safe: no target color is itself a map key, so the second pass
                // never re-triggers.
                var targets = parsers
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m =>
                    {
                        var n = m.Name;
                        if (m.ReturnType != typeof(Color)) return false;
                        return n == "ParseColor" || n == "ParseHexColor" || n == "ParseContextColor";
                    })
                    .ToList();

                if (targets.Count == 0) { Log.Write("no Color-returning parse methods"); return; }

                var instance = new HarmonyLib.Harmony("v6-ram-theme");
                var postMethod = new HarmonyLib.HarmonyMethod(typeof(ColorPatch).GetMethod("Post", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

                int ok = 0;
                foreach (var m in targets)
                {
                    try
                    {
                        if (instance.Patch(m, null, postMethod, null, null) != null)
                        {
                            ok++;
                            Log.Write("patched: " + Describe(m));
                        }
                    }
                    catch (Exception ex) { Log.Write("patch fail: " + Describe(m) + " -> " + ex.Message); }
                }
                Log.Write(ok > 0 ? "hook active (" + ok + "/" + targets.Count + " methods)"
                                 : "hook INACTIVE - app will use original colors");
            }
            catch (Exception ex)
            {
                Log.Write("TryPatch failed: " + ex);
            }
        }

        private static string Describe(MethodBase m)
        {
            var ps = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
            return m.DeclaringType?.Name + "." + m.Name + "(" + ps + ")";
        }
    }

    internal static class ColorPatch
    {
        public static long Count;

        // Our own UI (ThemeWindow, ThemeMenu templates) must keep its literal
        // colors. Anything it parses - XAML strings via XamlReader, hex codes -
        // travels through the very methods we patch, so without this guard the
        // settings window would see its own palette tinted by the active accent
        // (all six preset swatches rendering as one hue). The guard is a simple
        // depth counter so nested parsing can't clear it early.
        [ThreadStatic] private static int _bypass;

        internal static IDisposable Suppress()
        {
            _bypass++;
            return new Releaser();
        }

        private sealed class Releaser : IDisposable
        {
            private bool _done;
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (_bypass > 0) _bypass--;
            }
        }

        // Harmony postfix bound to __result: the Color produced by WPF's parser.
        // (live map) tinted ARGB -> original ARGB, recorded by Post. Used by
        // ReapplyTint to re-tint (or restore) on hot-switch without restart.
        private static readonly Dictionary<uint, uint> TintedToOriginal = new Dictionary<uint, uint>();

        // Reusable core: applies CURRENT Bootstrap tint settings to one color.
        internal static Color? TryTint(Color c)
        {
            if (_bypass > 0) return null;
            if (Bootstrap.TintHue < 0) return null;
            RgbToHsl(c.R, c.G, c.B, out var h, out var s, out var l);
            if (s > 0.12)
            {
                bool srcRed = h < 0.055 || h > 0.945;
                bool accentRed = Bootstrap.TintHue < 0.055 || Bootstrap.TintHue > 0.945;
                if (srcRed && !accentRed) return null;
                var ns = Math.Min(1.0, Math.Max(0.30, s * Bootstrap.TintSat));
                var res = HslToRgb(Bootstrap.TintHue, ns, l);
                return Color.FromArgb(c.A, res.Item1, res.Item2, res.Item3);
            }
            if (Bootstrap.BgTint > 0 && l < 0.34)
            {
                var ns = Math.Min(0.5, Bootstrap.BgTint * 0.5);
                var res = HslToRgb(Bootstrap.TintHue, ns, l);
                return Color.FromArgb(c.A, res.Item1, res.Item2, res.Item3);
            }
            return null;
        }

        // Harmony postfix bound to __result: the Color produced by WPF's parser.
        public static void Post(ref Color __result)
        {
            if (_bypass > 0) return;
            var rec = TryTint(__result);
            if (rec.HasValue)
            {
                TintedToOriginal[Pack(__result)] = Pack(rec.Value);
                __result = rec.Value;
                Count++;
                if (Count == 1) Log.Write("first tint applied");
            }
        }

        private static uint Pack(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

        // Hot-switch: walk the live visual trees and resource dictionaries and
        // re-tint every color we have seen before.
        internal static void ReapplyTint()
        {
            int n = 0;
            try
            {
                using (Suppress())
                {
                    var wins = new System.Collections.Generic.List<System.Windows.Window>();
                    var app = System.Windows.Application.Current;
                    if (app != null)
                    {
                        foreach (var w in app.Windows) if (w is System.Windows.Window win) wins.Add(win);
                        n += PatchDictionary(app.Resources);
                    }
                    for (int wi = 0; wi < wins.Count; wi++)
                    {
                        n += WalkVisual(wins[wi]);
                        n += PatchDictionary(wins[wi].Resources);
                    }
                }
            }
            catch (Exception ex) { Log.Write("reapply fail: " + ex.Message); }
            Log.Write("hot reapply done, updated=" + n);
        }

        private static int WalkVisual(DependencyObject node)
        {
            int n = 0;
            if (node is System.Windows.Media.SolidColorBrush scb)
            {
                uint orig;
                if (TintedToOriginal.TryGetValue(Pack(scb.Color), out orig))
                {
                    var c0 = Unpack(orig);
                    var rec = Bootstrap.TintHue >= 0 ? TryTint(c0) : (Color?)null;
                    scb.Color = rec ?? c0;
                    n++;
                }
            }
            int cnt = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < cnt; i++) n += WalkVisual(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            return n;
        }

        private static Color Unpack(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

        private static int PatchDictionary(System.Windows.ResourceDictionary rd)
        {
            int n = 0;
            if (rd == null) return 0;
            foreach (var key in rd.Keys)
            {
                var v = rd[key];
                if (v is System.Windows.Media.SolidColorBrush scb)
                {
                    uint orig;
                    if (TintedToOriginal.TryGetValue(Pack(scb.Color), out orig))
                    {
                        var c0 = Unpack(orig);
                        var rec = Bootstrap.TintHue >= 0 ? TryTint(c0) : (Color?)null;
                        rd[key] = new System.Windows.Media.SolidColorBrush(rec ?? c0);
                        n++;
                    }
                }
                else if (v is System.Windows.Media.Color col)
                {
                    uint orig;
                    if (TintedToOriginal.TryGetValue(Pack(col), out orig))
                    {
                        var c0 = Unpack(orig);
                        var rec = Bootstrap.TintHue >= 0 ? TryTint(c0) : (Color?)null;
                        rd[key] = rec ?? c0;
                        n++;
                    }
                }
            }
            foreach (var m in rd.MergedDictionaries) n += PatchDictionary(m);
            return n;
        }

        // HSL in [0,1]; hue degrees/360
        internal static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            l = (max + min) / 2.0;
            if (max == min) { h = 0; s = 0; return; }
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == rd) h = (gd - bd) / d + (gd < bd ? 6 : 0);
            else if (max == gd) h = (bd - rd) / d + 2;
            else h = (rd - gd) / d + 4;
            h /= 6.0;
        }

        internal static Tuple<byte, byte, byte> HslToRgb(double h, double s, double l)
        {
            double Hue2Rgb(double p, double q, double t)
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
                if (t < 1.0 / 2.0) return q;
                if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
                return p;
            }
            double r, g, b;
            if (s == 0) { r = g = b = l; }
            else
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                double p = 2.0 * l - q;
                r = Hue2Rgb(p, q, h + 1.0 / 3.0);
                g = Hue2Rgb(p, q, h);
                b = Hue2Rgb(p, q, h - 1.0 / 3.0);
            }
            return Tuple.Create((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
    }

    internal static class Log
    {
        private static string _path;
        private static readonly object _writeLock = new object();

        public static void Init(string dir)
        {
            _path = Path.Combine(dir, "v6theme.log");
        }

        public static void Write(string msg)
        {
            if (_path == null) return;
            lock (_writeLock)
            {
                try
                {
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " [pid " + System.Diagnostics.Process.GetCurrentProcess().Id + "] " + msg + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}
