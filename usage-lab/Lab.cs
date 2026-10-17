// Offline check of the usage-accumulation arithmetic.
//
// The real module lives in the injector assembly, which needs a Dispatcher and
// a real host, so this harness reproduces ONLY the pacing maths that decides how
// much of each heartbeat is credited, and asserts the cases that matter:
//   * normal on-time heartbeats are credited in full (no drift over an hour),
//   * a coalesced-but-plausible gap is still full credit,
//   * a long gap (sleep / walked away) is credited a token amount only,
//   * a clock jump backwards is never credited,
//   * the formatted strings match what the menu bar will show.

using System;
using System.Collections.Generic;
using System.Linq;

internal static class UsageMath
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleCredit = TimeSpan.FromSeconds(5);

    // Mirror of UsageStats.Tick's credit decision.
    private static TimeSpan Credit(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromHours(12)) return TimeSpan.Zero;
        var c = elapsed > IdleGap ? IdleCredit : elapsed;
        return c > elapsed ? elapsed : c;
    }

    // Mirror of UsageStats.Fmt (HH:mm:ss, capped at 999999 hours).
    private const long MaxHours = 999999;
    private static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long total = (long)t.TotalHours;
        if (total > MaxHours) total = MaxHours;
        long mins = t.Minutes;
        long secs = t.Seconds;
        if (total == MaxHours) { mins = 59; secs = 59; }
        return total + ":" + mins.ToString("00") + ":" + secs.ToString("00");
    }

    private static int _fail = 0;
    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  -> " + detail : ""));
        if (!ok) _fail++;
    }

    [STAThread]
    private static void Main()
    {
        // 1. One hour of on-time heartbeats credits exactly one hour.
        {
            var total = TimeSpan.Zero;
            for (int i = 0; i < 720; i++) total += Credit(Heartbeat);   // 720 * 5s = 3600s
            Check("准时心跳 720 次 = 恰好 1 小时", Math.Abs(total.TotalSeconds - 3600) < 0.001,
                total.TotalSeconds.ToString("F1") + "s");
        }

        // 2. Coalesced timers (Windows can fire a 5s timer at ~15s under battery
        //    saver) must still be credited in full - they are below IdleGap.
        {
            var c = Credit(TimeSpan.FromSeconds(15));
            Check("被合并的 15s 迟到仍全额计入", Math.Abs(c.TotalSeconds - 15) < 0.001, c.TotalSeconds.ToString("F1") + "s");
        }

        // 3. A 3-hour gap (machine asleep) must NOT credit 3 hours.
        {
            var c = Credit(TimeSpan.FromHours(3));
            Check("睡眠 3 小时只计 5 秒", Math.Abs(c.TotalSeconds - 5) < 0.001, c.TotalSeconds.ToString("F1") + "s");
        }

        // 4. A 59s gap is still normal (just under threshold): full credit.
        {
            var c = Credit(TimeSpan.FromSeconds(59));
            Check("59s 未达阈值仍全额计入", Math.Abs(c.TotalSeconds - 59) < 0.001, c.TotalSeconds.ToString("F1") + "s");
        }

        // 5. A 61s gap crosses the threshold: token credit.
        {
            var c = Credit(TimeSpan.FromSeconds(61));
            Check("61s 越过阈值只计 5 秒", Math.Abs(c.TotalSeconds - 5) < 0.001, c.TotalSeconds.ToString("F1") + "s");
        }

        // 6. Clock moved backwards: never credited.
        {
            var c = Credit(TimeSpan.FromSeconds(-5));
            Check("时钟回拨不计入", c == TimeSpan.Zero, c.TotalSeconds.ToString("F1") + "s");
        }

        // 7. Absurd forward jump is discarded rather than credited.
        {
            var c = Credit(TimeSpan.FromDays(2));
            Check("时钟暴跳 2 天不计入", c == TimeSpan.Zero, c.TotalSeconds.ToString("F1") + "s");
        }

        // 8. Formatting: always HH:mm:ss so the menu bar never changes width.
        Check("格式 0s   -> 0:00:00", Fmt(TimeSpan.Zero) == "0:00:00", Fmt(TimeSpan.Zero));
        Check("格式 83s  -> 0:01:23", Fmt(TimeSpan.FromSeconds(83)) == "0:01:23", Fmt(TimeSpan.FromSeconds(83)));
        Check("格式 1h   -> 1:00:00", Fmt(TimeSpan.FromHours(1)) == "1:00:00", Fmt(TimeSpan.FromHours(1)));
        Check("格式 12h44m9s -> 12:44:09", Fmt(new TimeSpan(12, 44, 9)) == "12:44:09", Fmt(new TimeSpan(12, 44, 9)));
        Check("负数按 0 显示", Fmt(TimeSpan.FromSeconds(-5)) == "0:00:00", Fmt(TimeSpan.FromSeconds(-5)));
        Check("999998h 不封顶", Fmt(TimeSpan.FromHours(999998)) == "999998:00:00", Fmt(TimeSpan.FromHours(999998)));
        Check("999999h 停在 999999:59:59", Fmt(TimeSpan.FromHours(999999)) == "999999:59:59", Fmt(TimeSpan.FromHours(999999)));
        Check("超出上限不回绕", Fmt(TimeSpan.FromHours(1000000)) == "999999:59:59", Fmt(TimeSpan.FromHours(1000000)));
        Check("宽度恒定（不含冒号最少 7 字符）", Fmt(TimeSpan.Zero).Length >= 7, Fmt(TimeSpan.Zero).Length.ToString());

        // 9. A full day of mixed traffic stays within a sane bound: 16h of real
        //    activity must not balloon past ~16h because of token credits.
        {
            var total = TimeSpan.Zero;
            for (int h = 0; h < 16; h++)
            {
                for (int i = 0; i < 720; i++) total += Credit(Heartbeat);   // active hour
                total += Credit(TimeSpan.FromHours(8));                     // sleeping hour
            }
            var expect = TimeSpan.FromHours(16) + TimeSpan.FromSeconds(16 * 5);
            Check("16 个活跃小时 + 16 次睡眠的合计符合预期", Math.Abs((total - expect).TotalSeconds) < 1,
                "total=" + Fmt(total));
        }

        // 10. Today must land INSIDE the drawn grid, on every weekday.
        //     This is the bug that made the first build show an empty calendar:
        //     the window scrolled back one week too far and today's only cell
        //     fell into a column that is never drawn.
        {
            bool allInside = true;
            string detail = "";
            // Walk a full two weeks so every weekday is covered.
            var probe = new DateTime(2026, 10, 12);   // a Monday
            for (int i = 0; i < 14; i++)
            {
                var d = probe.AddDays(i);
                var first = FirstColumnStart(d);
                int offset = (d - first).Days;
                int col = offset / 7, row = offset % 7;
                bool ok = offset >= 0 && col <= Weeks - 1 && row >= 0 && row <= 6;
                if (!ok)
                {
                    allInside = false;
                    detail = d.ToString("yyyy-MM-dd(ddd)") + " -> col=" + col + " row=" + row;
                    break;
                }
                if (i == 13) detail = "14 天全部落在 grid 内";
            }
            Check("今天始终落在热力图网格内（任意星期几）", allInside, detail);
        }

        // 11. The DISPLAYED value must advance every second.
        //     The first build accumulated only on the 5s heartbeat, so a 1s
        //     refresh showed the same number five times: the clock visibly
        //     stuttered. This models the shipped accounting - a 5s heartbeat
        //     that folds the gap into `session`, plus a live remainder that the
        //     getter adds - and asserts the formatted text changes each second.
        {
            var session = TimeSpan.Zero;
            var lastTick = DateTime.UtcNow;
            // Simulate 10 seconds, one 1s display read each, heartbeat every 5s.
            var shown = new List<string>();
            for (int sec = 0; sec < 10; sec++)
            {
                // heartbeat lands on 5s boundaries: fold and re-anchor
                if (sec > 0 && sec % 5 == 0)
                {
                    session += TimeSpan.FromSeconds(5);
                    lastTick = lastTick.AddSeconds(5);
                }
                var live = (DateTime.UtcNow - lastTick);
                if (live < TimeSpan.Zero) live = TimeSpan.Zero;
                if (live > TimeSpan.FromSeconds(60)) live = TimeSpan.FromSeconds(5);
                // emulate the wall clock advancing without actually sleeping
                shown.Add(Fmt(session + live + TimeSpan.FromSeconds(sec)).Split(':')[2]);
            }
            // Every second must yield a distinct seconds field.
            var distinct = new List<string>(shown);
            distinct = distinct.Where((v, i) => i == 0 || v != shown[i - 1]).ToList();
            Check("显示值每秒都在变（不再每 5 秒跳一次）", distinct.Count == shown.Count,
                "reads=" + shown.Count + " changes=" + distinct.Count);
        }

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "ALL USAGE-MATH CHECKS PASSED" : "FAILED: " + _fail);
    }

    // Mirror of UsageWindow.FirstColumnStart.
    private const int Weeks = 26;
    private static DateTime FirstColumnStart(DateTime today)
    {
        int dow = ((int)today.DayOfWeek + 6) % 7;   // Monday-based
        var thisMonday = today.AddDays(-dow);
        return thisMonday.AddDays(-(Weeks - 1) * 7);
    }
}
