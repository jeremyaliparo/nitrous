using System;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nitrous.Diagnostics;

namespace Nitrous.Hooks;

public partial class NitroKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private readonly IntPtr _hookID;
    private readonly LowLevelKeyboardProc _proc;

    public event EventHandler? NitroKeyPressed;

    private static readonly FrozenSet<uint> NitroScanCodes = new[] { 117u, 175u, 236u }.ToFrozenSet();

    public NitroKeyHook()
    {
        _proc = HookCallback;
        _hookID = SetHook(_proc);
        if (_hookID == IntPtr.Zero)
            Logger.Warn($"Nitro key: SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()}).");
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            if (curModule?.ModuleName == null)
            {
                Logger.Warn("Nitro key: could not resolve the current module name.");
                return IntPtr.Zero;
            }

            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
        catch (Exception ex)
        {
            Logger.Warn("Nitro key: hook installation failed.", ex);
            return IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            if (NitroScanCodes.Contains(kbd.scanCode))
            {
                try { NitroKeyPressed?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { Logger.Warn("Nitro key: handler failed.", ex); }
            }
        }

        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookID != IntPtr.Zero) UnhookWindowsHookEx(_hookID);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
