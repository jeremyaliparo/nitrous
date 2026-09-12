using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using Microsoft.Win32;
using Nitrous.Enums;
using Nitrous.Hooks;
using Nitrous.Managers;

namespace Nitrous.Ui;

public class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly NitroKeyHook _nitroHook;
    private readonly NvidiaGpuManager _gpuManager = new();
    private bool? _wasOnAcPower = null;
    private int _powerEventId = 0;

    public TrayApplication()
    {
        Icon appIcon = SystemIcons.Shield;
        try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield; } catch { }

        trayIcon = new NotifyIcon { Icon = appIcon, Visible = true, Text = "Nitrous" };
        trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(); };

        BuildContextMenu();

        SystemEvents.PowerModeChanged += OnPowerStateChanged;

        _ = Task.Run(() => UpdateManager.CheckForUpdatesAsync(true, () => Exit(null, EventArgs.Empty)));

        _nitroHook = new NitroKeyHook();
        _nitroHook.NitroKeyPressed += (s, e) => ShowDashboard();

        _ = Task.Run(async () =>
        {
            await Task.Delay(8000);
            ApplyPowerSettings(true);

            var bootProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
            await _gpuManager.ApplyOnBootAsync(bootProfile);
        });
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false };
        menu.Items.Add("Open Nitrous", null, (s, e) => ShowDashboard());
        menu.Items.Add("Check for Updates...", null, async (s, e) => await UpdateManager.CheckForUpdatesAsync(false, () => Exit(null, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, Exit);
        trayIcon.ContextMenuStrip = menu;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void ShowDashboard()
    {
        string processName = Process.GetCurrentProcess().ProcessName;
        int currentId = Process.GetCurrentProcess().Id;

        var processes = Process.GetProcessesByName(processName);

        foreach (var p in processes)
        {
            if (p.Id != currentId)
            {
                // Found the existing UI process. Restore and bring to front.
                IntPtr hWnd = p.MainWindowHandle;
                if (hWnd != IntPtr.Zero)
                {
                    const int SW_RESTORE = 9;
                    ShowWindow(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                }
                return; // Prevent spawning a new instance
            }
        }

        // If no UI process is running, start a new one
        Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--ui") { UseShellExecute = true });
    }

    private async void OnPowerStateChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            int eventId = ++_powerEventId;
            await Task.Delay(3500);
            if (eventId != _powerEventId) return;

            ApplyPowerSettings(false);
        }
    }

    private void ApplyPowerSettings(bool isStartup = false)
    {
        bool isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

        if (!isStartup && _wasOnAcPower.HasValue && _wasOnAcPower.Value == isOnline) return;
        _wasOnAcPower = isOnline;

        if (SettingsManager.Get("AutoSwitch", 0) == 1)
        {
            string keyMode = isOnline ? "LastAcPowerMode" : "LastDcPowerMode";
            var activeMode = (PowerProfile)SettingsManager.Get(keyMode, (int)(isOnline ? PowerProfile.Performance : PowerProfile.Quiet));
            _ = AcerWmiManager.SetPowerModeAsync(activeMode);
            SettingsManager.Save("LastPowerMode", (int)activeMode);

            string keyFan = isOnline ? "LastAcFanMode" : "LastDcFanMode";
            var activeFan = Enum.TryParse(SettingsManager.Get(keyFan, "Auto"), out FanProfile f) ? f : FanProfile.Auto;

            if (activeFan == FanProfile.Medium)
                _ = AcerWmiManager.SetCustomFansAsync(SettingsManager.Get("CustomFanSpeedCpu", 50), SettingsManager.Get("CustomFanSpeedGpu", 50));
            else
                _ = AcerWmiManager.SetFansAsync(activeFan);

            SettingsManager.Save("LastFanMode", activeFan.ToString());
        }

        var refreshMode = (RefreshProfile)SettingsManager.Get("RefreshMode", (int)RefreshProfile.Auto);
        DisplayManager.ApplyRefreshProfile(refreshMode, isOnline);

        var currentProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
        _ = _gpuManager.ApplyPowerProfileOcAsync(currentProfile);
    }

    private void Exit(object? sender, EventArgs e)
    {
        _nitroHook.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerStateChanged;
        trayIcon.Visible = false;
        trayIcon.Dispose();
        _gpuManager.Dispose();

        try
        {
            string pName = Process.GetCurrentProcess().ProcessName;
            int currentId = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName(pName))
            {
                if (p.Id != currentId) p.Kill();
            }
        }
        catch { }

        Application.Exit();
    }
}
