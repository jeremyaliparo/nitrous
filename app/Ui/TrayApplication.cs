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
    private DateTime _lastOcWatchdogLog = DateTime.MinValue;

    private readonly GlobalHotkeyManager _hotkeys = new();
    private readonly EventWaitHandle _hotkeyChanged = HotkeyChangeSignal.CreateReceiver();
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
    private const int HK_DASHBOARD = 99;

    public TrayApplication()
    {
        // Logger is initialized once in Program.Main, which also records the tray/dashboard role.

        // Force creation of the marshaling handle on the tray/UI thread.
        _ = _uiInvoker.Handle;

        Icon appIcon = SystemIcons.Shield;
        try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield; } catch { }

        trayIcon = new NotifyIcon { Icon = appIcon, Visible = true, Text = "Nitrous" };
        trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(); };

        BuildContextMenu();

        SystemEvents.PowerModeChanged += OnPowerStateChanged;

        _ = Task.Run(() => UpdateManager.CheckForUpdatesAsync(true, ExitFromAnyThread));

        _nitroHook = new NitroKeyHook();
        _nitroHook.NitroKeyPressed += (s, e) => ShowDashboard();

        _hotkeys.HotkeyPressed += OnCustomHotkeyPressed;
        ReloadHotkeys();

        _ = Task.Run(async () =>
        {
            await Task.Delay(8000);
            if (_shuttingDown) return;

            await ApplyPowerSettingsAsync(isStartup: true, force: true);
            if (_shuttingDown) return;

            var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            await _gpuManager.ApplyOnBootAsync(GetActivePowerProfile(isOnline));

            // Build the overlay cards while the machine is idle. This keeps the first hotkey
            // instant without holding GDI+ surfaces for users who never switch profiles.
            if (_shuttingDown) return;

            // The OSD is only opened by the cycle hotkey. Without one there is nothing to
            // pre-warm, so skip the extra window handle and surfaces entirely.
            if (string.IsNullOrEmpty(SettingsManager.Get("Hotkey_CyclePower", ""))) return;

            RunOnUi(() => EnsureOsd()?.Prewarm());
        });

        StartBackgroundEngine();
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false };
        menu.Items.Add("Open Nitrous", null, (s, e) => ShowDashboard());
        menu.Items.Add("Reset Dashboard Position", null, (s, e) => ResetDashboardPosition());
        menu.Items.Add("Check for Updates...", null, async (s, e) => await UpdateManager.CheckForUpdatesAsync(false, ExitFromAnyThread));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart", null, Restart);
        menu.Items.Add("Exit", null, Exit);
        trayIcon.ContextMenuStrip = menu;
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

            _lastAppliedCpuSpeed = _lastAppliedGpuSpeed = -1;
            await ApplyPowerSettingsAsync(isStartup: false, force: true);
            if (_shuttingDown) return;

            var isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            await _gpuManager.ApplyOnBootAsync(GetActivePowerProfile(isOnline));
        }
    }

    private static PowerProfile GetActivePowerProfile(bool isOnline)
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

    private static FanProfile GetActiveFan(bool isOnline)
    {
        if (SettingsManager.Get("AutoSwitch", 0) == 1)
        {
            var key = isOnline ? "LastAcFanMode" : "LastDcFanMode";
            return Enum.TryParse(SettingsManager.Get(key, "Auto"), out FanProfile f) ? f : FanProfile.Auto;
        }

        return Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile f2) ? f2 : FanProfile.Auto;
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

            var refreshMode = (RefreshProfile)SettingsManager.Get("RefreshMode", (int)RefreshProfile.Auto);
            DisplayManager.ApplyRefreshProfile(refreshMode, isOnline);

            // Charge limit is a hardware latch; re-assert it on startup/resume so it survives
            // an EC reset. It is cheap and harmless on AC/DC transitions, but we avoid churn.
            if (isStartup || force)
            {
                var chargeLimit = SettingsManager.Get("ChargeLimit", 0) == 1;
                await AcerWmiManager.SetChargeLimitAsync(chargeLimit);
            }

            await _gpuManager.ApplyPowerProfileOcAsync(
                profile,
                allowStockReset: true,
                source: isStartup ? "startup-apply" : "acdc");
            _lastAppliedCpuSpeed = _lastAppliedGpuSpeed = -1;
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

    private void ReloadHotkeys()
    {
        if (_shuttingDown) return;

        _hotkeys.UnregisterAll(HK_CYCLE_POWER, HK_DASHBOARD);

        RegisterSavedHotkey("Hotkey_CyclePower", HK_CYCLE_POWER);
        RegisterSavedHotkey("Hotkey_Dashboard", HK_DASHBOARD);

        _registeredCycleHotkey = SettingsManager.Get("Hotkey_CyclePower", "");
        _registeredDashboardHotkey = SettingsManager.Get("Hotkey_Dashboard", "");
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
    }

    private void OnCustomHotkeyPressed(int id)
    {
        switch (id)
        {
            case HK_CYCLE_POWER:
                _ = CyclePowerModeAsync();
                break;
            case HK_DASHBOARD:
                ShowDashboard();
                break;
        }
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
                _lastAppliedCpuSpeed = _lastAppliedGpuSpeed = -1;
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
    }

    private void ShowProfileOsd(PowerProfile profile)
    {
        if (_shuttingDown) return;

        var color = profile switch
        {
            PowerProfile.Quiet => Color.FromArgb(52, 199, 89),
            PowerProfile.Balanced => Color.FromArgb(10, 132, 255),
            PowerProfile.Performance => Color.FromArgb(255, 159, 10),
            PowerProfile.Turbo => Color.FromArgb(255, 69, 58),
            _ => Color.White
        };

        // The hardware awaits use ConfigureAwait(false), so this may run on a worker thread.
        // Marshal through the control whose handle was created on the UI thread; the OSD form
        // itself may not have a handle yet on its first show.
        RunOnUi(() => EnsureOsd()?.ShowProfile(color, profile));
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

        // Fan curve engine.
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while (!_engineCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // The dashboard is a separate process. Pick up hotkey edits without
                        // requiring a restart of the background engine.
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

                            if (targetCpuSpeed != _lastAppliedCpuSpeed || targetGpuSpeed != _lastAppliedGpuSpeed)
                            {
                                if (await AcerWmiManager.SetCustomFansAsync(targetCpuSpeed, targetGpuSpeed))
                                {
                                    _lastAppliedCpuSpeed = targetCpuSpeed;
                                    _lastAppliedGpuSpeed = targetGpuSpeed;
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
                            _lastAppliedCpuSpeed = _lastAppliedGpuSpeed = -1;
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
    }

    private void CleanupResources(bool killUiProcesses)
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;

        _shuttingDown = true;
        _engineCts.Cancel();
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
        trayIcon.Dispose();

        lock (_dashboardGate)
        {
            _dashboardProcess?.Dispose();
            _dashboardProcess = null;
        }

        _hotkeys.Dispose();
        _hotkeyChanged.Dispose();
        _osd?.Dispose();
        _uiInvoker.Dispose();
        _gpuManager.Dispose();
    }

    private static void KillOtherInstances()
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
        CleanupResources(killUiProcesses: true);

        // The old process still owns the single-instance mutex until it exits. Start the new
        // instance with --updated so it waits for us instead of silently quitting.
        Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--updated") { UseShellExecute = true });
        Application.Exit();
    }

    private void Exit(object? sender, EventArgs e) => ExitCore(killUiProcesses: true);

    private void ExitCore(bool killUiProcesses)
    {
        CleanupResources(killUiProcesses);
        Application.Exit();
    }
}
