using System.Windows.Input;
using System.Windows.Interop;
using CustomRecorder.Core.Interop;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Services;

/// <summary>System-wide hotkeys (work while another program is in front).</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5100;

    public List<string> Failed { get; } = new();

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    public void Register(string label, ModifierKeys modifiers, Key key, Action action)
    {
        uint mod = NativeMethods.MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mod |= NativeMethods.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mod |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mod |= NativeMethods.MOD_ALT;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        int id = _nextId++;
        if (NativeMethods.RegisterHotKey(_hwnd, id, mod, vk))
        {
            _actions[id] = action;
        }
        else
        {
            // Another program already owns this key.
            Failed.Add(label);
            Log.Warn($"단축키 등록 실패 (다른 프로그램이 사용 중): {label}");
        }
    }

    /// <summary>Registers a hotkey given as text ("Shift+F2"). Empty text = no hotkey.</summary>
    public void Register(string label, string? text, Action action)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (HotkeyText.TryParse(text, out var mods, out var key)) Register(text, mods, key, action);
        else { Failed.Add(text); Log.Warn($"단축키 형식 오류 ({label}): {text}"); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _source.RemoveHook(WndProc);
    }
}
