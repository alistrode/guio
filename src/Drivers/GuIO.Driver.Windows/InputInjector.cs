using GuIO.Contracts;

namespace GuIO.Driver.Windows;

/// <summary>
/// 桌面输入注入。统一走 SendInput（原子、兼容 UAC/DPI）。
/// 承接 sendinput_click.py 的方法：绝对坐标点击，比 mouse_event 更现代。
/// </summary>
internal sealed class InputInjector
{
    public void Click(int x, int y, PointerButton button, int count, double holdMs)
    {
        Win32.MoveAbsolute(x, y);
        Thread.Sleep(60);
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            Down(button);
            if (holdMs > 0) Thread.Sleep((int)holdMs); else Thread.Sleep(30);
            Up(button);
            if (i < count - 1) Thread.Sleep(40);
        }
        // 双击需要回到原位再按，系统据时间间隔判定
    }

    private static void Down(PointerButton b)
    {
        uint f = b switch
        {
            PointerButton.Right => Win32.MOUSEEVENTF_RIGHTDOWN,
            PointerButton.Middle => Win32.MOUSEEVENTF_MIDDLEDOWN,
            _ => Win32.MOUSEEVENTF_LEFTDOWN,
        };
        Win32.SendMouse(f);
    }

    private static void Up(PointerButton b)
    {
        uint f = b switch
        {
            PointerButton.Right => Win32.MOUSEEVENTF_RIGHTUP,
            PointerButton.Middle => Win32.MOUSEEVENTF_MIDDLEUP,
            _ => Win32.MOUSEEVENTF_LEFTUP,
        };
        Win32.SendMouse(f);
    }

    public void Move(int x, int y) => Win32.MoveAbsolute(x, y);

    public void Drag(int x1, int y1, int x2, int y2, int durationMs, int steps)
    {
        Win32.MoveAbsolute(x1, y1);
        Thread.Sleep(120);
        Down(PointerButton.Left);
        Thread.Sleep(80);
        steps = Math.Max(2, steps);
        int stepDelay = Math.Max(1, durationMs / steps);
        for (int i = 1; i <= steps; i++)
        {
            int x = x1 + (x2 - x1) * i / steps;
            int y = y1 + (y2 - y1) * i / steps;
            Win32.MoveAbsolute(x, y);
            Thread.Sleep(stepDelay);
        }
        Thread.Sleep(60);
        Up(PointerButton.Left);
    }

    public void Scroll(int? x, int? y, int dx, int dy, ScrollUnit unit)
    {
        if (x is not null && y is not null) { Win32.MoveAbsolute(x.Value, y.Value); Thread.Sleep(40); }
        int notches = unit switch
        {
            ScrollUnit.Wheel => dy,
            ScrollUnit.Line => dy * 1,
            ScrollUnit.Page => dy * 5,
            ScrollUnit.Pixel => dy / 100,
            _ => dy,
        };
        if (notches != 0) Win32.SendMouse(Win32.MOUSEEVENTF_WHEEL, 0, 0, notches * 120);
        if (dx != 0) Win32.SendMouse(Win32.MOUSEEVENTF_HWHEEL, 0, 0, dx * 120);
    }

    public void Key(int virtualKey, IReadOnlyList<string> modifiers, int repeat)
    {
        var modVks = modifiers.Select(KeyModifierVk).Where(v => v > 0).ToArray();
        for (int r = 0; r < Math.Max(1, repeat); r++)
        {
            foreach (var m in modVks) Win32.SendKeyVk((ushort)m, false);
            Win32.SendKeyVk((ushort)virtualKey, false);
            Thread.Sleep(20);
            Win32.SendKeyVk((ushort)virtualKey, true);
            for (int i = modVks.Length - 1; i >= 0; i--) Win32.SendKeyVk((ushort)modVks[i], true);
            Thread.Sleep(20);
        }
    }

    private static int KeyModifierVk(string m) => m.ToLowerInvariant() switch
    {
        "ctrl" or "control" => 0x11,
        "alt" => 0x12,
        "shift" => 0x10,
        "win" or "meta" => 0x5B,
        _ => 0,
    };

    /// <summary>输入文本。unicode=true 时用 KEYEVENTF_UNICODE，支持中文/emoji。</summary>
    public void Type(string text, bool unicode)
    {
        if (unicode)
        {
            foreach (char c in text)
            {
                // 换行需要回车键
                if (c == '\n') { Key(0x0D, Array.Empty<string>(), 1); continue; }
                Win32.SendUnicodeChar(c);
                Thread.Sleep(8);
            }
        }
        else
        {
            foreach (char c in text)
            {
                short vk = VkKeyScan(c);
                if (vk == -1) { Win32.SendUnicodeChar(c); continue; }
                byte key = (byte)(vk & 0xFF);
                bool shift = (vk >> 8 & 1) != 0;
                if (shift) Win32.SendKeyVk(0x10, false);
                Win32.SendKeyVk(key, false);
                Thread.Sleep(10);
                Win32.SendKeyVk(key, true);
                if (shift) Win32.SendKeyVk(0x10, true);
                Thread.Sleep(10);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern short VkKeyScan(char ch);
}
