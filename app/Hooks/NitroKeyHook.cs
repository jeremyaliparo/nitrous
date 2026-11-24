using System;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Nitrous.Diagnostics;

namespace Nitrous.Hooks;

/// <summary>
/// The laptop's Nitro (NitroSense) key, on both transports Acer uses for it:
///
/// <list type="bullet">
/// <item>as a keystroke - scan code <c>E0 75</c> with no virtual key - read with a low-level
/// keyboard hook, which is what earlier models send;</item>
/// <item>as a report on the Acer gaming HID collection (usage page <c>0x88</c>, usage 1) whose
/// bytes 1-2 are <c>81 FF</c>, read with raw input. Models that send the key this way never raise
/// a keystroke, so the hook alone never sees them.</item>
/// </list>
///
/// Both arrive while another app has focus: the hook is global, and the raw-input registration uses
/// <c>RIDEV_INPUTSINK</c>.
/// </summary>
public partial class NitroKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int WM_INPUT = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeHid = 2;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const ushort AcerGamingPage = 0x88;
    private const ushort AcerGamingUsage = 0x0001;

    /// <summary>The window handle Acer's HID reports are delivered to (HWND_MESSAGE).</summary>
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly IntPtr _hookID;
    private readonly LowLevelKeyboardProc _proc;
    private readonly HidReportWindow? _window;

    public event EventHandler? NitroKeyPressed;

    private static readonly FrozenSet<uint> NitroScanCodes = new[] { 117u, 175u, 236u }.ToFrozenSet();

    public NitroKeyHook()
    {
        _proc = HookCallback;
        _hookID = SetHook(_proc);
        if (_hookID == IntPtr.Zero)
            Logger.Warn($"Nitro key: SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()}).");

        // Raw input needs a window to deliver to; a message-only one costs nothing and never shows.
        // Created on the tray's UI thread, which is where the messages are then pumped.
        try
        {
            _window = new HidReportWindow(this);
            var devices = new[]
            {
                new RawInputDevice
                {
                    UsagePage = AcerGamingPage,
                    Usage = AcerGamingUsage,
                    Flags = RidevInputSink,
                    Target = _window.Handle
                }
            };

            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                Logger.Warn($"Nitro key: the Acer HID collection could not be registered (error {Marshal.GetLastWin32Error()}).");
        }
        catch (Exception ex)
        {
            Logger.Warn("Nitro key: raw-input registration failed.", ex);
        }
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
                Raise();
        }

        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    /// <summary>
    /// A raw-input buffer is the header followed by RAWHID: its sizes, then the report. Acer's
    /// report puts <c>81 FF</c> in bytes 1-2 (measured on the AN515-57).
    /// </summary>
    private void OnRawInput(IntPtr handle)
    {
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(handle, RidInput, null, ref size, headerSize) != 0 || size == 0 || size > 4096)
            return;

        var buffer = new byte[size];
        if (GetRawInputData(handle, RidInput, buffer, ref size, headerSize) != size) return;
        if (BitConverter.ToUInt32(buffer, 0) != RimTypeHid) return;

        // RAWHID is two DWORDs, then bRawData.
        const int DataOffset = 8;
        var report = (int)headerSize + DataOffset;
        if (size < report + 3) return;

        if (buffer[report + 1] == 0x81 && buffer[report + 2] == 0xFF)
        {
            Logger.Info("Nitro key: pressed (Acer HID report).");
            Raise();
        }
    }

    private void Raise()
    {
        try { NitroKeyPressed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Logger.Warn("Nitro key: handler failed.", ex); }
    }

    public void Dispose()
    {
        if (_window != null)
        {
            var devices = new[]
            {
                new RawInputDevice { UsagePage = AcerGamingPage, Usage = AcerGamingUsage, Flags = RidevRemove }
            };
            RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>());
            _window.DestroyHandle();
        }

        if (_hookID != IntPtr.Zero) UnhookWindowsHookEx(_hookID);
    }

    /// <summary>A message-only window whose only job is to receive WM_INPUT.</summary>
    private sealed class HidReportWindow : NativeWindow
    {
        private readonly NitroKeyHook _owner;

        public HidReportWindow(NitroKeyHook owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Parent = MessageOnlyParent });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) _owner.OnRawInput(m.LParam);
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput, uint command, byte[]? data, ref uint size, uint headerSize);
}
