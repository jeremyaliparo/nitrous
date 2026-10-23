using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

public class GlobalHotkeyManager : NativeWindow, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    public event Action<int>? HotkeyPressed;

    public GlobalHotkeyManager()
    {
        CreateHandle(new CreateParams()); // Creates a hidden message pump
    }

    public bool Register(int id, uint modifiers, uint key)
    {
        UnregisterHotKey(Handle, id); // Clear existing
        var ok = RegisterHotKey(Handle, id, modifiers, key);
        if (!ok)
            Logger.Warn($"Hotkey: RegisterHotKey failed for id {id} (error {Marshal.GetLastWin32Error()}).");
        return ok;
    }

    public void UnregisterAll(params int[] ids)
    {
        foreach (var id in ids) UnregisterHotKey(Handle, id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            try { HotkeyPressed?.Invoke(m.WParam.ToInt32()); }
            catch (Exception ex) { Logger.Warn("Hotkey: handler failed.", ex); }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        DestroyHandle();
    }
}
