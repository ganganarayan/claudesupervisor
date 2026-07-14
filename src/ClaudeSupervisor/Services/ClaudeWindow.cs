using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Threading;
using ClaudeSupervisor.Native;
using static ClaudeSupervisor.Native.NativeMethods;

namespace ClaudeSupervisor.Services;

/// <summary>
/// Locates the Claude desktop-app window and provides capture / focus / typing helpers.
/// </summary>
public sealed class ClaudeWindow
{
    public IntPtr Handle { get; }
    public string Title { get; }
    public string ProcessName { get; }
    public int Pid { get; }

    private ClaudeWindow(IntPtr handle, string title, string processName, int pid)
    {
        Handle = handle;
        Title = title;
        ProcessName = processName;
        Pid = pid;
    }

    /// <summary>
    /// Finds the Claude desktop window. Prefers a visible top-level window owned by a
    /// process named "Claude" (the desktop app); falls back to any visible window whose
    /// title contains "claude" but not "supervisor" (so we never match ourselves).
    /// </summary>
    public static ClaudeWindow? Find()
    {
        int self = Environment.ProcessId;
        ClaudeWindow? exact = null;
        ClaudeWindow? fallback = null;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            int len = GetWindowTextLength(hWnd);
            if (len == 0)
                return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == self)
                return true;

            string procName;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                procName = p.ProcessName;
            }
            catch
            {
                return true;
            }

            if (procName.Equals("Claude", StringComparison.OrdinalIgnoreCase))
            {
                exact = new ClaudeWindow(hWnd, title, procName, (int)pid);
                return false; // stop enumerating — best possible match
            }

            if (fallback is null &&
                title.Contains("claude", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("supervisor", StringComparison.OrdinalIgnoreCase))
            {
                fallback = new ClaudeWindow(hWnd, title, procName, (int)pid);
            }

            return true;
        }, IntPtr.Zero);

        return exact ?? fallback;
    }

    /// <summary>
    /// Captures the Claude window's own pixels via PrintWindow — even when it is
    /// covered by other windows (like this app). Never grabs the screen, so the
    /// Supervisor window can never leak into the capture. Throws if the result is blank.
    /// </summary>
    public Bitmap Capture()
    {
        if (!GetWindowRect(Handle, out RECT rect) || rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidOperationException("Could not read the Claude window bounds.");

        var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                PrintWindow(Handle, hdc, PW_RENDERFULLCONTENT);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        if (IsMostlyBlack(bmp))
        {
            bmp.Dispose();
            throw new InvalidOperationException(
                "Window capture came back blank. Make sure the Claude window is open and " +
                "not minimized, then try again.");
        }

        return bmp;
    }

    /// <summary>Brings the window to the foreground, restoring it if minimized.</summary>
    public void ForceForeground()
    {
        if (IsIconic(Handle))
            ShowWindow(Handle, SW_RESTORE);

        IntPtr foreground = GetForegroundWindow();
        uint foreThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(Handle, out _);

        AttachThreadInput(thisThread, foreThread, true);
        AttachThreadInput(thisThread, targetThread, true);
        try
        {
            BringWindowToTop(Handle);
            ShowWindow(Handle, SW_SHOW);
            SetForegroundWindow(Handle);
        }
        finally
        {
            AttachThreadInput(thisThread, targetThread, false);
            AttachThreadInput(thisThread, foreThread, false);
        }
    }

    /// <summary>
    /// Focuses the Claude window and submits.
    /// <para>
    /// If <paramref name="enterOnly"/> is true (or <paramref name="text"/> is empty), it just
    /// presses Enter — use this when the prompt is already typed in Claude's composer.
    /// Otherwise it moves the caret to the end of the composer, appends <paramref name="text"/>
    /// (multi-line text uses Shift+Enter for line breaks so it isn't submitted early), then
    /// presses Enter to send.
    /// </para>
    /// </summary>
    public void Resume(string? text, bool enterOnly)
    {
        ForceForeground();
        Thread.Sleep(400); // let focus settle on the composer

        bool typeText = !enterOnly && !string.IsNullOrEmpty(text);
        if (typeText)
        {
            SendChord(VK_CONTROL, VK_END); // go to the end of any existing text, then append
            Thread.Sleep(50);
            TypeText(text!);
            Thread.Sleep(80);
        }

        SendVirtualKey(VK_RETURN); // submit
    }

    private static void TypeText(string text)
    {
        foreach (char c in text)
        {
            if (c == '\r')
                continue;
            if (c == '\n')
                SendChord(VK_SHIFT, VK_RETURN); // newline without submitting
            else
                SendUnicodeChar(c);
        }
    }

    private static void SendUnicodeChar(char c)
    {
        var inputs = new[]
        {
            KeyInput(0, c, KEYEVENTF_UNICODE),
            KeyInput(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP),
        };
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static void SendVirtualKey(ushort vk)
    {
        var inputs = new[]
        {
            KeyInput(vk, 0, 0),
            KeyInput(vk, 0, KEYEVENTF_KEYUP),
        };
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    /// <summary>Sends a modifier+key chord, e.g. Ctrl+End or Shift+Enter.</summary>
    private static void SendChord(ushort modifier, ushort key)
    {
        var inputs = new[]
        {
            KeyInput(modifier, 0, 0),
            KeyInput(key, 0, 0),
            KeyInput(key, 0, KEYEVENTF_KEYUP),
            KeyInput(modifier, 0, KEYEVENTF_KEYUP),
        };
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    /// <summary>Samples a grid of pixels to decide whether a capture came back essentially black.</summary>
    private static bool IsMostlyBlack(Bitmap bmp)
    {
        const int steps = 12;
        int dark = 0, total = 0;
        int stepX = Math.Max(1, bmp.Width / steps);
        int stepY = Math.Max(1, bmp.Height / steps);

        for (int x = 0; x < bmp.Width; x += stepX)
        {
            for (int y = 0; y < bmp.Height; y += stepY)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.R < 12 && c.G < 12 && c.B < 12)
                    dark++;
                total++;
            }
        }

        return total > 0 && dark >= total * 0.98;
    }
}
