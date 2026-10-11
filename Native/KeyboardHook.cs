using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using SpawnTimer.Core;

namespace SpawnTimer.Native;

/// <summary>
/// Global hotkeys via a low-level keyboard hook (WH_KEYBOARD_LL) rather than RegisterHotKey,
/// so a hotkey can optionally still reach the game (pass-through). Must be created on the UI thread:
/// the callback runs there, so <see cref="KeyPressed"/> must return quickly and defer real work.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep alive: the OS holds only a raw pointer
    private readonly HashSet<int> _down = new();
    private readonly HashSet<int> _swallowed = new();
    private IntPtr _hook;

    public KeyboardHook()
    {
        _proc = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Return true when the key combo was a hotkey.</summary>
    public Func<Hotkey, bool>? KeyPressed { get; set; }

    /// <summary>Ignore all keys (e.g. while a settings field is capturing a new hotkey).</summary>
    public bool Suspended { get; set; }

    /// <summary>Let matched hotkeys continue on to the game.</summary>
    public bool PassThrough { get; set; }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            var vk = Marshal.ReadInt32(lParam);

            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var isRepeat = !_down.Add(vk);
                if (isRepeat)
                {
                    if (_swallowed.Contains(vk)) return (IntPtr)1;
                }
                else if (!Suspended && !IsModifier(vk) && KeyPressed is { } handler)
                {
                    var hotkey = new Hotkey(CurrentModifiers(), KeyInterop.KeyFromVirtualKey(vk));
                    if (handler(hotkey) && !PassThrough)
                    {
                        _swallowed.Add(vk);
                        return (IntPtr)1;
                    }
                }
            }
            else if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                _down.Remove(vk);
                if (_swallowed.Remove(vk)) return (IntPtr)1;
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsModifier(int vk) =>
        vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or (>= 0xA0 and <= 0xA5);

    private static ModifierKeys CurrentModifiers()
    {
        var mods = ModifierKeys.None;
        if (NativeMethods.IsDown(VK_CONTROL)) mods |= ModifierKeys.Control;
        if (NativeMethods.IsDown(VK_MENU)) mods |= ModifierKeys.Alt;
        if (NativeMethods.IsDown(VK_SHIFT)) mods |= ModifierKeys.Shift;
        if (NativeMethods.IsDown(VK_LWIN) || NativeMethods.IsDown(VK_RWIN)) mods |= ModifierKeys.Windows;
        return mods;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
