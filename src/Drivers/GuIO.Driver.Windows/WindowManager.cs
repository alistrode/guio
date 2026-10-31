using System.Text;
using GuIO.Contracts;

namespace GuIO.Driver.Windows;

/// <summary>窗口发现与管理。提供前台夺取（含防菜单吃键的工程经验）。</summary>
internal static class WindowManager
{
    public sealed record WinInfo(IntPtr Hwnd, uint Pid, string ClassName, string Title, PixelRect Rect, bool Visible);

    public static IReadOnlyList<WinInfo> Enumerate()
    {
        var list = new List<WinInfo>();
        Win32.EnumWindows((h, l) =>
        {
            Win32.GetWindowRect(h, out var r);
            uint pid = 0;
            Win32.GetWindowThreadProcessId(h, out pid);
            var t = new StringBuilder(512); Win32.GetWindowTextW(h, t, t.Capacity);
            var c = new StringBuilder(256); Win32.GetClassNameW(h, c, c.Capacity);
            list.Add(new WinInfo(h, pid, c.ToString(), t.ToString(),
                new PixelRect(r.Left, r.Top, r.Right, r.Bottom), Win32.IsWindowVisible(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>按 hwnd / 标题 / 进程名 / 类名查找窗口。优先级：精确 hwnd > 标题精确 > 标题包含 > 进程名。</summary>
    public static WinInfo? Find(string query)
    {
        if (long.TryParse(query, out long hwndVal))
            return Enumerate().FirstOrDefault(w => w.Hwnd.ToInt64() == hwndVal);

        if (query.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(query[2..], System.Globalization.NumberStyles.HexNumber, null, out long hx))
            return Enumerate().FirstOrDefault(w => w.Hwnd.ToInt64() == hx);

        var all = Enumerate().Where(w => w.Visible && w.Rect.Width > 0 && w.Rect.Height > 0).ToList();

        // 进程名匹配：可能命中多个窗口（主窗、托盘、输入法候选、隐藏工具窗），
        // 取「面积最大的可见窗口」——那才是用户实际看到的主窗口。
        var procQuery = query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? query[..^4] : query;
        var procHits = new List<WinInfo>();
        foreach (var w in all)
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessById((int)w.Pid);
                if (string.Equals(p.ProcessName, procQuery, StringComparison.OrdinalIgnoreCase)) procHits.Add(w);
            }
            catch { }
        }
        if (procHits.Count > 0)
        {
            // 只认「有标题且面积够大」的窗口，避免命中候选框/工具窗
            var main = procHits
                .Where(w => !string.IsNullOrWhiteSpace(w.Title) && w.Rect.Width >= 200 && w.Rect.Height >= 120)
                .OrderByDescending(w => (long)w.Rect.Width * w.Rect.Height)
                .FirstOrDefault();
            if (main is not null && main.Hwnd != IntPtr.Zero) { RestoreIfMinimized(main.Hwnd); return main; }
            var pick = procHits.OrderByDescending(w => (long)w.Rect.Width * w.Rect.Height).First();
            RestoreIfMinimized(pick.Hwnd);
            return pick;
        }

        var exact = all.FirstOrDefault(w => string.Equals(w.Title, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null && exact.Hwnd != IntPtr.Zero) { RestoreIfMinimized(exact.Hwnd); return exact; }

        var byTitle = all
            .Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => (long)w.Rect.Width * w.Rect.Height)
            .FirstOrDefault();
        if (byTitle is not null) RestoreIfMinimized(byTitle.Hwnd);
        return byTitle;
    }

    /// <summary>
    /// 最小化窗口的 GetWindowRect 会返回 (-25600,-25600) 之类的伪坐标，
    /// 必须先恢复，否则后续截图与坐标换算全部错位。
    /// </summary>
    /// <summary>
    /// 判断一个窗口矩形是否是「最小化伪矩形」。Windows 给最小化窗口返回
    /// 类似 (-25600,-25600) 的屏幕外坐标且尺寸极小。产品代码据此拒绝使用该矩形。
    /// </summary>
    public static bool LooksMinimized(PixelRect r)
        => r.Left <= -30000 || r.Top <= -30000
        || (r.Width < 200 && r.Height < 120 && r.Left < -1000);

    public static void RestoreIfMinimized(IntPtr hwnd)
    {
        try
        {
            if (Win32.IsIconic(hwnd))
            {
                Win32.ShowWindow(hwnd, 9); // SW_RESTORE
                Thread.Sleep(280);
            }
        }
        catch { }
    }

    /// <summary>
    /// 确保窗口在前台。承接原 winio.ps1 的工程经验：
    /// 用 AttachThreadInput 抢焦；绝不使用 ALT 技巧（会激活菜单栏，后续字符键被当加速键）。
    /// </summary>
    public static bool EnsureForeground(IntPtr hwnd)
    {
        if (Win32.GetForegroundWindow() == hwnd) return true;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, 9); // SW_RESTORE
            Win32.BringWindowToTop(hwnd);

            var fg = Win32.GetForegroundWindow();
            uint t1 = Win32.GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint t2 = Win32.GetCurrentThreadId();
            if (t1 != t2) Win32.AttachThreadInput(t2, t1, true);
            Win32.SetForegroundWindow(hwnd);
            if (t1 != t2) Win32.AttachThreadInput(t2, t1, false);
            Win32.SwitchToThisWindow(hwnd, true);

            Win32.GetWindowRect(hwnd, out var r);
            // 兜底：点标题条取焦（不使用键盘技巧）
            if (Win32.GetForegroundWindow() != hwnd)
            {
                Win32.MoveAbsolute((r.Left + r.Right) / 2, r.Top + 16);
                Win32.SendMouse(Win32.MOUSEEVENTF_LEFTDOWN);
                Win32.SendMouse(Win32.MOUSEEVENTF_LEFTUP);
            }
            Thread.Sleep(200);
            if (Win32.GetForegroundWindow() == hwnd) return true;
        }
        return Win32.GetForegroundWindow() == hwnd;
    }
}
