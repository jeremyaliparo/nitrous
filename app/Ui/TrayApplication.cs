using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;
using Nitrous.Hooks;
using Nitrous.Managers;

namespace Nitrous.Ui;

public class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly NitroKeyHook _nitroHook;
    private readonly NvidiaGpuManager _gpuManager = new();
    private bool? _wasOnAcPower;
    private int _powerEventId;
    private readonly SemaphoreSlim _powerGate = new(1, 1);

    private readonly CancellationTokenSource _engineCts = new();
    private int _lastAppliedCpuSpeed = -1;
    private int _lastAppliedGpuSpeed = -1;
    private readonly FanRampState _cpuRamp = new();
    private readonly FanRampState _gpuRamp = new();

    /// <summary>The fan engine's period; also the step the ramp-down rate is measured against.</summary>
    private static readonly TimeSpan FanEngineTick = TimeSpan.FromSeconds(2);

    /// <summary>Forgets the ramp and the applied speeds, so the next tick starts from the curve.</summary>
    private void ResetFanRamp()
    {
        _lastAppliedCpuSpeed = _lastAppliedGpuSpeed = -1;
        _cpuRamp.Reset();
        _gpuRamp.Reset();
    }
    private DateTime _lastOcWatchdogLog = DateTime.MinValue;

    private readonly GlobalHotkeyManager _hotkeys = new();
    private readonly EventWaitHandle _hotkeyChanged = HotkeyChangeSignal.CreateReceiver();
    private readonly EventWaitHandle _appearanceChanged = AppearanceChangeSignal.CreateReceiver();

    /// <summary>The keep-awake value this tray has applied; the engine tick watches for a change.</summary>
    private bool _keepAwakeWanted;
    private readonly Control _uiInvoker = new();

    // Serializes dashboard launches. A newly spawned dashboard needs a moment to create its
    // window, so repeated hotkey presses must not be mistaken for "no dashboard is running".
    private readonly object _dashboardGate = new();
    private Process? _dashboardProcess;
    private DateTime _dashboardLaunchUtc = DateTime.MinValue;
    private static readonly TimeSpan DashboardLaunchCooldown = TimeSpan.FromSeconds(2);

    // Created on demand and pre-warmed in the background. Building it eagerly costs GDI+
    // surfaces and a window handle in every tray session, including sessions that never
    // press the profile hotkey.
    private OsdForm? _osd;

    private readonly Icon _appIcon;
    private Icon? _generatedIcon;
    private string _trayIconKey = "";

    // App profiles: the rule currently owning the mode and fans, and what to restore afterwards.
    private volatile int _appRuleProfileValue = -1;
    private volatile bool _appRuleOwned;
    private bool _appManualOverride;
    private bool _appProfileApplying;
    private bool _appStartupSweepDone;
    private PowerProfile _appRestoreProfile = PowerProfile.Performance;
    private PowerProfile _appAppliedProfile = PowerProfile.Performance;
    private int _appRuleIndex = -1;
    private string _appActiveName = "";

    private volatile int _appFanModeValue = -1; // (int)FanProfile, or no fan ownership
    private volatile bool _appFanOwned;
    private bool _appManualFanOverride;
    private bool _appFanApplying;
    private FanProfile _appRestoreFan = FanProfile.Auto;
    private FanProfile _appAppliedFan = FanProfile.Auto;

    private readonly EventWaitHandle _appProfileChanged = AppProfileChangeSignal.CreateReceiver();
    private readonly SemaphoreSlim _appProfileGate = new(1, 1);
    private ProcessEventWatcher? _processWatcher;
    private HashSet<string> _appWatchNames = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _shuttingDown;
    private int _cleanupStarted;
    private string _registeredCycleHotkey = "";
    private string _registeredDashboardHotkey = "";

    // The updater may call back from a worker thread; marshal cleanup/exit to the tray thread.
    private void ExitFromAnyThread()
    {
        if (_shuttingDown) return;

        try
        {
            if (_uiInvoker.IsDisposed) return;

            if (_uiInvoker.InvokeRequired)
                _uiInvoker.BeginInvoke((Action)(() => ExitCore(killUiProcesses: false)));
            else
                ExitCore(killUiProcesses: false);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private const int HK_CYCLE_POWER = 1;
    private const int HK_CYCLE_FAN = 2;
    private const int HK_SCREEN_SYNC = 3;
    private const int HK_SOUND_SYNC = 4;
    private const int HK_NEXT_EFFECT = 5;
    private const int HK_NVIDIA_APP = 6;
    private const int HK_DASHBOARD = 99;

    private static readonly (string Name, KeyboardEffect Effect)[] GlobalEffectCycle =
    {
        ("Static", KeyboardEffect.Static),
        ("Breathing", KeyboardEffect.Breathing),
        ("Neon", KeyboardEffect.Neon),
        ("Wave", KeyboardEffect.Wave),
        ("Ripple", KeyboardEffect.Shifting),
        ("Zoom", KeyboardEffect.Zoom),
        ("Snake", KeyboardEffect.Meteor),
        ("Disco", KeyboardEffect.Twinkling)
    };

    private static readonly (string Name, KeyboardEffect Effect)[] DynamicEffectCycle =
    {
        ("Breathing", KeyboardEffect.Breathing),
        ("Neon", KeyboardEffect.Neon),
        ("Wave", KeyboardEffect.Wave),
        ("Ripple", KeyboardEffect.Shifting),
        ("Zoom", KeyboardEffect.Zoom),
        ("Snake", KeyboardEffect.Meteor),
        ("Disco", KeyboardEffect.Twinkling)
    };

    /// <summary>Whether the keyboard has RGB zones, which screen sync and effects need.</summary>
    private static bool LightingZoned() =>
        KeyboardLighting.IsAvailable() &&
        AcerCapabilitiesProvider.Current.Smbios.KeyboardColor is not 1;

    /// <summary>Whether sound sync has a path: RGB zones or the five-step backlight.</summary>
    private static bool SoundSyncAvailable() =>
        LightingZoned() ||
        AcerWmiManager.GetKeyboardBacklightPath() != AcerWmiManager.KeyboardBacklightPath.None;

    private static bool SyncOn()
    {
        var value = SettingsManager.Get("KbSyncEnabled", -1);
        return value >= 0 ? value == 1 : SettingsManager.Get("KbScreenSync", 0) == 1;
    }

    public TrayApplication()
    {
        // Logger is initialized once in Program.Main, which also records the tray/dashboard role.

        // Force creation of the marshaling handle on the tray/UI thread.
        _ = _uiInvoker.Handle;

        _appIcon = SystemIcons.Shield;
        try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield; } catch { }

        trayIcon = new NotifyIcon { Icon = _appIcon, Visible = true, Text = "Nitrous" };
        trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(); };

        BuildContextMenu();
        RefreshTrayIcon();

        SystemEvents.PowerModeChanged += OnPowerStateChanged;

        _ = Task.Run(() => UpdateManager.CheckForUpdatesAsync(true, ExitFromAnyThread));

        _nitroHook = new NitroKeyHook();
        _nitroHook.NitroKeyPressed += (s, e) => ShowDashboard();

        // The physical Mode key, on the models whose firmware raises an event for it and applies
        // nothing itself. It walks the same cycle as Nitrous' own hotkey.
        FirmwareEvents.ModeKeyPressed += OnModeKeyPressed;
        FirmwareEvents.Start();

        _hotkeys.HotkeyPressed += OnCustomHotkeyPressed;
        ReloadHotkeys();

        _ = Task.Run(async () =>
        {
            await Task.Delay(8000);
            if (_shuttingDown) return;

            CpuPowerCap.MigrateLegacyCap(GetActivePowerProfile(
                SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online));
            await ApplyPowerSettingsAsync(isStartup: true, force: true);
            if (_shuttingDown) return;

            var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            await _gpuManager.ApplyOnBootAsync(GetActivePowerProfile(isOnline));

            // Build the overlay cards while the machine is idle. This keeps the first hotkey
            // instant without holding GDI+ surfaces for users who never switch profiles.
            if (_shuttingDown) return;

            // The OSD is only opened by an action hotkey. Without one there is nothing to
            // pre-warm, so skip the extra window handle and surfaces entirely.
            if (!AnyOsdHotkey()) return;

            RunOnUi(() => EnsureOsd()?.Prewarm());
        });

        // Apply the saved lighting preset once at startup. The dashboard no longer writes it on
        // open, so a brightness change made with the Fn keys is not undone just by showing the
        // window. Sync-enabled sessions are left to the worker.
        _ = Task.Run(() =>
        {
            try
            {
                if (SettingsManager.Get("KbSyncEnabled", -1) != 1)
                    LightingSync.ReapplySavedPreset();
            }
            catch (Exception ex)
            {
                Logger.Warn("Lighting: startup restore failed.", ex);
            }
        });

        // A thread execution-state flag, so it is set on this thread: the tray outlives every dashboard.
        _keepAwakeWanted = SettingsManager.Get("KeepAwake", 0) == 1;
        KeepAwake.Apply(_keepAwakeWanted);

        StartBackgroundEngine();
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false };
        menu.Items.Add("Open Nitrous", null, (s, e) => ShowDashboard());
        if (NvidiaTools.Find() is { } vendorTool)
            menu.Items.Add($"Open {vendorTool.Label}", null, (s, e) => NvidiaTools.Open());
        menu.Items.Add("Reset Dashboard Position", null, (s, e) => ResetDashboardPosition());
        menu.Items.Add("Check for Updates...", null, async (s, e) => await UpdateManager.CheckForUpdatesAsync(false, ExitFromAnyThread));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart", null, Restart);
        menu.Items.Add("Exit", null, Exit);
        trayIcon.ContextMenuStrip = menu;
    }

    /// <summary>
    /// Rebuilds the tray icon from the Theme choice: the app logo, the active mode's initial, or
    /// the CPU temperature. The dashboard writes the preference; this reads it on a slow timer.
    /// </summary>
    private void RefreshTrayIcon()
    {
        if (_shuttingDown || trayIcon == null) return;

        var style = UiPreferences.TrayIconStyle;
        var profile = GetActivePowerProfile(
            SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online);

        var icon = _appIcon;
        var key = "logo";
        var tooltip = "Nitrous";
        var ink = Color.FromArgb(18, 18, 20);

        if (string.Equals(style, "mode", StringComparison.Ordinal))
        {
            var fill = ModeColors.For(profile);
            key = $"mode|{profile}|{fill.ToArgb()}";
            tooltip = $"Nitrous \u00b7 {profile}";
            if (!string.Equals(key, _trayIconKey, StringComparison.Ordinal))
                icon = TrayIconRenderer.Create(profile.ToString()[..1], fill, ModeColors.InkFor(fill));
        }
        else if (string.Equals(style, "temperature", StringComparison.Ordinal))
        {
            var temperature = AcerWmiManager.GetSystemTelemetry().CpuTemp;
            if (temperature > 0)
            {
                var accent = AccentColor();
                key = $"temperature|{TemperatureDisplay.Convert(temperature)}|{accent.ToArgb()}";
                tooltip = $"Nitrous \u00b7 CPU {TemperatureDisplay.Short(temperature)}";
                if (!string.Equals(key, _trayIconKey, StringComparison.Ordinal))
                {
                    icon = TrayIconRenderer.Create(
                        $"{TemperatureDisplay.Convert(temperature)}", accent, ink);
                }
            }
            else
            {
                key = "temperature|none";
            }
        }

        // Rebuild only when the drawn content changes: at a steady temperature this skips the
        // bitmap, PNG and icon allocation on every refresh.
        if (!string.Equals(key, _trayIconKey, StringComparison.Ordinal))
        {
            _trayIconKey = key;

            // Assign before disposing: the old icon is still on screen until the new one is set.
            var previous = _generatedIcon;
            _generatedIcon = ReferenceEquals(icon, _appIcon) ? null : icon;
            trayIcon.Icon = icon;
            previous?.Dispose();
        }

        trayIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    private static Color AccentColor()
    {
        var saved = SettingsManager.Get("AccentChoice", "");
        if (saved.Length == 7 && saved[0] == '#')
        {
            try { return ColorTranslator.FromHtml(saved); } catch { }
        }

        return Color.FromArgb(179, 136, 255);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private void ShowDashboard()
    {
        // The hotkey and the tray menu can fire in quick succession, and the dashboard takes a
        // few hundred milliseconds to create its window. Without this gate, each press during
        // that gap sees "no window" and spawns another dashboard process.
        lock (_dashboardGate)
        {
            var current = Process.GetCurrentProcess();
            var processName = current.ProcessName;
            var currentId = current.Id;
            current.Dispose();

            var processes = Process.GetProcessesByName(processName);
            try
            {
                foreach (var p in processes)
                {
                    if (p.Id == currentId) continue;

                    // Found the existing UI process. Restore and bring to front.
                    var hWnd = p.MainWindowHandle;
                    if (hWnd != IntPtr.Zero)
                    {
                        const int SW_RESTORE = 9;
                        ShowWindow(hWnd, SW_RESTORE);
                        SetForegroundWindow(hWnd);
                        return; // Prevent spawning a new instance
                    }
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }

            // The dashboard is still starting, so its window does not exist yet. Ignore the
            // extra press instead of starting a second copy.
            if (_dashboardProcess != null)
            {
                if (!HasExited(_dashboardProcess))
                    return;

                _dashboardProcess.Dispose();
                _dashboardProcess = null;
            }

            // A very recent launch that did not produce a process (for example, the shell
            // handed the request to an existing instance) is also treated as a repeat.
            if (DateTime.UtcNow - _dashboardLaunchUtc < DashboardLaunchCooldown)
                return;

            try
            {
                _dashboardProcess = Process.Start(
                    new ProcessStartInfo(Application.ExecutablePath, "--ui") { UseShellExecute = true });
                _dashboardLaunchUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Logger.Warn("Dashboard: could not start the UI process.", ex);
            }
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    /// <summary>
    /// Clears the saved dashboard position and, if the dashboard is already open (possibly on
    /// a monitor that was just removed), moves its window back to the primary work area.
    /// </summary>
    private void ResetDashboardPosition()
    {
        SettingsManager.Delete("WindowTop");
        SettingsManager.Delete("WindowLeft");

        var current = Process.GetCurrentProcess();
        var processName = current.ProcessName;
        var currentId = current.Id;
        current.Dispose();

        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.Id == currentId) continue;

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) continue;

                var width = Math.Max(1, rect.Right - rect.Left);
                var height = Math.Max(1, rect.Bottom - rect.Top);
                var x = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
                var y = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);

                const int SW_RESTORE = 9;
                ShowWindow(handle, SW_RESTORE);
                SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                SetForegroundWindow(handle);
            }
            catch (Exception ex)
            {
                Logger.Warn("Dashboard: could not reset the window position.", ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (trayIcon.Visible)
            trayIcon.ShowBalloonTip(3000, "Nitrous", "Dashboard position reset. It will open centered.", ToolTipIcon.Info);
    }

    private async void OnPowerStateChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            var eventId = Interlocked.Increment(ref _powerEventId);
            await Task.Delay(3500);
            if (eventId != Volatile.Read(ref _powerEventId) || _shuttingDown) return;

            await ApplyPowerSettingsAsync(isStartup: false, force: false);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Logger.Info("Power: resumed from sleep; re-applying hardware state.");

            // The dGPU driver and EC frequently reset offsets/limits across sleep. Give the
            // driver a moment to come back, then force a full re-apply (including charge limit).
            await Task.Delay(6000);
            if (_shuttingDown) return;

            ResetFanRamp();
            await ApplyPowerSettingsAsync(isStartup: false, force: true);
            if (_shuttingDown) return;

            var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            await _gpuManager.ApplyOnBootAsync(GetActivePowerProfile(isOnline));

            // The dGPU/EC readback is back; reconcile the process set in case WMI events were
            // missed while Windows was asleep.
            if (_processWatcher is { IsRunning: true }) _processWatcher.Resync();
            else _ = EvaluateAppProfilesAsync("resume");
        }
    }

    private static PowerProfile GetBasePowerProfile(bool isOnline)
    {
        PowerProfile profile;
        if (SettingsManager.Get("AutoSwitch", 0) == 1)
        {
            var key = isOnline ? "LastAcPowerMode" : "LastDcPowerMode";
            profile = (PowerProfile)SettingsManager.Get(key, (int)(isOnline ? PowerProfile.Performance : PowerProfile.Quiet));
        }
        else
        {
            profile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
        }

        if (!Enum.IsDefined(profile))
            profile = PowerProfile.Performance;

        if (profile == PowerProfile.Turbo && !AcerWmiManager.IsTurboModeSupported())
            profile = PowerProfile.Performance;

        return profile;
    }

    /// <summary>
    /// The profile the hardware should hold now: an app rule's target when one owns the mode,
    /// otherwise the user's AC/DC or last manual choice.
    /// </summary>
    private PowerProfile GetActivePowerProfile(bool isOnline)
    {
        var rule = _appRuleProfileValue;
        if (rule >= 0)
            return _appManualOverride ? _appAppliedProfile : (PowerProfile)rule;

        return GetBasePowerProfile(isOnline);
    }

    private static FanProfile GetBaseFan(bool isOnline)
    {
        if (SettingsManager.Get("AutoSwitch", 0) == 1)
        {
            var key = isOnline ? "LastAcFanMode" : "LastDcFanMode";
            return Enum.TryParse(SettingsManager.Get(key, "Auto"), out FanProfile f) ? f : FanProfile.Auto;
        }

        return Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile f2) ? f2 : FanProfile.Auto;
    }

    /// <summary>The fan mode the hardware should hold: the rule's when it owns the fans, else the base.</summary>
    private FanProfile GetActiveFan(bool isOnline)
    {
        var fan = _appFanModeValue;
        if (fan >= 0)
            return _appManualFanOverride ? _appAppliedFan : (FanProfile)fan;

        return GetBaseFan(isOnline);
    }

    private async Task ApplyPowerSettingsAsync(bool isStartup, bool force)
    {
        await _powerGate.WaitAsync();
        try
        {
            if (_shuttingDown) return;

            var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

            // StatusChange fires more than once for a single plug/unplug. Only act on an
            // actual AC/DC transition unless the caller wants a forced re-apply (startup/resume).
            if (!isStartup && !force && _wasOnAcPower.HasValue && _wasOnAcPower.Value == isOnline)
                return;

            _wasOnAcPower = isOnline;

            var profile = GetActivePowerProfile(isOnline);
            var fan = GetActiveFan(isOnline);

            SettingsManager.Save("LastPowerMode", (int)profile);
            // A rule must not overwrite the AC/DC base; that is what it restores to on exit.
            if (!_appRuleOwned)
                SettingsManager.Save(isOnline ? "LastAcPowerMode" : "LastDcPowerMode", (int)profile);
            SettingsManager.Save("LastFanMode", fan.ToString());

            Logger.Info($"Power: applying {profile} / fan {fan} on {(isOnline ? "AC" : "DC")} " +
                        $"(startup={isStartup}, force={force}).");

            // Always apply the selected power mode at startup, not only when AutoSwitch is on.
            // This was the main reason a saved mode/fan combo did not survive a reboot.
            await AcerWmiManager.SetPowerModeAsync(profile);

            if (fan == FanProfile.Medium)
            {
                // The background curve engine owns the fans when curve mode is on. Otherwise
                // apply the saved manual speeds.
                if (SettingsManager.Get("IsCurveModeEnabled", 0) != 1)
                {
                    var cpu = FanSafety.Clamp(SettingsManager.Get("CustomFanSpeedCpu", 50));
                    var gpu = FanSafety.Clamp(SettingsManager.Get("CustomFanSpeedGpu", 50));
                    await AcerWmiManager.SetCustomFansAsync(cpu, gpu);
                }
            }
            else
            {
                await AcerWmiManager.SetFansAsync(fan);
            }

            if (SettingsManager.Get("ManageCpuPower", 0) == 1)
                await CpuPowerManager.ApplyProfileLimitsAsync(profile, isOnline);
            else
                await CpuPowerManager.RestoreOriginalAsync();

            DisplayManager.Apply(isOnline);

            // Charge limit is a hardware latch with no readback: the toggle shows the saved
            // setting, not what the EC holds. Re-assert it on every power event (startup, resume
            // and AC/DC), because an EC reset on a charger change otherwise leaves it off while
            // the setup page still says on. The write is cheap and idempotent.
            var chargeLimit = SettingsManager.Get("ChargeLimit", 0) == 1;
            await AcerWmiManager.SetChargeLimitAsync(chargeLimit);

            await _gpuManager.ApplyPowerProfileOcAsync(
                profile,
                allowStockReset: true,
                source: isStartup ? "startup-apply" : "acdc");
            ResetFanRamp();

            // The cap is bounded by the mode's own request, so it is applied only after the mode and
            // the EC's limits have settled - and re-applied here on every startup, resume and
            // adapter change, because the firmware puts its own value back on resume.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                if (_shuttingDown) return;
                CpuPowerCap.Reassert(profile, modeChanged: true);
                GpuThermalTarget.Reassert(profile);
                GpuClockLimit.Reassert(profile);
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Power: failed to apply hardware settings.", ex);
        }
        finally
        {
            _powerGate.Release();
        }
    }

    /// <summary>
    /// Matches the enabled rules against the running processes. One snapshot per tick; the first
    /// rule in the list wins. Applying a rule snapshots the mode and fans to restore and stops
    /// overwriting the AC/DC slots, so reverting cannot restore the game profile by accident.
    /// Power and fans are owned independently: a rule may change one or both.
    /// </summary>
    private async Task EvaluateAppProfilesAsync(string reason)
    {
        await _appProfileGate.WaitAsync();
        try
        {
            await EvaluateAppProfilesCoreAsync(reason);
        }
        finally
        {
            _appProfileGate.Release();
        }
    }

    private async Task EvaluateAppProfilesCoreAsync(string reason)
    {
        if (_shuttingDown) return;

        if (!AppProfileStore.Enabled)
        {
            if (_appRuleOwned || _appFanOwned) await RestoreAppRuleAsync("app profiles disabled");
            else ClearStaleRestore();
            return;
        }

        var rules = AppProfileStore.Load();
        if (!rules.Exists(r => r.Enabled))
        {
            if (_appRuleOwned || _appFanOwned) await RestoreAppRuleAsync("no enabled rules");
            else ClearStaleRestore();
            return;
        }

        var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

        // A manual change while a rule owns the setting becomes the restore target, so the engine
        // does not fight the user. A new winning app clears both overrides.
        if (_appRuleOwned && !_appProfileApplying)
        {
            var current = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
            if (Enum.IsDefined(current) && current != _appAppliedProfile)
            {
                _appManualOverride = true;
                _appRestoreProfile = current;
                _appAppliedProfile = current;
                Logger.Info($"App profiles: manual {current} wins until the active app changes.");
            }
        }

        if (_appFanOwned && !_appFanApplying)
        {
            var currentFan = Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile f)
                ? f
                : FanProfile.Auto;
            if (currentFan != _appAppliedFan)
            {
                _appManualFanOverride = true;
                _appRestoreFan = currentFan;
                _appAppliedFan = currentFan;
                Logger.Info($"App profiles: manual fan {currentFan} wins until the active app changes.");
            }
        }

        HashSet<string> running;
        try
        {
            running = CurrentRunningNames();
        }
        catch (Exception ex)
        {
            Logger.Warn("App profiles: could not enumerate processes.", ex);
            return;
        }

        var index = rules.FindIndex(rule => rule.Enabled && running.Contains(rule.MatchName));
        if (index < 0)
        {
            if (_appRuleOwned || _appFanOwned) await RestoreAppRuleAsync("app closed");
            else ClearStaleRestore();
            return;
        }

        var rule = rules[index];
        var target = AppProfileStore.ClampForThisMachine(rule.Profile);
        var targetFan = FanFor(rule.Fan);
        var newSession = !_appRuleOwned && !_appFanOwned;
        var winnerChanged = !newSession && _appRuleIndex != index;

        if (winnerChanged)
        {
            // A new app takes over: its rule wins over any manual setting for the previous one.
            _appManualOverride = false;
            _appManualFanOverride = false;
        }

        if (newSession)
        {
            // A tray restart during a game loses the live bases; the saved values survive it.
            var savedPower = SettingsManager.Get(AppProfileStore.RestoreKey, -1);
            _appRestoreProfile = savedPower >= 0 && Enum.IsDefined((PowerProfile)savedPower)
                ? (PowerProfile)savedPower
                : GetBasePowerProfile(isOnline);
            SettingsManager.Save(AppProfileStore.RestoreKey, (int)_appRestoreProfile);

            var savedFan = SettingsManager.Get(AppProfileStore.FanRestoreKey, "");
            _appRestoreFan = Enum.TryParse<FanProfile>(savedFan, out var restoredFan)
                ? restoredFan
                : GetBaseFan(isOnline);
            SettingsManager.Save(AppProfileStore.FanRestoreKey, _appRestoreFan.ToString());
            _appStartupSweepDone = true;
        }

        _appRuleIndex = index;
        _appActiveName = rule.DisplayName;

        // --- power --------------------------------------------------------------------------
        if (!_appManualOverride && (!_appRuleOwned || target != _appAppliedProfile))
        {
            _appRuleOwned = true;
            _appRuleProfileValue = (int)target;
            _appAppliedProfile = target;

            Logger.Info($"App profiles: {rule.Executable} -> {target}.");
            _appProfileApplying = true;
            try
            {
                await ApplyAppRuleProfileAsync(target, "app-rule");
                if (newSession) ShowRuleOsd(rule.DisplayName, target);
            }
            catch (Exception ex)
            {
                Logger.Error("App profiles: the mode switch failed.", ex);
            }
            finally
            {
                _appProfileApplying = false;
            }
        }

        // --- fans ---------------------------------------------------------------------------
        if (targetFan is null)
        {
            if (_appFanOwned)
            {
                var restoreFan = ChooseFanRestore(isOnline);
                var manual = _appManualFanOverride;
                _appFanOwned = false;
                _appFanModeValue = -1;
                _appManualFanOverride = false;
                SettingsManager.Delete(AppProfileStore.FanRestoreKey);

                if (!manual)
                {
                    Logger.Info($"App profiles: fans restored to {restoreFan}.");
                    _appFanApplying = true;
                    try
                    {
                        await ApplyAppRuleFanAsync(restoreFan);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("App profiles: the fan restore failed.", ex);
                    }
                    finally
                    {
                        _appFanApplying = false;
                    }
                }
            }
        }
        else
        {
            var fan = targetFan.Value;
            if (!_appFanOwned)
            {
                if (!newSession)
                {
                    _appRestoreFan = GetBaseFan(isOnline);
                    SettingsManager.Save(AppProfileStore.FanRestoreKey, _appRestoreFan.ToString());
                }

                _appFanOwned = true;
                _appManualFanOverride = false;
            }

            if (!_appManualFanOverride && (!_appAppliedFan.Equals(fan) || _appFanModeValue < 0))
            {
                _appFanModeValue = (int)fan;
                _appAppliedFan = fan;
                Logger.Info($"App profiles: fans -> {fan}.");
                _appFanApplying = true;
                try
                {
                    await ApplyAppRuleFanAsync(fan);
                }
                catch (Exception ex)
                {
                    Logger.Error("App profiles: the fan switch failed.", ex);
                }
                finally
                {
                    _appFanApplying = false;
                }
            }
        }

        // Keep the active label current for fan-only changes and name edits.
        var label = _appManualOverride
            ? $"{rule.DisplayName} \u2192 {_appAppliedProfile} (manual)"
            : ActiveRuleText(rule, target);
        if (!string.Equals(SettingsManager.Get(AppProfileStore.ActiveKey, ""), label, StringComparison.Ordinal))
            SettingsManager.Save(AppProfileStore.ActiveKey, label);
    }

    private async Task RestoreAppRuleAsync(string reason)
    {
        var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        var powerOwned = _appRuleOwned;
        var fanOwned = _appFanOwned;

        // With AutoSwitch on the AC/DC slots were never touched, so they are the truthful base;
        // with it off the saved snapshots are the base because Last*Mode holds the rule value.
        var powerTarget = powerOwned
            ? AppProfileStore.ClampForThisMachine(_appManualOverride
                ? _appRestoreProfile
                : SettingsManager.Get("AutoSwitch", 0) == 1
                    ? GetBasePowerProfile(isOnline)
                    : _appRestoreProfile)
            : (PowerProfile?)null;
        var fanTarget = fanOwned ? ChooseFanRestore(isOnline) : (FanProfile?)null;

        var previousPower = _appAppliedProfile;
        var previousFan = _appAppliedFan;
        _appRuleOwned = false;
        _appRuleProfileValue = -1;
        _appRuleIndex = -1;
        _appManualOverride = false;
        _appActiveName = "";
        _appStartupSweepDone = true;
        _appFanOwned = false;
        _appFanModeValue = -1;
        _appManualFanOverride = false;
        SettingsManager.Save(AppProfileStore.ActiveKey, "");
        SettingsManager.Delete(AppProfileStore.RestoreKey);
        SettingsManager.Delete(AppProfileStore.FanRestoreKey);

        Logger.Info($"App profiles: {reason}; restoring {powerTarget?.ToString() ?? "mode untouched"}/" +
                    $"{fanTarget?.ToString() ?? "fans untouched"} (was {previousPower}/{previousFan}).");

        if (powerTarget is { } power)
        {
            try
            {
                await ApplyAppRuleProfileAsync(power, "app-rule-restore");
            }
            catch (Exception ex)
            {
                Logger.Error("App profiles: the mode restore failed.", ex);
            }

            _appAppliedProfile = power;
        }

        if (fanTarget is { } fan)
        {
            try
            {
                await ApplyAppRuleFanAsync(fan);
            }
            catch (Exception ex)
            {
                Logger.Error("App profiles: the fan restore failed.", ex);
            }

            _appAppliedFan = fan;
        }
    }

    private async Task ApplyAppRuleFanAsync(FanProfile fan)
    {
        await _powerGate.WaitAsync();
        try
        {
            if (_shuttingDown) return;

            if (fan == FanProfile.Medium)
            {
                // Curve mode owns the fans once LastFanMode says Medium; otherwise the saved
                // manual speeds are the custom mode.
                if (SettingsManager.Get("IsCurveModeEnabled", 0) != 1)
                {
                    var cpu = FanSafety.Clamp(SettingsManager.Get("CustomFanSpeedCpu", 50));
                    var gpu = FanSafety.Clamp(SettingsManager.Get("CustomFanSpeedGpu", 50));
                    await AcerWmiManager.SetCustomFansAsync(cpu, gpu);
                }
            }
            else
            {
                await AcerWmiManager.SetFansAsync(fan);
            }

            SettingsManager.Save("LastFanMode", fan.ToString());
            ResetFanRamp();
        }
        finally
        {
            _powerGate.Release();
        }
    }

    private static FanProfile? FanFor(AppFanMode mode) => mode switch
    {
        AppFanMode.Auto => FanProfile.Auto,
        AppFanMode.Max => FanProfile.Max,
        AppFanMode.Custom => FanProfile.Medium,
        _ => null
    };

    private FanProfile ChooseFanRestore(bool isOnline) =>
        _appManualFanOverride
            ? _appRestoreFan
            : SettingsManager.Get("AutoSwitch", 0) == 1
                ? GetBaseFan(isOnline)
                : _appRestoreFan;


    /// <summary>Applies just the parts a profile owns; fans, charge and display are not per-app.</summary>
    private async Task ApplyAppRuleProfileAsync(PowerProfile profile, string source)
    {
        await _powerGate.WaitAsync();
        try
        {
            if (_shuttingDown) return;

            var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            await AcerWmiManager.SetPowerModeAsync(profile);
            SettingsManager.Save("LastPowerMode", (int)profile);

            CpuPowerCap.Reassert(profile, modeChanged: true);
            GpuThermalTarget.Reassert(profile);
            GpuClockLimit.Reassert(profile);

            if (SettingsManager.Get("ManageCpuPower", 0) == 1)
                await CpuPowerManager.ApplyProfileLimitsAsync(profile, isOnline);

            await _gpuManager.ApplyPowerProfileOcAsync(profile, allowStockReset: true, source: source);
            ResetFanRamp();

            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                if (_shuttingDown) return;
                CpuPowerCap.Reassert(profile, modeChanged: true);
                GpuThermalTarget.Reassert(profile);
                GpuClockLimit.Reassert(profile);
            });
        }
        finally
        {
            _powerGate.Release();
        }
    }

    private static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }

    private HashSet<string> CurrentRunningNames()
    {
        var watcher = _processWatcher;
        return watcher is { IsRunning: true } ? watcher.RunningNames() : RunningProcessNames();
    }

    private void StartAppProfileEngine()
    {
        ReloadAppWatchNames();

        _processWatcher = new ProcessEventWatcher();
        _processWatcher.ProcessChanged += OnProcessChanged;
        _processWatcher.Resynced += OnProcessResynced;

        if (!_processWatcher.Start())
        {
            Logger.Warn("App profiles: process events unavailable; using the polling fallback.");
            _processWatcher.Dispose();
            _processWatcher = null;
            StartAppProfilePollingFallback();
        }

        _ = EvaluateAppProfilesAsync("startup");

        Task.Run(() =>
        {
            var handles = new WaitHandle[] { _appProfileChanged, _engineCts.Token.WaitHandle };
            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    if (WaitHandle.WaitAny(handles) != 0) break;
                    ReloadAppWatchNames();
                    _ = EvaluateAppProfilesAsync("settings changed");
                }
            }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        });
    }

    private void StartAppProfilePollingFallback()
    {
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    await timer.WaitForNextTickAsync(_engineCts.Token);

                    try
                    {
                        await EvaluateAppProfilesAsync("fallback poll");
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("App profiles: fallback evaluation failed.", ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void ReloadAppWatchNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in AppProfileStore.Load())
        {
            if (rule.Enabled)
                names.Add(rule.MatchName);
        }

        _appWatchNames = names;
    }

    private void OnProcessChanged(string name, bool started)
    {
        if (!_appWatchNames.Contains(name)) return;
        _ = EvaluateAppProfilesAsync(started ? "process started" : "process stopped");
    }

    private void OnProcessResynced() => _ = EvaluateAppProfilesAsync("process resync");

    private void ShowRuleOsd(string app, PowerProfile profile)
    {
        var title = string.IsNullOrWhiteSpace(app) ? profile.ToString().ToUpperInvariant() : app.ToUpperInvariant();
        ShowStatusOsd(ModeColors.For(profile),
            $"APP PROFILE \u00b7 {profile.ToString().ToUpperInvariant()}", title);
    }

    private static string ActiveRuleText(AppProfileRule rule, PowerProfile profile) =>
        rule.Fan == AppFanMode.Leave
            ? $"{rule.DisplayName} \u2192 {profile}"
            : $"{rule.DisplayName} \u2192 {profile} \u00b7 {rule.Fan} fans";

    /// <summary>No app is running at tray start: saved restore values are stale.</summary>
    private void ClearStaleRestore()
    {
        if (_appStartupSweepDone) return;
        _appStartupSweepDone = true;
        SettingsManager.Delete(AppProfileStore.RestoreKey);
        SettingsManager.Delete(AppProfileStore.FanRestoreKey);
    }

    private void ReloadHotkeys()
    {
        if (_shuttingDown) return;

        _hotkeys.UnregisterAll(
            HK_CYCLE_POWER, HK_CYCLE_FAN, HK_SCREEN_SYNC, HK_SOUND_SYNC, HK_NEXT_EFFECT, HK_DASHBOARD);

        RegisterSavedHotkey("Hotkey_CyclePower", HK_CYCLE_POWER);
        RegisterSavedHotkey("Hotkey_Dashboard", HK_DASHBOARD);
        RegisterSavedHotkey("Hotkey_CycleFan", HK_CYCLE_FAN);
        RegisterSavedHotkey("Hotkey_NvidiaApp", HK_NVIDIA_APP);
        // Registered whether or not a vendor tool was found: detection can fail at logon, when the
        // Start apps list is not populated yet, and the press resolves it then. It also must not
        // live inside a lighting branch - a single-colour keyboard has no zones and would skip it.

        if (LightingZoned())
        {
            RegisterSavedHotkey("Hotkey_ScreenSync", HK_SCREEN_SYNC);
            RegisterSavedHotkey("Hotkey_NextEffect", HK_NEXT_EFFECT);
        }

        if (SoundSyncAvailable())
            RegisterSavedHotkey("Hotkey_SoundSync", HK_SOUND_SYNC);

        _registeredCycleHotkey = SettingsManager.Get("Hotkey_CyclePower", "");
        _registeredDashboardHotkey = SettingsManager.Get("Hotkey_Dashboard", "");
    }

    /// <summary>Any action that needs the OSD, so the tray only pays for it when it is used.</summary>
    private static bool AnyOsdHotkey()
    {
        string[] keys =
        {
            "Hotkey_CyclePower", "Hotkey_CycleFan", "Hotkey_ScreenSync",
            "Hotkey_SoundSync", "Hotkey_NextEffect"
        };

        foreach (var key in keys)
            if (!string.IsNullOrEmpty(SettingsManager.Get(key, "")))
                return true;

        return false;
    }

    private void RegisterSavedHotkey(string settingKey, int id)
    {
        var saved = SettingsManager.Get(settingKey, "");
        if (string.IsNullOrEmpty(saved)) return;

        if (!HotkeyBinding.TryParse(saved, out var modifiers, out var virtualKey))
        {
            Logger.Warn($"Hotkey: ignoring invalid binding for {settingKey}: '{saved}'.");
            return;
        }

        if (!_hotkeys.Register(id, modifiers, virtualKey))
            Logger.Warn($"Hotkey: registration failed for {settingKey} ({HotkeyBinding.Format(saved)}).");
        else
            Logger.Info($"Hotkey: {settingKey} bound to {HotkeyBinding.Format(saved)}.");
    }

    private void OnCustomHotkeyPressed(int id)
    {
        switch (id)
        {
            case HK_CYCLE_POWER:
                _ = CyclePowerModeAsync();
                break;
            case HK_CYCLE_FAN:
                _ = CycleFanModeAsync();
                break;
            case HK_SCREEN_SYNC:
                ToggleLightingSync(LightingSync.SourceScreen);
                break;
            case HK_SOUND_SYNC:
                ToggleLightingSync(LightingSync.SourceSound);
                break;
            case HK_NEXT_EFFECT:
                CycleLightingEffect();
                break;
            case HK_DASHBOARD:
                ShowDashboard();
                break;
            case HK_NVIDIA_APP:
                NvidiaTools.Open();
                break;
        }
    }

    /// <summary>Last Mode-key press, so the firmware's press-and-release pair is one step.</summary>
    private DateTime _lastModeKey;

    /// <summary>
    /// The physical Mode key. A worker thread raises it, and the cycle is the one the hotkey uses,
    /// so the two never disagree about the mode.
    ///
    /// The firmware sends the press and its release (values 4 and 5); a press that arrives within
    /// 400 ms of the last is the same one. A model whose firmware applies the key itself never
    /// raises the event, so this cannot double-step where the key already worked.
    /// </summary>
    private void OnModeKeyPressed()
    {
        if (_shuttingDown) return;

        var now = DateTime.UtcNow;
        if (now - _lastModeKey < TimeSpan.FromMilliseconds(400)) return;
        _lastModeKey = now;

        _ = Task.Run(async () =>
        {
            try { await CyclePowerModeAsync(); }
            catch (Exception ex) { Logger.Warn("Power: the mode key could not change the mode.", ex); }
        });
    }

    private async Task CyclePowerModeAsync()
    {
        if (_shuttingDown) return;

        await _powerGate.WaitAsync();
        try
        {
            if (_shuttingDown) return;

            var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            var acDcKey = isOnline ? "LastAcPowerMode" : "LastDcPowerMode";

            var currentProfile = GetActivePowerProfile(isOnline);
            var nextProfile = currentProfile switch
            {
                PowerProfile.Quiet => PowerProfile.Balanced,
                PowerProfile.Balanced => PowerProfile.Performance,
                PowerProfile.Performance => AcerWmiManager.IsTurboModeSupported() ? PowerProfile.Turbo : PowerProfile.Quiet,
                _ => PowerProfile.Quiet
            };

            SettingsManager.Save("LastPowerMode", (int)nextProfile);
            SettingsManager.Save(acDcKey, (int)nextProfile);

            // Show the toast before the hardware awaits: NVAPI/WMI writes can take a few
            // hundred milliseconds on a sleeping dGPU, and the overlay should not wait for them.
            ShowProfileOsd(nextProfile);

            try
            {
                await AcerWmiManager.SetPowerModeAsync(nextProfile).ConfigureAwait(false);

                if (SettingsManager.Get("ManageCpuPower", 0) == 1)
                    await CpuPowerManager.ApplyProfileLimitsAsync(nextProfile, isOnline).ConfigureAwait(false);

                await _gpuManager.ApplyPowerProfileOcAsync(nextProfile, allowStockReset: true, source: "hotkey").ConfigureAwait(false);

                // These are global driver settings held per profile, so the mode change is where the next
                // profile's values (or the defaults) are applied.
                GpuThermalTarget.Reassert(nextProfile);
                GpuClockLimit.Reassert(nextProfile);

                ResetFanRamp();
            }
            catch (Exception ex)
            {
                Logger.Error($"Power: failed to cycle to {nextProfile}.", ex);
            }
        }
        finally
        {
            _powerGate.Release();
        }

        _ = EvaluateAppProfilesAsync("manual power");
    }

    private void ShowProfileOsd(PowerProfile profile)
    {
        if (_shuttingDown) return;

        var color = ModeColors.For(profile);

        // The hardware awaits use ConfigureAwait(false), so this may run on a worker thread.
        // Marshal through the control whose handle was created on the UI thread; the OSD form
        // itself may not have a handle yet on its first show.
        RunOnUi(() => EnsureOsd()?.ShowProfile(color, profile));
    }

    private void ShowStatusOsd(Color color, string caption, string title)
    {
        if (_shuttingDown) return;
        RunOnUi(() => EnsureOsd()?.ShowStatus(color, caption, title));
    }

    private async Task CycleFanModeAsync()
    {
        if (_shuttingDown) return;

        var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        var current = GetActiveFan(isOnline);
        var next = current switch
        {
            FanProfile.Auto => FanProfile.Max,
            FanProfile.Max => FanProfile.Medium,
            FanProfile.Medium => FanProfile.Auto,
            _ => FanProfile.Auto
        };

        SettingsManager.Save("LastFanMode", next.ToString());
        SettingsManager.Save(isOnline ? "LastAcFanMode" : "LastDcFanMode", next.ToString());

        var curve = SettingsManager.Get("IsCurveModeEnabled", 0) == 1;
        var title = next switch
        {
            FanProfile.Auto => "AUTO",
            FanProfile.Max => "MAX",
            FanProfile.Medium => curve ? "CUSTOM (CURVE)" : "CUSTOM (FIXED)",
            _ => next.ToString().ToUpperInvariant()
        };

        ShowStatusOsd(Color.FromArgb(255, 159, 10), "FAN MODE", title);

        try
        {
            if (_appFanOwned)
            {
                // ApplyPowerSettingsAsync would read the rule's fan back and undo the manual
                // choice; apply this one directly so the override is what the hardware holds.
                await ApplyAppRuleFanAsync(next).ConfigureAwait(false);
            }
            else
            {
                await ApplyPowerSettingsAsync(isStartup: false, force: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Fans: the hotkey cycle failed.", ex);
        }

        _ = EvaluateAppProfilesAsync("manual fan");
    }

    /// <summary>
    /// One key per source. Pressing the active source turns sync off; pressing the other source
    /// switches to it and turns it on.
    /// </summary>
    private void ToggleLightingSync(string source)
    {
        if (_shuttingDown) return;

        var on = SyncOn();
        var current = SettingsManager.Get("KbSyncSource", LightingSync.SourceScreen);
        var same = string.Equals(current, source, StringComparison.OrdinalIgnoreCase);
        var enable = !on || !same;

        SettingsManager.Save("KbSyncEnabled", enable ? 1 : 0);
        if (enable) SettingsManager.Save("KbSyncSource", source);

        var label = string.Equals(source, LightingSync.SourceScreen, StringComparison.Ordinal)
            ? "SCREEN"
            : "SOUND";
        ShowStatusOsd(Color.FromArgb(167, 139, 250), "LIGHTING SYNC",
            $"{label} {(enable ? "ON" : "OFF")}");

        // Start/Stop joins the previous worker, so keep that off the hotkey's UI thread.
        _ = Task.Run(() =>
        {
            try
            {
                if (!enable)
                {
                    LightingSync.Stop(restorePreset: true);
                    return;
                }

                LightingSync.Start(source);
                if (LightingSync.IsRunning) return;

                SettingsManager.Save("KbSyncEnabled", 0);
                ShowStatusOsd(Color.FromArgb(255, 69, 58), "LIGHTING SYNC", "UNAVAILABLE");
            }
            catch (Exception ex)
            {
                Logger.Warn("Lighting sync: the hotkey toggle failed.", ex);
            }
        });
    }

    private void CycleLightingEffect()
    {
        if (_shuttingDown) return;

        if (!LightingZoned())
        {
            ShowStatusOsd(Color.FromArgb(255, 69, 58), "LIGHTING", "NOT AVAILABLE");
            return;
        }

        if (SyncOn())
        {
            ShowStatusOsd(Color.FromArgb(167, 139, 250), "LIGHTING", "SYNC IS ON");
            return;
        }

        var area = string.Equals(SettingsManager.Get("KbLightingSource", "Global"), "Area",
            StringComparison.OrdinalIgnoreCase);

        string caption;
        string name;

        if (area)
        {
            var dynamic = string.Equals(SettingsManager.Get("KbStyle", "Static"), "Dynamic",
                StringComparison.OrdinalIgnoreCase);
            var current = AcerKeyboard.ParseEffect(SettingsManager.Get("KbEffect", "Breathing"));
            var cycle = DynamicEffectCycle.Where(entry => KeyboardLighting.SupportsEffect(entry.Effect)).ToArray();
            var index = Array.FindIndex(cycle, entry => entry.Effect == current);
            var next = dynamic
                ? (index + 1 + cycle.Length) % cycle.Length
                : index >= 0 ? index : 0;

            name = cycle[next].Name;
            SettingsManager.Save("KbStyle", "Dynamic");
            SettingsManager.Save("KbEffect", name);
            caption = "AREA EFFECT";
        }
        else
        {
            var current = AcerKeyboard.ParseEffect(SettingsManager.Get("KbGlobal_Effect", "Static"));
            var cycle = GlobalEffectCycle.Where(entry => KeyboardLighting.SupportsEffect(entry.Effect)).ToArray();
            var index = Array.FindIndex(cycle, entry => entry.Effect == current);
            var next = (index + 1 + cycle.Length) % cycle.Length;

            name = cycle[next].Name;
            SettingsManager.Save("KbGlobal_Effect", name);
            caption = "GLOBAL EFFECT";
        }

        SettingsManager.Save("KbLightingSource", area ? "Area" : "Global");
        ShowStatusOsd(Color.FromArgb(167, 139, 250), caption, name.ToUpperInvariant());

        _ = Task.Run(() => LightingSync.ReapplySavedPreset());
    }

    private OsdForm? EnsureOsd()
    {
        if (_shuttingDown) return null;
        if (_osd != null) return _osd;

        // The form owns a WinForms timer, so it must be created on the tray/UI thread.
        if (_uiInvoker.InvokeRequired)
        {
            OsdForm? created = null;
            try { _uiInvoker.Invoke((Action)(() => created = EnsureOsd())); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return created;
        }

        try
        {
            _osd = new OsdForm();
            return _osd;
        }
        catch (Exception ex)
        {
            Logger.Warn("OSD: could not create the overlay window.", ex);
            return null;
        }
    }

    private void RunOnUi(Action action)
    {
        try
        {
            if (_uiInvoker.IsDisposed) return;

            if (_uiInvoker.InvokeRequired)
            {
                _uiInvoker.BeginInvoke(action);
                return;
            }
        }
        catch (ObjectDisposedException) { return; }
        catch (InvalidOperationException) { return; }

        action();
    }

    private void StartBackgroundEngine()
    {
        // Hotkey changes arrive immediately from the dashboard through a named event. The
        // two-second registry poll below stays as a fallback (for example if the dashboard was
        // started before this tray instance). Registration is marshaled to the tray thread that
        // owns the hidden message window.
        Task.Run(() =>
        {
            var handles = new WaitHandle[] { _hotkeyChanged, _engineCts.Token.WaitHandle };

            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    if (WaitHandle.WaitAny(handles) != 0) break;
                    RunOnUi(ReloadHotkeys);
                }
            }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        });

        // Tray-visible appearance choices (icon style, colours, accent) wake the icon at once;
        // the ten-second poll below remains the fallback if no signal arrives.
        Task.Run(() =>
        {
            var handles = new WaitHandle[] { _appearanceChanged, _engineCts.Token.WaitHandle };

            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    if (WaitHandle.WaitAny(handles) != 0) break;
                    RunOnUi(RefreshTrayIcon);
                }
            }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        });

        // Fan curve engine.
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(FanEngineTick);
            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // The dashboard is a separate process. Pick up hotkey edits without
                        // requiring a restart of the background engine.
                        // The dashboard writes the preference; this thread holds the request. Only a
                        // change is marshalled, so a steady setting costs one registry read per tick.
                        var keepAwake = SettingsManager.Get("KeepAwake", 0) == 1;
                        if (keepAwake != _keepAwakeWanted)
                        {
                            _keepAwakeWanted = keepAwake;
                            RunOnUi(() => KeepAwake.Apply(keepAwake));
                        }

                        var cycleHotkey = SettingsManager.Get("Hotkey_CyclePower", "");
                        var dashboardHotkey = SettingsManager.Get("Hotkey_Dashboard", "");
                        if (!string.Equals(cycleHotkey, _registeredCycleHotkey, StringComparison.Ordinal) ||
                            !string.Equals(dashboardHotkey, _registeredDashboardHotkey, StringComparison.Ordinal))
                        {
                            RunOnUi(ReloadHotkeys);
                        }

                        var currentFanMode = SettingsManager.Get("LastFanMode", "Auto");
                        var isCurveEnabled = SettingsManager.Get("IsCurveModeEnabled", 0) == 1;

                        if (string.Equals(currentFanMode, FanProfile.Medium.ToString(), StringComparison.OrdinalIgnoreCase) &&
                            isCurveEnabled)
                        {
                            var telemetry = AcerWmiManager.GetSystemTelemetry();
                            var effectiveGpuTemp = telemetry.GpuTemp;
                            var deepTelemetry = SettingsManager.Get("DeepGpuTelemetry", 1) == 1;

                            if (effectiveGpuTemp == 0 && deepTelemetry && _gpuManager.IsGpuAwake)
                            {
                                // Only ask NVML while Windows reports the device as powered. The
                                // Acer EC returns 0 for an idle dGPU, and waking it every two
                                // seconds just for a fan curve is not worth the battery.
                                var smi = await _gpuManager.GetNvmlTelemetryAsync(_engineCts.Token);
                                if (smi.CoreTemp > 0) effectiveGpuTemp = smi.CoreTemp;
                            }

                            var activeMode = GetActivePowerProfile(
                                SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online);
                            var pName = activeMode.ToString();

                            var cpuCurve = FanCurveHelper.LoadCurveFromRegistry(
                                $"CpuCurve_{pName}", FanCurveHelper.GetDefaultCpuCurve(activeMode));
                            var gpuCurve = FanCurveHelper.LoadCurveFromRegistry(
                                $"GpuCurve_{pName}", FanCurveHelper.GetDefaultGpuCurve(activeMode));

                            var targetCpuSpeed = FanSafety.Clamp(
                                FanCurveHelper.InterpolateSpeed(cpuCurve, telemetry.CpuTemp));
                            var targetGpuSpeed = effectiveGpuTemp == 0
                                ? targetCpuSpeed
                                : FanSafety.Clamp(FanCurveHelper.InterpolateSpeed(gpuCurve, effectiveGpuTemp));

                            // Rise at once, fall gradually. A burst of load that lifts the
                            // temperature for a second should not make the fans drop again the
                            // moment it ends.
                            var rampPerSecond = FanRamp.PerSecondFor(
                                SettingsManager.Get("FanRampDown", FanRamp.Balanced));
                            var now = DateTime.UtcNow;
                            var cpuSpeed = _cpuRamp.Next(
                                targetCpuSpeed, telemetry.CpuTemp,
                                rampPerSecond, FanEngineTick, FanRamp.Hold, now);
                            var gpuSpeed = _gpuRamp.Next(
                                targetGpuSpeed,
                                effectiveGpuTemp == 0 ? telemetry.CpuTemp : effectiveGpuTemp,
                                rampPerSecond, FanEngineTick, FanRamp.Hold, now);

                            if (cpuSpeed != _lastAppliedCpuSpeed || gpuSpeed != _lastAppliedGpuSpeed)
                            {
                                if (await AcerWmiManager.SetCustomFansAsync(cpuSpeed, gpuSpeed))
                                {
                                    _lastAppliedCpuSpeed = cpuSpeed;
                                    _lastAppliedGpuSpeed = gpuSpeed;
                                }
                                else
                                {
                                    // Retry on the next tick instead of treating a failed WMI write
                                    // as applied and leaving the fans stuck at the old speed.
                                    _lastAppliedCpuSpeed = _lastAppliedGpuSpeed = -1;
                                }
                            }
                        }
                        else
                        {
                            // Re-arm the next time the user enters curve mode.
                            ResetFanRamp();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Fan engine: iteration failed.", ex);
                    }

                    await timer.WaitForNextTickAsync(_engineCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        // Lighting sync: a software effect that rewrites the zones from the screen or the audio
        // meter. It lives here, not in the dashboard, because the dashboard may be closed. The
        // dashboard only writes KbSyncEnabled/KbSyncSource.
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var enabledValue = SettingsManager.Get("KbSyncEnabled", -1);
                        var enabled = enabledValue >= 0
                            ? enabledValue == 1
                            : SettingsManager.Get("KbScreenSync", 0) == 1;

                        if (enabled)
                            LightingSync.Start(SettingsManager.Get("KbSyncSource", LightingSync.SourceScreen));
                        else
                            LightingSync.Stop(restorePreset: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Lighting sync: supervisor tick failed.", ex);
                    }

                    await timer.WaitForNextTickAsync(_engineCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                LightingSync.Stop();
            }
        });

        // Tray icon and tooltip. The choice is written by the dashboard, so it is re-read on a
        // slow timer; only the temperature style touches WMI, and only while it is selected.
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    await timer.WaitForNextTickAsync(_engineCts.Token);
                    if (_shuttingDown) break;

                    try { RunOnUi(RefreshTrayIcon); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Logger.Warn("Tray icon: refresh failed.", ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        // GPU OC watchdog. This repairs the offsets after a driver reset, sleep/resume, or
        // another tool clearing P0 — the "randomly resets to 0" report.
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    await timer.WaitForNextTickAsync(_engineCts.Token);

                    try
                    {
                        var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
                        var profile = GetActivePowerProfile(isOnline);
                        if (_shuttingDown) break;

                        if (!await _gpuManager.EnsureClocksAppliedAsync(profile))
                        {
                            // A sleeping dGPU is expected on battery: the probe is skipped
                            // without waking it, so there is nothing to warn about.
                            if (!_gpuManager.IsGpuSleeping &&
                                DateTime.UtcNow - _lastOcWatchdogLog > TimeSpan.FromMinutes(5))
                            {
                                Logger.Warn($"GPU OC: watchdog could not verify/apply {profile} offsets.");
                                _lastOcWatchdogLog = DateTime.UtcNow;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (DateTime.UtcNow - _lastOcWatchdogLog > TimeSpan.FromMinutes(5))
                        {
                            Logger.Warn("GPU OC: watchdog failed.", ex);
                            _lastOcWatchdogLog = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        // App profiles: WMI process start/stop events, with a polling fallback only when WMI
        // refuses the subscription. No periodic process scan in the normal path.
        StartAppProfileEngine();
    }

    private void CleanupResources(bool killUiProcesses)
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;

        _shuttingDown = true;
        _engineCts.Cancel();
        LightingSync.Stop();
        FirmwareEvents.Stop();
        _nitroHook.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerStateChanged;

        // Close the dashboard before restoring the CPU. The dashboard is a separate process
        // and could otherwise write its managed CPU values again after our restore. Never do
        // this for an update: the updater has already started the replacement Nitrous process,
        // and killing every Nitrous process here would kill it before it can take over.
        if (killUiProcesses)
            KillOtherInstances();

        // Wait for an in-flight hardware apply so it cannot re-apply CPU limits after the
        // restore below. Then restore the exact original processor state synchronously.
        var gateAcquired = _powerGate.Wait(TimeSpan.FromSeconds(10));
        if (!gateAcquired)
            Logger.Warn("Shutdown: timed out waiting for an in-flight hardware apply.");

        try
        {
            CpuPowerManager.RestoreOriginalSynchronously();
        }
        catch (Exception ex)
        {
            Logger.Warn("Shutdown: CPU power restore failed.", ex);
        }
        finally
        {
            if (gateAcquired) _powerGate.Release();
        }

        trayIcon.Visible = false;
        SafeDispose(trayIcon, "tray icon");
        SafeDispose(_generatedIcon, "generated tray icon");

        lock (_dashboardGate)
        {
            _dashboardProcess?.Dispose();
            _dashboardProcess = null;
        }

        SafeDispose(_hotkeys, "hotkey registration");
        SafeDispose(_hotkeyChanged, "hotkey change event");
        SafeDispose(_appearanceChanged, "appearance change event");
        SafeDispose(_appProfileChanged, "app profile change event");
        SafeDispose(_processWatcher, "process event watcher");
        SafeDispose(_osd, "on-screen display");
        SafeDispose(_uiInvoker, "UI invoker");
        SafeDispose(_gpuManager, "GPU manager");
    }

    /// <summary>
    /// Disposes one shutdown resource, and never lets a failure escape. After the updater has
    /// renamed the running bundle, anything this process had not loaded yet can no longer be
    /// resolved - which is exactly how shutdown used to die on GPU clock management being off.
    /// </summary>
    private static void SafeDispose(IDisposable? resource, string what)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Shutdown: could not dispose the {what}.", ex);
        }
    }

    internal static void KillOtherInstances()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var pName = current.ProcessName;
            var currentId = current.Id;
            current.Dispose();

            foreach (var p in Process.GetProcessesByName(pName))
            {
                try
                {
                    if (p.Id != currentId) p.Kill();
                }
                finally
                {
                    p.Dispose();
                }
            }

            // Give a killed dashboard a moment to release its named powercfg mutex before the
            // restore below tries to acquire it.
            Thread.Sleep(150);
        }
        catch { }
    }

    private void Restart(object? sender, EventArgs e)
    {
        try
        {
            CleanupResources(killUiProcesses: true);
        }
        catch (Exception ex)
        {
            // Same reasoning as ExitCore: a failed teardown must not cancel the restart.
            Logger.Warn("Restart: cleanup failed; starting the new instance anyway.", ex);
        }

        try
        {
            // The old process still owns the single-instance mutex until it exits. Start the new
            // instance with --updated so it waits for us instead of silently quitting.
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--updated") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Never exit into nothing: if the replacement cannot start, keep this instance alive.
            Logger.Error("Restart: could not start the new instance; staying open.", ex);
            return;
        }

        Application.Exit();
    }

    private void Exit(object? sender, EventArgs e) => ExitCore(killUiProcesses: true);

    private void ExitCore(bool killUiProcesses)
    {
        try
        {
            CleanupResources(killUiProcesses);
        }
        catch (Exception ex)
        {
            // Exiting matters more than a tidy teardown. This process holds the single-instance
            // mutex, and after an update the replacement waits on it: staying alive is what left
            // Nitrous sitting in Task Manager with no tray icon.
            Logger.Error("Shutdown: cleanup failed; exiting anyway.", ex);
        }
        finally
        {
            Application.Exit();
        }
    }
}
