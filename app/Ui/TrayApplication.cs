using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using Microsoft.Win32;
using Nitrous.Enums;
using Nitrous.Hooks;
using Nitrous.Managers;
using Nitrous.Helpers;

namespace Nitrous.Ui;

public class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly NitroKeyHook _nitroHook;
    private readonly NvidiaGpuManager _gpuManager = new();
    private bool? _wasOnAcPower = null;
    private int _powerEventId = 0;

    private CancellationTokenSource _engineCts = new CancellationTokenSource();
    private int _lastAppliedCpuSpeed = -1;
    private int _lastAppliedGpuSpeed = -1;

    private readonly GlobalHotkeyManager _hotkeys = new();
    private readonly OsdForm _osd = new();

    private const int HK_CYCLE_POWER = 1;
    private const int HK_DASHBOARD = 99;

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

        _hotkeys.HotkeyPressed += OnCustomHotkeyPressed;
        ReloadHotkeys();

        _ = Task.Run(async () =>
        {
            await Task.Delay(8000);
            ApplyPowerSettings(true);

            var bootProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
            await _gpuManager.ApplyOnBootAsync(bootProfile);
        });

        StartBackgroundEngine();
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false };
        menu.Items.Add("Open Nitrous", null, (s, e) => ShowDashboard());
        menu.Items.Add("Check for Updates...", null, async (s, e) => await UpdateManager.CheckForUpdatesAsync(false, () => Exit(null, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart", null, Restart);
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

        var currentProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);

        if (SettingsManager.Get("AutoSwitch", 0) == 1)
        {
            string keyMode = isOnline ? "LastAcPowerMode" : "LastDcPowerMode";
            var activeMode = (PowerProfile)SettingsManager.Get(keyMode, (int)(isOnline ? PowerProfile.Performance : PowerProfile.Quiet));
            _ = AcerWmiManager.SetPowerModeAsync(activeMode);
            SettingsManager.Save("LastPowerMode", (int)activeMode);
            currentProfile = activeMode;

            string keyFan = isOnline ? "LastAcFanMode" : "LastDcFanMode";
            var activeFan = Enum.TryParse(SettingsManager.Get(keyFan, "Auto"), out FanProfile f) ? f : FanProfile.Auto;

            if (activeFan == FanProfile.Medium)
                _ = AcerWmiManager.SetCustomFansAsync(SettingsManager.Get("CustomFanSpeedCpu", 50), SettingsManager.Get("CustomFanSpeedGpu", 50));
            else
                _ = AcerWmiManager.SetFansAsync(activeFan);

            SettingsManager.Save("LastFanMode", activeFan.ToString());
        }

        // Apply CPU Power Management
        if (SettingsManager.Get("ManageCpuPower", 0) == 1)
        {
            _ = CpuPowerManager.ApplyProfileLimitsAsync(currentProfile, isOnline);
        }
        else
        {
            _ = CpuPowerManager.RestoreDefaultsAsync(isOnline);
        }

        var refreshMode = (RefreshProfile)SettingsManager.Get("RefreshMode", (int)RefreshProfile.Auto);
        DisplayManager.ApplyRefreshProfile(refreshMode, isOnline);

        _ = _gpuManager.ApplyPowerProfileOcAsync(currentProfile);
    }

    private void ReloadHotkeys()
    {
        _hotkeys.UnregisterAll(HK_CYCLE_POWER, HK_DASHBOARD);

        RegisterSavedHotkey("Hotkey_CyclePower", HK_CYCLE_POWER);
        RegisterSavedHotkey("Hotkey_Dashboard", HK_DASHBOARD);
    }

    private void RegisterSavedHotkey(string settingKey, int id)
    {
        string saved = SettingsManager.Get(settingKey, "");
        if (string.IsNullOrEmpty(saved)) return;

        try
        {
            // Example saved format: "2|81" (Modifiers=2 (Ctrl), Key=81 (Q))
            var parts = saved.Split('|');
            uint mods = uint.Parse(parts[0]);
            uint key = uint.Parse(parts[1]);
            _hotkeys.Register(id, mods, key);
        }
        catch { }
    }

    private void OnCustomHotkeyPressed(int id)
    {
        switch (id)
        {
            case HK_CYCLE_POWER:
                CyclePowerMode();
                break;
            case HK_DASHBOARD:
                ShowDashboard();
                break;
        }
    }

    private void CyclePowerMode()
    {
        bool isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        string acDcKey = isOnline ? "LastAcPowerMode" : "LastDcPowerMode";

        // Get the current mode
        var currentProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
        PowerProfile nextProfile;

        // Determine the next mode in the sequence
        switch (currentProfile)
        {
            case PowerProfile.Quiet:
                nextProfile = PowerProfile.Balanced;
                break;
            case PowerProfile.Balanced:
                nextProfile = PowerProfile.Performance;
                break;
            case PowerProfile.Performance:
                // Skip Turbo and wrap around to Quiet if it isn't supported
                nextProfile = AcerWmiManager.IsTurboModeSupported() ? PowerProfile.Turbo : PowerProfile.Quiet;
                break;
            case PowerProfile.Turbo:
            default:
                nextProfile = PowerProfile.Quiet;
                break;
        }

        // 1. Apply the new Acer power mode
        _ = AcerWmiManager.SetPowerModeAsync(nextProfile);
        SettingsManager.Save("LastPowerMode", (int)nextProfile);
        SettingsManager.Save(acDcKey, (int)nextProfile);

        // 2. Apply Windows CPU Power Limits if enabled
        if (SettingsManager.Get("ManageCpuPower", 0) == 1)
        {
            _ = CpuPowerManager.ApplyProfileLimitsAsync(nextProfile, isOnline);
        }

        // 3. Apply GPU Overclock/Underclock profile
        _ = _gpuManager.ApplyPowerProfileOcAsync(nextProfile);

        // 4. Show the correct color and icon on the OSD
        Color osdColor = nextProfile switch
        {
            PowerProfile.Quiet => Color.FromArgb(52, 199, 89),       // Green
            PowerProfile.Balanced => Color.FromArgb(10, 132, 255),   // Blue
            PowerProfile.Performance => Color.FromArgb(255, 159, 10),// Orange
            PowerProfile.Turbo => Color.FromArgb(255, 69, 58),       // Red
            _ => Color.White
        };

        _osd.ShowProfile($"{nextProfile} MODE", osdColor, nextProfile);
    }

    private void StartBackgroundEngine()
    {
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (!_engineCts.Token.IsCancellationRequested)
            {
                try
                {
                    // 1. Check if the user wants Custom Fan Mode and if the Curve is enabled
                    string currentFanMode = SettingsManager.Get("LastFanMode", "Auto");
                    bool isCurveEnabled = SettingsManager.Get("IsCurveModeEnabled", 0) == 1;

                    if (currentFanMode == "Medium" && isCurveEnabled)
                    {
                        // 2. Fetch Telemetry
                        var telemetry = AcerWmiManager.GetSystemTelemetry();

                        int effectiveGpuTemp = telemetry.GpuTemp;
                        bool deepTelemetry = SettingsManager.Get("DeepGpuTelemetry", 1) == 1;

                        // Fallback to NVIDIA SMI if EC reports 0
                        if (effectiveGpuTemp == 0 && deepTelemetry)
                        {
                            var smi = await _gpuManager.GetNvmlTelemetryAsync(_engineCts.Token);
                            if (smi != null && smi.CoreTemp > 0)
                            {
                                effectiveGpuTemp = smi.CoreTemp;
                            }
                        }

                        // 3. Load curves from Registry using the specific profile and fallback defaults
                        var activeMode = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
                        string pName = activeMode.ToString();

                        var cpuCurve = FanCurveHelper.LoadCurveFromRegistry($"CpuCurve_{pName}", FanCurveHelper.GetDefaultCpuCurve(activeMode));
                        var gpuCurve = FanCurveHelper.LoadCurveFromRegistry($"GpuCurve_{pName}", FanCurveHelper.GetDefaultGpuCurve(activeMode));

                        // 4. Interpolate
                        int targetCpuSpeed = FanCurveHelper.InterpolateSpeed(cpuCurve, telemetry.CpuTemp);
                        int targetGpuSpeed = effectiveGpuTemp == 0
                            ? targetCpuSpeed
                            : FanCurveHelper.InterpolateSpeed(gpuCurve, effectiveGpuTemp);

                        // 5. Fire WMI only if changed
                        if (targetCpuSpeed != _lastAppliedCpuSpeed || targetGpuSpeed != _lastAppliedGpuSpeed)
                        {
                            _lastAppliedCpuSpeed = targetCpuSpeed;
                            _lastAppliedGpuSpeed = targetGpuSpeed;
                            await AcerWmiManager.SetCustomFansAsync(targetCpuSpeed, targetGpuSpeed);
                        }
                    }
                }
                catch { /* Absorb exceptions to keep background engine alive */ }

                await timer.WaitForNextTickAsync(_engineCts.Token);
            }
        });
    }

    private void CleanupResources()
    {
        // 1. Cancel background loops and unhook events
        _engineCts.Cancel();
        _nitroHook.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerStateChanged;

        // 2. Hide and dispose tray icon to prevent ghost icons
        trayIcon.Visible = false;
        trayIcon.Dispose();
        _gpuManager.Dispose();

        // 3. Kill any open Dashboard UI processes BEFORE spawning a new one
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
    }

    private void Restart(object? sender, EventArgs e)
    {
        CleanupResources();
        Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true });
        Application.Exit();
    }

    private void Exit(object? sender, EventArgs e)
    {
        CleanupResources();
        Application.Exit();
    }
}
