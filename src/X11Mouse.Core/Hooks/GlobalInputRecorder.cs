using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;

namespace X11Mouse.Core.Hooks;

/// <summary>
/// Clean-room low-level Win32 keyboard and mouse hook (`WH_KEYBOARD_LL` / `WH_MOUSE_LL`)
/// replacing `Hook.dll` for macro recording. Must be started and stopped on a thread pumping Windows messages (e.g. WPF UI thread).
/// </summary>
public sealed class GlobalInputRecorder : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    private const uint LLKHF_EXTENDED = 0x01;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly LowLevelProc _kbdProc;
    private readonly LowLevelProc _mouseProc;
    private IntPtr _kbdHook;
    private IntPtr _mouseHook;
    private long _lastEventTickMs;
    private bool _hasLastEvent;

    public bool IsRecording { get; private set; }
    public MacroDelayMode DelayMode { get; set; } = MacroDelayMode.RecordDelay;
    public ushort DefaultDelayMs { get; set; } = 50;
    public Func<int, int, bool>? ShouldIgnoreMouseClick { get; set; }

    public event Action<MacroAction>? ActionRecorded;

    public GlobalInputRecorder()
    {
        _kbdProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Start()
    {
        if (IsRecording)
            return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        IntPtr hMod = GetModuleHandle(module?.ModuleName);

        _lastEventTickMs = Environment.TickCount64;
        _hasLastEvent = false;
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, hMod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        IsRecording = true;
    }

    public void Stop()
    {
        if (!IsRecording)
            return;
        IsRecording = false;

        if (_kbdHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_kbdHook);
            _kbdHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private ushort ComputeDelayMs()
    {
        long now = Environment.TickCount64;
        if (!_hasLastEvent)
        {
            _hasLastEvent = true;
            _lastEventTickMs = now;
            return DelayMode == MacroDelayMode.DefaultDelay ? Math.Max((ushort)1, DefaultDelayMs) : (ushort)10;
        }

        long delta = Math.Clamp(now - _lastEventTickMs, 1, ushort.MaxValue);
        _lastEventTickMs = now;

        return DelayMode switch
        {
            MacroDelayMode.RecordDelay => (ushort)delta,
            MacroDelayMode.DefaultDelay => Math.Max((ushort)1, DefaultDelayMs),
            MacroDelayMode.NoDelay => (ushort)10,
            _ => (ushort)10,
        };
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            int msg = wParam.ToInt32();
            bool? isDown = msg switch
            {
                WM_KEYDOWN or WM_SYSKEYDOWN => true,
                WM_KEYUP or WM_SYSKEYUP => false,
                _ => null,
            };

            if (isDown.HasValue)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool extended = (info.flags & LLKHF_EXTENDED) != 0;
                byte fw = KeyCodeTable.VkToFwCode((int)info.vkCode, extended);
                if (fw != 0)
                {
                    ActionRecorded?.Invoke(new MacroAction
                    {
                        FwCode = fw,
                        State = isDown.Value ? MacroActionState.Down : MacroActionState.Up,
                        DelayMs = ComputeDelayMs(),
                    });
                }
            }
        }
        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            int msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (ShouldIgnoreMouseClick?.Invoke(info.pt.x, info.pt.y) == true)
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            (byte fw, MacroActionState? state) = msg switch
            {
                WM_LBUTTONDOWN => (KeyCodeTable.MouseLeftFw, (MacroActionState?)MacroActionState.Down),
                WM_LBUTTONUP => (KeyCodeTable.MouseLeftFw, MacroActionState.Up),
                WM_RBUTTONDOWN => (KeyCodeTable.MouseRightFw, MacroActionState.Down),
                WM_RBUTTONUP => (KeyCodeTable.MouseRightFw, MacroActionState.Up),
                WM_MBUTTONDOWN => (KeyCodeTable.MouseMiddleFw, MacroActionState.Down),
                WM_MBUTTONUP => (KeyCodeTable.MouseMiddleFw, MacroActionState.Up),
                WM_XBUTTONDOWN => (((info.mouseData >> 16) == 2 ? KeyCodeTable.MouseX2ForwardFw : KeyCodeTable.MouseX1BackwardFw), MacroActionState.Down),
                WM_XBUTTONUP => (((info.mouseData >> 16) == 2 ? KeyCodeTable.MouseX2ForwardFw : KeyCodeTable.MouseX1BackwardFw), MacroActionState.Up),
                _ => ((byte)0, null),
            };

            if (fw != 0 && state.HasValue)
            {
                ActionRecorded?.Invoke(new MacroAction
                {
                    FwCode = fw,
                    State = state.Value,
                    DelayMs = ComputeDelayMs(),
                });
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }
}
