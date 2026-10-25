using System.Runtime.InteropServices;
using System.Text;
using CustomRecorder.Core.Util;

namespace CustomRecorder.Core.Effects;

public enum KeyDisplayMode { Off, Shortcuts, All }

/// <summary>
/// Global low-level keyboard hook that turns key presses into display text ("Ctrl + Shift + S", "Enter", typed letters).
/// Only used to draw the keys into the recording; nothing is stored.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    private readonly KeyDisplayMode _mode;
    private readonly Action<string, bool> _onKey; // text, isPlainCharacter
    private readonly HookProc _proc;
    private readonly Thread _thread;
    private uint _threadId;

    public KeyboardHook(KeyDisplayMode mode, Action<string, bool> onKey)
    {
        _mode = mode;
        _onKey = onKey;
        _proc = Callback;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log.Warn($"키보드 후크 설치 실패: {Marshal.GetLastWin32Error()}");
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }) { Name = "KeyHook", IsBackground = true };
        _thread.Start();
        ready.Wait(2000);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            try { Handle(Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).VkCode); }
            catch { /* never break input */ }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Handle(uint vk)
    {
        if (vk is >= 0x10 and <= 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C) return; // modifiers alone
        bool ctrl = Down(0x11), alt = Down(0x12), shift = Down(0x10), win = Down(0x5B) || Down(0x5C);
        string name = KeyName(vk);
        bool special = name.Length > 1 || vk is >= 0x70 and <= 0x87;
        bool combo = ctrl || alt || win;

        if (_mode == KeyDisplayMode.Shortcuts && !combo && !(vk is >= 0x70 and <= 0x87 or 0x1B)) return; // only combos, F-keys, Esc

        if (!combo && !special && _mode == KeyDisplayMode.All)
        {
            _onKey(shift ? name : name.ToLowerInvariant(), true);
            return;
        }
        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl + ");
        if (alt) sb.Append("Alt + ");
        if (win) sb.Append("Win + ");
        if (shift && (combo || special)) sb.Append("Shift + ");
        sb.Append(name);
        _onKey(sb.ToString(), false);
    }

    private static string KeyName(uint vk) => vk switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc", 0x20 => "Space",
        0x21 => "PgUp", 0x22 => "PgDn", 0x23 => "End", 0x24 => "Home",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        0x2C => "PrtSc", 0x2D => "Ins", 0x2E => "Del", 0x15 => "한/영", 0x19 => "한자",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        _ => MapVirtualKey(vk, 2) is var ch and > 0 ? ((char)(ch & 0x7FFF)).ToString().ToUpperInvariant() : $"#{vk:X2}",
    };

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
