// ProjectStats - historical activity reconstructed from the user's own project
// folders and .vpr files.
//
// WHAT IS REAL AND WHAT IS NOT - read this before trusting any number here:
//
//   Dates: the user writes the work date INTO THE FOLDER NAME
//   ("深海少女2026.6.4"). That is the most trustworthy signal available, and it
//   is what this module prefers.
//
//   Why not the file timestamps? Measured on the real library:
//     * LastWriteTime clusters on 2026-08-08 for 24 of 48 files - a bulk
//       touch/copy, not 24 projects edited the same day.
//     * CreationTime clusters too (9 files on 2026-04-02), for the same reason.
//     * LastAccessTime is 2026-10-06 15:14 for every file - one backup pass.
//   Using modification dates would paint a false spike and hide real work, so
//   the folder-name date wins whenever it parses. Timestamps are only a fallback.
//
//   Launches / hours: NOT RECORDED ANYWHERE. A .vpr has no session log. These
//   two come from the user's own estimate, applied by a deterministic rule so
//   the same folder always yields the same numbers (see EstimateFor). They are
//   labelled 估算 in the UI and must never be presented as measurements.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace V6RamTheme
{
    internal static class ProjectStats
    {
        /// <summary>One project folder, with its real date and estimated effort.</summary>
        internal struct Project
        {
            public string Name;          // folder name, date tail stripped
            public DateTime WorkDate;    // REAL: parsed from the folder name, else fallback
            public bool DateFromName;    // true when WorkDate came from the folder name
            public int VprCount;         // REAL: how many .vpr files it holds
            public long SizeBytes;       // REAL
            public DateTime FirstCreated;// REAL: earliest .vpr creation time
            public DateTime LastModified;// REAL: newest .vpr write time in the folder
            public DateTime LastAccessed;// REAL: newest .vpr access time in the folder
            public string VprPath;       // REAL: the .vpr that drove LastModified
            public string[] VprNames;    // REAL: every .vpr file stem in this folder
            public int SpanDays;         // REAL: FirstCreated -> WorkDate
            public int EstLaunches;      // ESTIMATED
            public double EstHours;      // ESTIMATED
        }

        private static readonly object _lock = new object();
        private static List<Project> _projects;
        private static Dictionary<DateTime, int> _perDay;
        private static bool _scanned;
        private static int _fileCount;

        /// <summary>Folder that was scanned, for the window's footnote.</summary>
        public static string SourceFolder { get; private set; }

        /// <summary>Folders that carried a parsable date in their name.</summary>
        public static int DatedCount { get; private set; }

        public static int ProjectCount
        {
            get { Ensure(); lock (_lock) { return _projects.Count; } }
        }

        /// <summary>
        /// Total project FILES across the library, honouring the .vsqx/.mid
        /// switches. This is what the "工程文件" card shows: a count of files,
        /// not of folders, because "41 个" next to a folder path reads as
        /// "41 files" to most people.
        /// </summary>
        public static int FileCount
        {
            get { Ensure(); lock (_lock) { return _fileCount; } }
        }

        /// <summary>Estimated launches keyed by work day.</summary>
        public static IEnumerable<KeyValuePair<DateTime, int>> Launches
        {
            get
            {
                Ensure();
                lock (_lock)
                {
                    return _perDay
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new KeyValuePair<DateTime, int>(kv.Key, kv.Value))
                        .ToList();
                }
            }
        }

        public static IEnumerable<Project> Projects
        {
            get { Ensure(); lock (_lock) { return new List<Project>(_projects); } }
        }

        /// <summary>
        /// The project whose .vpr was written most recently, or null when the
        /// library is empty.
        ///
        /// "Current" here means "most recently saved". The editor does not tell
        /// an injected hook which file it has open (the startup hook only sees
        /// WPF colour parsing), so the newest write is the closest honest
        /// approximation. It lags if you open a project and change nothing -
        /// nothing is written, so the previous project still looks current.
        /// </summary>
        public static Project? Current
        {
            get
            {
                Ensure();
                lock (_lock)
                {
                    if (_projects == null || _projects.Count == 0) return null;
                    Project best = _projects[0];
                    foreach (var p in _projects) if (p.LastModified > best.LastModified) best = p;
                    return best;
                }
            }
        }

        /// <summary>Estimated hours of the most-recently-saved project (0 if none).</summary>
        public static double CurrentHours
        {
            get
            {
                var c = Current;
                return c.HasValue ? c.Value.EstHours : 0.0;
            }
        }

        /// <summary>
        /// Drop the cached scan and re-read the folder.
        ///
        /// Must be called after SetFolder so every derived count (project count,
        /// launches, hours) reflects the new folder. Without the cache drop the
        /// window reopened with a stale count even though the folder had
        /// changed - the numbers on screen disagreed with the folder label.
        /// </summary>
        public static void Refresh()
        {
            lock (_lock) { _scanned = false; }
            Ensure();
        }

        // ---- scanning -----------------------------------------------------

        private static string OwnDir()
        {
            try
            {
                var d = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                return string.IsNullOrEmpty(d) ? null : d;
            }
            catch { return null; }
        }

        private static string CfgPath()
        {
            var d = OwnDir();
            return d == null ? null : Path.Combine(d, "v6.cfg");
        }

        /// <summary>Folder currently used as the project source, or null.</summary>
        public static string CurrentFolder => SourceFolder ?? ResolveFolder();

        /// <summary>
        /// Persist a new project folder into v6.cfg and rescan.
        ///
        /// Only the "projects" line is touched: the rest of v6.cfg (sat/bg/
        /// smooth/smoothplay/fps) belongs to ThemeWindow and is rewritten in
        /// place, so setting the folder here cannot silently reset the theme.
        /// </summary>
        public static bool SetFolder(string folder)
        {
            try
            {
                var cfg = CfgPath();
                if (cfg == null) return false;

                var lines = new List<string>();
                if (File.Exists(cfg)) lines.AddRange(File.ReadAllLines(cfg));

                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    var eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    if (t.Substring(0, eq).Trim() != "projects") continue;
                    lines[i] = "projects=" + folder;
                    replaced = true;
                    break;
                }
                if (!replaced) lines.Add("projects=" + folder);

                File.WriteAllText(cfg, string.Join(Environment.NewLine, lines) + Environment.NewLine);

                // Drop the cached scan so the next read picks up the new folder.
                lock (_lock) { _scanned = false; }
                SourceFolder = folder;
                Log.Write("projects folder set: " + folder);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("projects set fail: " + ex.Message);
                return false;
            }
        }

        // The folder the user keeps projects in.
        //
        // Resolved at scan time, not hard-coded to one machine: the path is read
        // from v6.cfg ("projects=<path>") so a clone of this repo works for
        // anyone. A user who never sets it sees an empty calendar, which is the
        // honest result of "we do not know where your projects are".
        private static string ResolveFolder()
        {
            try
            {
                var cfg = CfgPath();
                if (cfg == null || !File.Exists(cfg)) return null;

                foreach (var raw in File.ReadAllLines(cfg))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() != "projects") continue;
                    var v = line.Substring(eq + 1).Trim();
                    if (v.Length > 0) return v;
                }
            }
            catch (Exception ex)
            {
                Log.Write("projects path read fail: " + ex.Message);
            }
            return null;
        }

        // 2026.6.4 / 2026-06-04 / 2026_6_4 - the separator varies by project.
        private static readonly Regex DateInName = new Regex(
            @"(20\d{2})[.\-_/](\d{1,2})[.\-_/](\d{1,2})",
            RegexOptions.Compiled);

        // Serialises scans. _lock guards the cached VALUES (readers take it for a
        // snapshot); _scanLock guards the scanning itself, so a Refresh() racing a
        // lazy Ensure() cannot interleave two scans and have the older result win.
        private static readonly object _scanLock = new object();

        private static void Ensure()
        {
            lock (_lock)
            {
                if (_scanned) return;
            }
            lock (_scanLock)
            {
                // Re-check under the scan lock: another thread may have just
                // finished the same scan while we waited.
                lock (_lock)
                {
                    if (_scanned) return;
                }
                ScanOnce();
                lock (_lock) { _scanned = true; }
            }
        }

        private static void ScanOnce()
        {
            try
            {
                var root = ResolveFolder();
                var list = new List<Project>();
                var exts = Extensions();
                var perDay = new Dictionary<DateTime, int>();
                var accessDays = new Dictionary<DateTime, HashSet<string>>();
                int dated = 0;
                int fileCount = 0;

                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        try
                        {
                            var name = Path.GetFileName(dir);

                            // Skip non-project folders that merely look dated.
                            var vprs = SafeProjectFiles(dir, exts);
                            if (vprs.Count == 0) continue;
                            fileCount += vprs.Count;

                            var p = new Project { Name = StripDate(name), VprCount = vprs.Count };
                            long size = 0;
                            DateTime firstCreated = DateTime.MaxValue;
                            DateTime newestWrite = DateTime.MinValue;
                            DateTime newestAccess = DateTime.MinValue;
                            string newestPath = null;
                            var stems = new List<string>();
                            foreach (var f in vprs)
                            {
                                try
                                {
                                    size += new FileInfo(f).Length;
                                    var c = File.GetCreationTime(f);
                                    if (c < firstCreated) firstCreated = c;
                                    // Newest write across the folder: this is what
                                    // "the project I touched most recently" means.
                                    var w = File.GetLastWriteTime(f);
                                    if (w > newestWrite) { newestWrite = w; newestPath = f; }
                                    // Access time is the only REAL evidence of
                                    // "I opened this on day X". Verified live:
                                    // DisableLastAccess=2 and a probe file's
                                    // stamp moved on read, so it is maintained.
                                    //
                                    // Heatmap scope is .vpr ONLY, regardless of
                                    // the .vsqx/.mid switches. Those formats are
                                    // shared with other software, so letting them
                                    // drive the calendar would attribute someone
                                    // else's file reads to a VOCALOID session.
                                    // The switches therefore affect the file
                                    // COUNT, never the calendar.
                                    if (string.Equals(Path.GetExtension(f), ".vpr",
                                            StringComparison.OrdinalIgnoreCase))
                                    {
                                        var a = File.GetLastAccessTime(f);
                                        if (a > newestAccess) newestAccess = a;
                                    }
                                    // The editor titles its window with the .vpr
                                    // FILE name, not the folder name, so detection
                                    // needs both to match a title back to a folder.
                                    var stem = Path.GetFileNameWithoutExtension(f);
                                    if (!string.IsNullOrEmpty(stem)) stems.Add(stem);
                                }
                                catch { }
                            }
                            p.VprNames = stems.ToArray();
                            p.SizeBytes = size;
                            p.FirstCreated = firstCreated == DateTime.MaxValue
                                ? DateTime.Today : firstCreated;
                            p.LastAccessed = newestAccess == DateTime.MinValue
                                ? p.FirstCreated : newestAccess;
                            p.LastModified = newestWrite == DateTime.MinValue
                                ? p.FirstCreated : newestWrite;
                            p.VprPath = newestPath;

                            // Prefer the date the user wrote in the folder name.
                            var mo = DateInName.Match(name);
                            DateTime parsed;
                            if (mo.Success && TryDate(mo, out parsed) && InRange(parsed))
                            {
                                p.WorkDate = parsed;
                                p.DateFromName = true;
                                dated++;
                            }
                            else
                            {
                                // Fallback: the earliest a file in this folder
                                // existed. Never the newest - that is the bulk
                                // 2026-08-08 touch.
                                p.WorkDate = EarliestStamp(dir, vprs);
                                p.DateFromName = false;
                            }

                            // Span first: the estimate reads it.
                            p.SpanDays = Math.Max(0, (p.WorkDate.Date - p.FirstCreated.Date).Days);
                            EstimateFor(ref p);
                            list.Add(p);

                            // Heatmap: REAL data only.
                            //
                            // "How many projects did I have open on day X" comes
                            // from .vpr access time, not from the launch estimate.
                            // One project counts once per day even if it was
                            // opened several times - access time only remembers
                            // the last access, so repeated opens are invisible
                            // and pretending otherwise would be inventing data.
                            var accessDay = p.LastAccessed.Date;
                            {
                                HashSet<string> set;
                                if (!accessDays.TryGetValue(accessDay, out set))
                                {
                                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    accessDays[accessDay] = set;
                                }
                                set.Add(p.Name);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Write("project skip (" + Path.GetFileName(dir) + "): " + ex.Message);
                        }
                    }
                }

                // Screen out mass-access days, then convert to per-day counts.
                //
                // A day on which a large majority of the whole library was
                // touched is not a work day - it is a backup pass, an antivirus
                // scan, or a copy. Real evidence from this machine: 41 of 46
                // .vpr files share one access date (2026-10-06), while the days
                // that actually correspond to work have 2-3 files.
                //
                // The rule is proportional rather than a fixed number: it has to
                // hold for a 5-project library and a 500-project one alike.
                int noiseCut = Math.Max(8, list.Count * 3 / 10);
                foreach (var kv in accessDays)
                    if (kv.Value.Count < noiseCut)
                        perDay[kv.Key] = kv.Value.Count;

                lock (_lock)
                {
                    _projects = list;
                    _perDay = perDay;
                    _fileCount = fileCount;
                    DatedCount = dated;
                    SourceFolder = root;
                }
                Log.Write("project scan: " + list.Count + " folders, " + fileCount +
                    " files, " + dated + " with a date in the name, root=" + root);
                Log.Write("heatmap: " + perDay.Count + " active days from access time (noise cut >= " +
                    noiseCut + " projects/day)");
            }
            catch (Exception ex)
            {
                Log.Write("project scan fail: " + ex.Message);
                lock (_lock)
                {
                    _projects = new List<Project>();
                    _perDay = new Dictionary<DateTime, int>();
                    _fileCount = 0;
                    DatedCount = 0;
                }
            }
        }

        // Which extra file types count as "project files" besides .vpr.
        //
        // .vpr is always included: it is VOCALOID's own format and the one the
        // editor titles its window with. The other two are OPT-IN because they
        // are shared formats written by many programs - a folder full of MIDI
        // loops would otherwise look like an extremely active project. Evidence
        // from this machine: 91 .mid files sit inside one chord-pack folder, and
        // 88 of them share a single access date from a bulk scan.
        public static bool IncludeVsqx { get; set; }
        public static bool IncludeMidi { get; set; }

        /// <summary>Read the two opt-in switches from v6.cfg.</summary>
        public static void LoadFileTypes()
        {
            try
            {
                var cfg = CfgPath();
                if (cfg == null || !File.Exists(cfg)) return;
                foreach (var raw in File.ReadAllLines(cfg))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = Bootstrap.ParseBool(line.Substring(eq + 1).Trim());
                    if (key == "extvsqx") IncludeVsqx = val;
                    else if (key == "extmidi") IncludeMidi = val;
                }
            }
            catch (Exception ex)
            {
                Log.Write("load file types fail: " + ex.Message);
            }
        }

        /// <summary>Persist the two switches, leaving every other cfg key alone.</summary>
        public static void SaveFileTypes()
        {
            try
            {
                var cfg = CfgPath();
                if (cfg == null) return;
                var lines = File.Exists(cfg)
                    ? new List<string>(File.ReadAllLines(cfg))
                    : new List<string>();
                foreach (var kv in new[] {
                    Tuple.Create("extvsqx", IncludeVsqx ? "1" : "0"),
                    Tuple.Create("extmidi", IncludeMidi ? "1" : "0") })
                {
                    bool done = false;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var t = lines[i].Trim();
                        var eq = t.IndexOf('=');
                        if (eq <= 0) continue;
                        if (t.Substring(0, eq).Trim() != kv.Item1) continue;
                        lines[i] = kv.Item1 + "=" + kv.Item2;
                        done = true;
                        break;
                    }
                    if (!done) lines.Add(kv.Item1 + "=" + kv.Item2);
                }
                File.WriteAllText(cfg, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log.Write("save file types fail: " + ex.Message);
            }
        }

        private static string[] Extensions()
        {
            var list = new List<string> { "*.vpr" };
            if (IncludeVsqx) list.Add("*.vsqx");
            if (IncludeMidi) { list.Add("*.mid"); list.Add("*.midi"); }
            return list.ToArray();
        }

        private static List<string> SafeProjectFiles(string dir, string[] patterns)
        {
            var all = new List<string>();
            foreach (var pat in patterns)
            {
                try
                {
                    all.AddRange(Directory.EnumerateFiles(dir, pat, SearchOption.AllDirectories));
                }
                catch { }
            }
            return all;
        }

        // Earliest of creation times, capped at the newest write so a
        // future-dated oddity cannot drag the whole day.
        private static DateTime EarliestStamp(string dir, List<string> vprs)
        {
            var earliest = DateTime.MaxValue;
            foreach (var f in vprs)
            {
                try
                {
                    var c = File.GetCreationTime(f);
                    if (c < earliest) earliest = c;
                }
                catch { }
            }
            if (earliest == DateTime.MaxValue)
            {
                try { earliest = Directory.GetCreationTime(dir); }
                catch { earliest = DateTime.Today; }
            }
            return earliest;
        }

        private static bool TryDate(Match m, out DateTime d)
        {
            d = DateTime.MinValue;
            int y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int mo = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int da = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (y < 2020 || y > 2100 || mo < 1 || mo > 12 || da < 1 || da > 31) return false;
            try { d = new DateTime(y, mo, da); return true; }
            catch { return false; }
        }

        // Guard against a stray future date scattering the calendar; allow a day
        // of slack for a machine in a different timezone.
        private static bool InRange(DateTime d) =>
            d >= new DateTime(2020, 1, 1) && d <= DateTime.Today.AddDays(1);

        private static string StripDate(string name)
        {
            var m = DateInName.Match(name);
            return m.Success ? name.Remove(m.Index).Trim() : name;
        }

        // ---- the estimate -------------------------------------------------
        //
        // Deterministic in the folder's own facts so repeated scans agree.
        //
        //   span   = days from the first .vpr creation to the folder-name date
        //            (how long the project was actually in progress)
        //   launches = 5 + (span % 11)                        -> 5..15
        //   hours    = 5 + min(7, log2(megabytes bucket))     -> 5..12
        //
        // A project worked on across more days was plausibly opened more often;
        // a bigger project was plausibly sat with longer. Heuristics, not data -
        // the UI labels them 估算 for exactly that reason.
        //
        // NOT based on "days until today": that would make the same project change
        // its historical numbers every time the clock advances, which is absurd
        // for something labelled as a record of the past.
        private static void EstimateFor(ref Project p)
        {
            int span = p.SpanDays < 0 ? 0 : p.SpanDays;
            p.EstLaunches = 5 + (span % 11);

            double mb = p.SizeBytes / (1024.0 * 1024.0);
            int bucket = mb < 1 ? 0 : (int)Math.Min(7, Math.Floor(Math.Log(mb, 2)));
            p.EstHours = 5 + bucket;
        }

        // ---- summaries ----------------------------------------------------

        public static int TotalEstLaunches => Projects.Sum(p => p.EstLaunches);

        public static double TotalEstHours => Projects.Sum(p => p.EstHours);

        public static string RangeText()
        {
            var ps = Projects.ToList();
            if (ps.Count == 0) return "无";
            return ps.Min(p => p.EstLaunches).ToString(CultureInfo.InvariantCulture) + "~" +
                   ps.Max(p => p.EstLaunches).ToString(CultureInfo.InvariantCulture) + " 次 / " +
                   ps.Min(p => p.EstHours).ToString("F0", CultureInfo.InvariantCulture) + "~" +
                   ps.Max(p => p.EstHours).ToString("F0", CultureInfo.InvariantCulture) + " 小时";
        }
    }
}
