using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;
using Nitrous.Managers;
using Nitrous.Mvvm;

using MessageBox = System.Windows.MessageBox;

namespace Nitrous.Ui;

public class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly ActionDebouncer _fanDebouncer = new();
    private readonly NvidiaGpuManager _gpuManager = new();
    private CancellationTokenSource? _pollingCts;
    private volatile bool _telemetryEnabled = true;
    private volatile bool _gpuPageActive;
    private bool _telemetryErrorLogged;
    private int _deepTelemetryFailures;
    private bool _isApplyingClocks;
    private bool _disposed;

    private bool _loadingBoostModes;
    private bool _boostRestoreInProgress;
    private IReadOnlyList<CpuPowerManager.BoostModeOption> _boostModes =
        Array.Empty<CpuPowerManager.BoostModeOption>();
    private CpuPowerManager.BoostModeOption? _boostAcMode;
    private CpuPowerManager.BoostModeOption? _boostDcMode;
    private bool _isBoostSupported;
    private string _boostStatusText = "Reading supported Windows boost modes...";

    private bool _acerServiceRefreshInProgress;
    private bool _acerServiceOperationInProgress;
    private string _acerServicesStatusText = "Scanning Acer services...";
    private string _acerServicesDetailText = "";
    private bool _canDisableAcerServices;
    private bool _canRestoreAcerServices;

    private string _powerPillWattsText = "";
    public string PowerPillWattsText
    {
        get => _powerPillWattsText;
        set => SetProperty(ref _powerPillWattsText, value);
    }

    private string _cpuTempText = "--°C";
    public string CpuTempText { get => _cpuTempText; set => SetProperty(ref _cpuTempText, value); }

    private string _cpuTempColor = "White";
    public string CpuTempColor { get => _cpuTempColor; set => SetProperty(ref _cpuTempColor, value); }

    private string _cpuRpmText = "-- RPM";
    public string CpuRpmText { get => _cpuRpmText; set => SetProperty(ref _cpuRpmText, value); }

    private string _gpuTempText = "--°C";
    public string GpuTempText { get => _gpuTempText; set => SetProperty(ref _gpuTempText, value); }

    private string _gpuRpmText = "-- RPM";
    public string GpuRpmText { get => _gpuRpmText; set => SetProperty(ref _gpuRpmText, value); }

    private string _gpuTempColor = "White";
    public string GpuTempColor { get => _gpuTempColor; set => SetProperty(ref _gpuTempColor, value); }

    private string _applyBtnText = "APPLY";
    public string ApplyBtnText { get => _applyBtnText; set => SetProperty(ref _applyBtnText, value); }

    private string _applyBtnColor = "#a855f7";
    public string ApplyBtnColor { get => _applyBtnColor; set => SetProperty(ref _applyBtnColor, value); }

    private int _gpuCoreOffset;
    public int GpuCoreOffset
    {
        get => _gpuCoreOffset;
        set
        {
            value = Math.Clamp(value, NvidiaGpuManager.MinCoreOffset, NvidiaGpuManager.MaxCoreOffset);
            if (!SetProperty(ref _gpuCoreOffset, value)) return;
            OnPropertyChanged(nameof(GpuCoreOffsetText));
        }
    }

    /// <summary>Signed display text for the core offset; positive values get a "+" prefix.</summary>
    public string GpuCoreOffsetText
    {
        get => FormatOffset(_gpuCoreOffset);
        set
        {
            if (TryParseOffset(value, NvidiaGpuManager.MaxCoreOffset, out var offset))
                GpuCoreOffset = offset;
        }
    }

    private int _gpuMemoryOffset;
    public int GpuMemoryOffset
    {
        get => _gpuMemoryOffset;
        set
        {
            value = Math.Clamp(value, NvidiaGpuManager.MinMemoryOffset, NvidiaGpuManager.MaxMemoryOffset);
            if (!SetProperty(ref _gpuMemoryOffset, value)) return;
            OnPropertyChanged(nameof(GpuMemoryOffsetText));
        }
    }

    /// <summary>Signed display text for the memory offset; positive values get a "+" prefix.</summary>
    public string GpuMemoryOffsetText
    {
        get => FormatOffset(_gpuMemoryOffset);
        set
        {
            if (TryParseOffset(value, NvidiaGpuManager.MaxMemoryOffset, out var offset))
                GpuMemoryOffset = offset;
        }
    }

    private static string FormatOffset(int value) => value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseOffset(string? text, int maximum, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("+", StringComparison.Ordinal))
            trimmed = trimmed[1..];

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;

        value = Math.Clamp(parsed, 0, maximum);
        return true;
    }

    private void SetGpuOffsets(int core, int memory)
    {
        // Never show a value the sliders and the driver validation cannot represent.
        GpuCoreOffset = core;
        GpuMemoryOffset = memory;
    }

    private bool _manageGpuClocks;
    /// <summary>
    /// Explicit ownership switch for GPU core/memory clock writes. Off means read-only:
    /// every write path is gated inside NvidiaGpuManager, not just here.
    /// </summary>
    public bool ManageGpuClocks
    {
        get => _manageGpuClocks;
        set
        {
            if (_manageGpuClocks == value) return;

            if (value)
            {
                if (!ConfirmEnableGpuClockManagement())
                {
                    OnPropertyChanged(nameof(ManageGpuClocks));
                    return;
                }

                // Save before applying so the tray watchdog (a separate process) sees the
                // new ownership immediately.
                SettingsManager.Save(NvidiaGpuManager.ManageGpuClocksKey, 1);
                _manageGpuClocks = true;
                GpuClockStatusText = "Nitrous manages GPU clocks. Applying the active profile offsets...";
                OnPropertyChanged(nameof(ManageGpuClocks));
                _ = ApplyGpuClockManagementAsync();
                return;
            }

            var clearOffsets = false;
            if (_gpuManager.TryGetAppliedOffsets(out var appliedCore, out var appliedMemory))
            {
                var choice = MessageBox.Show(
                    $"Nitrous recorded these GPU offsets from its last write:\n\n" +
                    $"Core: {appliedCore:+#;-#;0} MHz\n" +
                    $"Memory: {appliedMemory:+#;-#;0} MHz\n\n" +
                    "Clear them back to 0/0 now?\n\n" +
                    "Yes: write 0/0 once (only if the GPU still shows these values)\n" +
                    "No: leave them as they are\n" +
                    "Cancel: keep GPU clock management on",
                    "Nitrous GPU clocks",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (choice == MessageBoxResult.Cancel)
                {
                    OnPropertyChanged(nameof(ManageGpuClocks));
                    return;
                }

                clearOffsets = choice == MessageBoxResult.Yes;
            }

            // Persist first so the tray watchdog stops writing immediately. Turning the
            // switch off is otherwise read-only.
            SettingsManager.Save(NvidiaGpuManager.ManageGpuClocksKey, 0);
            _manageGpuClocks = false;
            OnPropertyChanged(nameof(ManageGpuClocks));

            if (clearOffsets)
            {
                var cleared = _gpuManager.TryClearAppliedOffsets(out var message);
                Logger.Info($"GPU OC: disable cleanup: {message}");
                GpuClockStatusText = cleared
                    ? "GPU clock management is off. Nitrous cleared its offsets to 0/0."
                    : $"GPU clock management is off. {message}";
            }
            else
            {
                GpuClockStatusText =
                    "GPU clock management is off. Nitrous will not write GPU clocks. " +
                    "Current offsets are left as they are.";
            }

            RefreshGpuOffsets();
        }
    }

    private string _gpuClockStatusText = "";
    public string GpuClockStatusText
    {
        get => _gpuClockStatusText;
        private set => SetProperty(ref _gpuClockStatusText, value);
    }

    private string _gpuNameText = "NVIDIA GPU";
    public string GpuNameText { get => _gpuNameText; set => SetProperty(ref _gpuNameText, value); }

    private string _gpuArchText = "--";
    public string GpuArchText { get => _gpuArchText; set => SetProperty(ref _gpuArchText, value); }

    private string _gpuLoadText = "0%";
    public string GpuLoadText { get => _gpuLoadText; set => SetProperty(ref _gpuLoadText, value); }

    private string _gpuVramText = "0 / 0 MB";
    public string GpuVramText { get => _gpuVramText; set => SetProperty(ref _gpuVramText, value); }

    private string _gpuDeepTempText = "0 C";
    public string GpuDeepTempText { get => _gpuDeepTempText; set => SetProperty(ref _gpuDeepTempText, value); }

    private string _gpuPStateText = "P0";
    public string GpuPStateText { get => _gpuPStateText; set => SetProperty(ref _gpuPStateText, value); }

    private string _gpuLoadColor = "#B388FF";
    public string GpuLoadColor { get => _gpuLoadColor; set => SetProperty(ref _gpuLoadColor, value); }

    private string _gpuDeepTempColor = "#B388FF";
    public string GpuDeepTempColor { get => _gpuDeepTempColor; set => SetProperty(ref _gpuDeepTempColor, value); }

    private string _gpuCoreClockText = "0 MHz";
    public string GpuCoreClockText { get => _gpuCoreClockText; set => SetProperty(ref _gpuCoreClockText, value); }

    private string _gpuMemClockText = "0 MHz";
    public string GpuMemClockText { get => _gpuMemClockText; set => SetProperty(ref _gpuMemClockText, value); }

    private string _gpuPowerText = "0.0 W";
    public string GpuPowerText { get => _gpuPowerText; set => SetProperty(ref _gpuPowerText, value); }

    private bool _isTurboSupported = true;
    public bool IsTurboSupported { get => _isTurboSupported; set => SetProperty(ref _isTurboSupported, value); }

    private PowerProfile _activePowerProfile;
    public PowerProfile ActivePowerProfile
    {
        get => _activePowerProfile;
        set => SetProperty(ref _activePowerProfile, value);
    }

    private bool _isCurveModeEnabled;
    public bool IsCurveModeEnabled
    {
        get => _isCurveModeEnabled;
        set
        {
            if (SetProperty(ref _isCurveModeEnabled, value))
            {
                SettingsManager.Save("IsCurveModeEnabled", value ? 1 : 0);
                OnPropertyChanged(nameof(IsManualSliderEnabled));

                // Leaving curve mode should immediately hand control back to the manual sliders.
                if (!value && IsCustomFanEnabled)
                    _ = AcerWmiManager.SetCustomFansAsync(CpuFanSpeed, GpuFanSpeed);
            }
        }
    }

    private bool _manageCpuPower;
    public bool ManageCpuPower
    {
        get => _manageCpuPower;
        set
        {
            if (!SetProperty(ref _manageCpuPower, value)) return;

            SettingsManager.Save("ManageCpuPower", value ? 1 : 0);
            var isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                           System.Windows.Forms.PowerLineStatus.Online;

            if (!value)
            {
                // Restore synchronously: the user may close the dashboard immediately after
                // turning the feature off, and a background task would die with the process
                // while the CPU limits are still applied.
                try
                {
                    CpuPowerManager.RestoreOriginalSynchronously();
                }
                catch (Exception ex)
                {
                    Logger.Warn("CPU power: toggle restore failed.", ex);
                }

                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await CpuPowerManager.ApplyProfileLimitsAsync(ActivePowerProfile, isOnline);
                }
                catch (Exception ex)
                {
                    Logger.Warn("CPU power: toggle failed.", ex);
                }
            });
        }
    }

    public IReadOnlyList<CpuPowerManager.BoostModeOption> BoostModes
    {
        get => _boostModes;
        private set => SetProperty(ref _boostModes, value);
    }

    public CpuPowerManager.BoostModeOption? BoostAcMode
    {
        get => _boostAcMode;
        set
        {
            if (!SetProperty(ref _boostAcMode, value)) return;
            if (_loadingBoostModes || value == null) return;
            _ = ApplyBoostModeAsync(isOnline: true, value.Value);
        }
    }

    public CpuPowerManager.BoostModeOption? BoostDcMode
    {
        get => _boostDcMode;
        set
        {
            if (!SetProperty(ref _boostDcMode, value)) return;
            if (_loadingBoostModes || value == null) return;
            _ = ApplyBoostModeAsync(isOnline: false, value.Value);
        }
    }

    public bool IsBoostSupported
    {
        get => _isBoostSupported;
        private set => SetProperty(ref _isBoostSupported, value);
    }

    public string BoostStatusText
    {
        get => _boostStatusText;
        private set => SetProperty(ref _boostStatusText, value);
    }

    public string AcerServicesStatusText
    {
        get => _acerServicesStatusText;
        private set => SetProperty(ref _acerServicesStatusText, value);
    }

    public string AcerServicesDetailText
    {
        get => _acerServicesDetailText;
        private set => SetProperty(ref _acerServicesDetailText, value);
    }

    public bool CanDisableAcerServices
    {
        get => _canDisableAcerServices;
        private set => SetProperty(ref _canDisableAcerServices, value);
    }

    public bool CanRestoreAcerServices
    {
        get => _canRestoreAcerServices;
        private set => SetProperty(ref _canRestoreAcerServices, value);
    }

    public bool IsManualSliderEnabled => IsCustomFanEnabled && !IsCurveModeEnabled;

    public DashboardViewModel()
    {
        // Initialize fan state. Old versions could persist 0/1%, so repair it on load.
        var savedCpu = SettingsManager.Get("CustomFanSpeedCpu", 50);
        var savedGpu = SettingsManager.Get("CustomFanSpeedGpu", 50);
        _cpuFanSpeed = FanSafety.Clamp(savedCpu);
        _gpuFanSpeed = FanSafety.Clamp(savedGpu);
        if (_cpuFanSpeed != savedCpu) SettingsManager.Save("CustomFanSpeedCpu", _cpuFanSpeed);
        if (_gpuFanSpeed != savedGpu) SettingsManager.Save("CustomFanSpeedGpu", _gpuFanSpeed);

        _isUnifiedFans = SettingsManager.Get("UnifiedFans", 1) == 1;
        _isCurveModeEnabled = SettingsManager.Get("IsCurveModeEnabled", 0) == 1;
        _manageCpuPower = SettingsManager.Get("ManageCpuPower", 0) == 1;

        // GPU clock writes are opt-in. Default off for existing installs so an update never
        // starts writing clocks on a machine that has been tuned by another tool.
        _manageGpuClocks = NvidiaGpuManager.IsClockManagementEnabled();
        _gpuClockStatusText = _manageGpuClocks
            ? "Nitrous manages GPU clocks. Per-profile offsets apply at startup, profile switches, AC/DC, resume, and the 30-second watchdog."
            : "Nitrous is not managing GPU clocks. Offsets below are read from the driver and are never written. " +
              "Enable the switch to apply per-profile core and memory offsets.";

        _deepGpuTelemetry = SettingsManager.Get("DeepGpuTelemetry", 1) == 1;
        if (!_deepGpuTelemetry) ClearDeepTelemetryUI();

        var activeFan = Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile f)
            ? f
            : FanProfile.Auto;
        IsCustomFanEnabled = activeFan == FanProfile.Medium;

        // Initialize refresh-rate label.
        MaxRefreshText = $"{DisplayManager.GetPrimaryMaxRefreshRate()}Hz";

        // Initialize settings state.
        _chargeLimit = SettingsManager.Get("ChargeLimit", 0) == 1;
        _autoSwitch = SettingsManager.Get("AutoSwitch", 0) == 1;
        _refreshAutoSwitch = SettingsManager.Get("RefreshAutoSwitch", 0) == 1;

        ActivePowerProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);

        Task.Run(() =>
        {
            var isTurbo = AcerWmiManager.IsTurboModeSupported();
            var isTaskEnabled = StartupManager.CheckStartupTask();
            var hasClocks = _gpuManager.TryGetClocks(out var core, out var memory, out var clockError);
            if (!hasClocks && !string.IsNullOrEmpty(clockError))
                Logger.Info($"GPU OC: initial clock read failed: {clockError}");

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                IsTurboSupported = isTurbo;

                _runOnStartup = isTaskEnabled;
                OnPropertyChanged(nameof(RunOnStartup));

                // Never overwrite the user's saved offsets with 0 just because the GPU was asleep.
                if (hasClocks)
                {
                    SetGpuOffsets(core, memory);
                }
            });
        });

        // Setup commands.
        SetPowerCommand = new RelayCommand(async param =>
        {
            try
            {
                if (!Enum.TryParse(param?.ToString(), out PowerProfile mode)) return;

                ActivePowerProfile = mode;

                var isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                               System.Windows.Forms.PowerLineStatus.Online;

                SettingsManager.Save("LastPowerMode", (int)mode);
                SettingsManager.Save(isOnline ? "LastAcPowerMode" : "LastDcPowerMode", (int)mode);

                await AcerWmiManager.SetPowerModeAsync(mode);

                if (ManageCpuPower)
                    await CpuPowerManager.ApplyProfileLimitsAsync(mode, isOnline);

                await _gpuManager.ApplyPowerProfileOcAsync(mode, allowStockReset: true, source: "profile-click");

                if (_gpuManager.TryGetClocks(out var c, out var m, out var error))
                {
                    SetGpuOffsets(c, m);
                }
                else
                {
                    Logger.Info($"GPU OC: could not read clocks after switching to {mode}: {error}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Power: failed to switch profile.", ex);
            }
        });

        SetFanCommand = new RelayCommand(async param =>
        {
            try
            {
                if (!Enum.TryParse(param?.ToString(), out FanProfile mode)) return;

                IsCustomFanEnabled = mode == FanProfile.Medium;

                // Persist the choice before the WMI calls: the dashboard is a separate process
                // and may be closed while the async hardware write is still in flight.
                SettingsManager.Save("LastFanMode", mode.ToString());
                var isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                               System.Windows.Forms.PowerLineStatus.Online;
                SettingsManager.Save(isOnline ? "LastAcFanMode" : "LastDcFanMode", mode.ToString());

                if (mode == FanProfile.Medium)
                    await AcerWmiManager.SetCustomFansAsync(CpuFanSpeed, GpuFanSpeed);
                else
                    await AcerWmiManager.SetFansAsync(mode);
            }
            catch (Exception ex)
            {
                Logger.Warn("Fan: failed to switch mode.", ex);
            }
        });

        SetRefreshCommand = new RelayCommand(param =>
        {
            try
            {
                if (!Enum.TryParse(param?.ToString(), out RefreshProfile profile)) return;

                SettingsManager.Save("RefreshMode", (int)profile);
                var isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                               System.Windows.Forms.PowerLineStatus.Online;
                DisplayManager.ApplyRefreshProfile(profile, isOnline);
            }
            catch (Exception ex)
            {
                Logger.Warn("Display: failed to switch refresh profile.", ex);
            }
        });

        ApplyGpuClocksCommand = new RelayCommand(async _ =>
        {
            if (_isApplyingClocks) return;

            _isApplyingClocks = true;
            try
            {
                var currentProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);

                _gpuManager.SaveCustomProfileOc(currentProfile, GpuCoreOffset, GpuMemoryOffset);
                var applied = _gpuManager.TrySetClocks(
                    GpuCoreOffset, GpuMemoryOffset, out var error, "apply");

                if (applied)
                {
                    ApplyBtnText = "APPLIED!";
                    ApplyBtnColor = "#34C759";
                    GpuClockStatusText =
                        $"Applied {FormatOffset(GpuCoreOffset)} / {FormatOffset(GpuMemoryOffset)} MHz to {currentProfile}.";
                }
                else
                {
                    ApplyBtnText = "ERROR";
                    ApplyBtnColor = "#FF453A";
                    GpuClockStatusText = _gpuManager.IsGpuSleeping
                        ? "The GPU is asleep. Nitrous saved the offsets but did not wake it to write them."
                        : $"Could not apply GPU clocks: {error}";
                    Logger.Warn($"GPU OC: apply failed: {error}");
                }

                await Task.Delay(2000);
                ApplyBtnText = "APPLY";
                ApplyBtnColor = "#a855f7";
            }
            catch (Exception ex)
            {
                Logger.Warn("GPU OC: apply command failed.", ex);
            }
            finally
            {
                _isApplyingClocks = false;
            }
        });

        ResetGpuClocksCommand = new RelayCommand(async _ =>
        {
            try
            {
                var currentProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
                await _gpuManager.ResetProfileToDefaultsAsync(currentProfile, "reset");

                if (_gpuManager.TryGetClocks(out var c, out var m, out var error))
                {
                    SetGpuOffsets(c, m);
                    GpuClockStatusText = $"Reset {currentProfile} to 0 / 0 MHz (stock).";
                }
                else
                {
                    GpuClockStatusText = _gpuManager.IsGpuSleeping
                        ? "The GPU is asleep. Reset saved 0 / 0; Nitrous will write it after the GPU wakes."
                        : $"Reset saved, but the clocks could not be read back: {error}";
                    Logger.Info($"GPU OC: could not read clocks after reset: {error}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("GPU OC: reset failed.", ex);
            }
        });

        RestoreBoostCommand = new RelayCommand(async _ => await RestoreBoostAsync());
        DisableAcerServicesCommand = new RelayCommand(async _ => await DisableAcerServicesAsync());
        RestoreAcerServicesCommand = new RelayCommand(async _ => await RestoreAcerServicesAsync());

        _ = LoadBoostModesAsync();
        _ = RefreshAcerServicesAsync();

        StartTelemetryPolling();
    }

    public string MaxRefreshText { get; }

    // --- FAN PROPERTIES & LOGIC ---
    private int _cpuFanSpeed;
    public int CpuFanSpeed
    {
        get => _cpuFanSpeed;
        set
        {
            value = FanSafety.Clamp(value);
            if (!SetProperty(ref _cpuFanSpeed, value)) return;

            if (IsUnifiedFans) GpuFanSpeed = value;
            TriggerFanSave();
        }
    }

    private int _gpuFanSpeed;
    public int GpuFanSpeed
    {
        get => _gpuFanSpeed;
        set
        {
            value = FanSafety.Clamp(value);
            if (!SetProperty(ref _gpuFanSpeed, value)) return;

            if (IsUnifiedFans) CpuFanSpeed = value;
            TriggerFanSave();
        }
    }

    private bool _isUnifiedFans;
    public bool IsUnifiedFans
    {
        get => _isUnifiedFans;
        set
        {
            if (SetProperty(ref _isUnifiedFans, value))
            {
                SettingsManager.Save("UnifiedFans", value ? 1 : 0);
                if (value) GpuFanSpeed = CpuFanSpeed;
            }
        }
    }

    private bool _isCustomFanEnabled;
    public bool IsCustomFanEnabled
    {
        get => _isCustomFanEnabled;
        set
        {
            if (!SetProperty(ref _isCustomFanEnabled, value)) return;

            OnPropertyChanged(nameof(CustomFanOpacity));
            OnPropertyChanged(nameof(IsManualSliderEnabled));
        }
    }

    public double CustomFanOpacity => IsCustomFanEnabled ? 1.0 : 0.4;

    private void TriggerFanSave()
    {
        if (!IsCustomFanEnabled) return;

        _fanDebouncer.Debounce(250, () =>
        {
            var cpu = FanSafety.Clamp(CpuFanSpeed);
            var gpu = FanSafety.Clamp(GpuFanSpeed);
            SettingsManager.Save("CustomFanSpeedCpu", cpu);
            SettingsManager.Save("CustomFanSpeedGpu", gpu);
            _ = AcerWmiManager.SetCustomFansAsync(cpu, gpu);
        });
    }

    // --- SETTINGS PROPERTIES & LOGIC ---
    private bool _chargeLimit;
    public bool ChargeLimit
    {
        get => _chargeLimit;
        set
        {
            if (!SetProperty(ref _chargeLimit, value)) return;

            SettingsManager.Save("ChargeLimit", value ? 1 : 0);
            _ = AcerWmiManager.SetChargeLimitAsync(value);
        }
    }

    private bool _autoSwitch;
    public bool AutoSwitch
    {
        get => _autoSwitch;
        set
        {
            if (SetProperty(ref _autoSwitch, value))
                SettingsManager.Save("AutoSwitch", value ? 1 : 0);
        }
    }

    private bool _refreshAutoSwitch;
    public bool RefreshAutoSwitch
    {
        get => _refreshAutoSwitch;
        set
        {
            if (SetProperty(ref _refreshAutoSwitch, value))
                SettingsManager.Save("RefreshAutoSwitch", value ? 1 : 0);
        }
    }

    private bool _deepGpuTelemetry;
    public bool DeepGpuTelemetry
    {
        get => _deepGpuTelemetry;
        set
        {
            if (!SetProperty(ref _deepGpuTelemetry, value)) return;

            SettingsManager.Save("DeepGpuTelemetry", value ? 1 : 0);
            if (!value) ClearDeepTelemetryUI();
        }
    }

    private void ClearDeepTelemetryUI()
    {
        GpuNameText = "NVIDIA GPU (NO DATA)";
        GpuArchText = "--";
        GpuLoadText = "-- %";
        GpuLoadColor = "#888890";
        GpuVramText = "-- / -- MB";
        GpuDeepTempText = "-- C";
        GpuDeepTempColor = "#888890";
        GpuPStateText = "--";
        GpuCoreClockText = "-- MHz";
        GpuMemClockText = "-- MHz";
        GpuPowerText = "-- W";
    }

    private bool _runOnStartup;
    public bool RunOnStartup
    {
        get => _runOnStartup;
        set
        {
            if (!SetProperty(ref _runOnStartup, value)) return;

            if (StartupManager.ToggleStartupTask(value, System.Windows.Forms.Application.ExecutablePath))
                return;

            // Revert without re-entering the setter.
            _runOnStartup = !value;
            OnPropertyChanged(nameof(RunOnStartup));
            MessageBox.Show(
                $"Could not {(value ? "enable" : "disable")} the startup task. See the Nitrous log for details.",
                "Nitrous", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- COMMANDS ---
    public ICommand SetPowerCommand { get; }
    public ICommand SetFanCommand { get; }
    public ICommand SetRefreshCommand { get; }
    public ICommand ApplyGpuClocksCommand { get; }
    public ICommand ResetGpuClocksCommand { get; }
    public ICommand RestoreBoostCommand { get; }
    public ICommand DisableAcerServicesCommand { get; }
    public ICommand RestoreAcerServicesCommand { get; }

    /// <summary>Pause WMI/NVML polling while the dashboard is hidden or minimized.</summary>
    public void SetTelemetryEnabled(bool enabled)
    {
        _telemetryEnabled = enabled;
        if (enabled) _telemetryErrorLogged = false;
    }

    /// <summary>
    /// NVML telemetry is only polled while the GPU page is on screen. Reading NVML wakes the
    /// dGPU, and the rest of the dashboard does not display any of its values, so polling from
    /// another page would keep an idle GPU out of RTD3 for no visible benefit.
    /// </summary>
    public void SetGpuTelemetryActive(bool active)
    {
        if (_gpuPageActive == active) return;

        _gpuPageActive = active;
        if (active) _deepTelemetryFailures = 0;
    }

    public void StopTelemetryPolling()
    {
        try { _pollingCts?.Cancel(); } catch { }
        _pollingCts?.Dispose();
        _pollingCts = null;
    }

    public Task<Dictionary<string, object?>> GetGpuDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return _gpuManager.GetDiagnosticsSnapshotAsync(cancellationToken);
    }

    private async Task LoadBoostModesAsync()
    {
        try
        {
            var snapshot = await Task.Run(CpuPowerManager.GetBoostModeSnapshot);

            await RunOnUiAsync(() =>
            {
                _loadingBoostModes = true;
                BoostModes = snapshot.Options;
                IsBoostSupported = snapshot.Options.Count > 0 && snapshot.SchemeGuid != null;
                BoostAcMode = snapshot.Options.FirstOrDefault(option => option.Value == snapshot.AcValue);
                BoostDcMode = snapshot.Options.FirstOrDefault(option => option.Value == snapshot.DcValue);

                BoostStatusText = snapshot.Error != null
                    ? snapshot.Error
                    : snapshot.SchemeGuid == null
                        ? "Could not determine the active Windows power plan."
                        : snapshot.UsedFallback
                            ? "Windows did not expose the supported list; using the standard modes."
                            : "Applies to the active Windows power plan.";

                _loadingBoostModes = false;
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("CPU boost: could not load the mode state.", ex);
            await RunOnUiAsync(() =>
            {
                IsBoostSupported = false;
                BoostStatusText = "Could not read the Windows boost modes.";
            });
        }
    }

    private async Task ApplyBoostModeAsync(bool isOnline, int value)
    {
        try
        {
            var result = await CpuPowerManager.ApplyBoostModeAsync(isOnline, value);
            if (!result.Success)
                await LoadBoostModesAsync();

            await RunOnUiAsync(() => BoostStatusText = result.Message);
        }
        catch (Exception ex)
        {
            Logger.Warn("CPU boost: apply command failed.", ex);
            await RunOnUiAsync(() => BoostStatusText = ex.Message);
        }
    }

    private async Task RestoreBoostAsync()
    {
        if (_boostRestoreInProgress) return;
        _boostRestoreInProgress = true;

        try
        {
            await RunOnUiAsync(() => BoostStatusText = "Restoring the original boost values...");
            var result = await CpuPowerManager.RestoreBoostOriginalAsync();
            await LoadBoostModesAsync();
            await RunOnUiAsync(() => BoostStatusText = result.Message);
        }
        catch (Exception ex)
        {
            Logger.Warn("CPU boost: restore command failed.", ex);
            await RunOnUiAsync(() => BoostStatusText = ex.Message);
        }
        finally
        {
            _boostRestoreInProgress = false;
        }
    }

    private async Task RefreshAcerServicesAsync()
    {
        if (_acerServiceRefreshInProgress) return;
        _acerServiceRefreshInProgress = true;

        try
        {
            var state = await Task.Run(AcerServiceManager.GetState);
            await RunOnUiAsync(() =>
            {
                if (state.Error != null)
                {
                    AcerServicesStatusText = state.Services.Count > 0
                        ? $"{state.Services.Count} Acer service(s) detected"
                        : "Could not read Acer services.";
                    AcerServicesDetailText = state.Error;
                    CanDisableAcerServices = false;
                    CanRestoreAcerServices = false;
                    return;
                }

                AcerServicesStatusText = state.Services.Count == 0
                    ? "No Acer-related services were found."
                    : $"{state.Services.Count} Acer service(s) detected";

                var details = new List<string>
                {
                    $"{state.EnabledCount} enabled",
                    $"{state.RunningCount} running"
                };
                if (state.BackupExists)
                    details.Add("backup ready");
                else if (state.EnabledCount == 0 && state.RunningCount == 0)
                    details.Add("already disabled; no Nitrous backup to restore");

                AcerServicesDetailText = string.Join(" · ", details);

                CanDisableAcerServices = state.CanDisable;
                CanRestoreAcerServices = state.CanRestore;
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Acer services: refresh failed.", ex);
            await RunOnUiAsync(() =>
            {
                AcerServicesStatusText = "Could not read Acer services.";
                AcerServicesDetailText = ex.Message;
                CanDisableAcerServices = false;
                CanRestoreAcerServices = false;
            });
        }
        finally
        {
            _acerServiceRefreshInProgress = false;
        }
    }

    private async Task DisableAcerServicesAsync()
    {
        if (_acerServiceOperationInProgress) return;

        try
        {
            var state = await Task.Run(AcerServiceManager.GetState);
            if (state.Error != null)
            {
                await RunOnUiAsync(() =>
                {
                    AcerServicesStatusText = state.Services.Count > 0
                        ? $"{state.Services.Count} Acer service(s) detected"
                        : "Could not read Acer services.";
                    AcerServicesDetailText = state.Error;
                });
                return;
            }

            if (state.Services.Count == 0)
            {
                await RunOnUiAsync(() =>
                {
                    AcerServicesStatusText = "No Acer-related services were found.";
                    AcerServicesDetailText = "";
                });
                return;
            }

            var names = string.Join(Environment.NewLine, state.Services.Select(service =>
                $"  • {service.DisplayName} ({service.Name})"));

            if (MessageBox.Show(
                    $"Stop and disable these Acer services?\n\n{names}\n\n" +
                    "OEM hotkeys, lighting, fan tools, or Acer Care may stop working. " +
                    "Nitrous saves their original start types and can restore them later.",
                    "Disable Acer services", MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
                MessageBoxResult.Yes)
                return;

            _acerServiceOperationInProgress = true;
            await RunOnUiAsync(() =>
            {
                AcerServicesStatusText = "Stopping and disabling Acer services...";
                AcerServicesDetailText = "This can take a few seconds.";
                CanDisableAcerServices = false;
                CanRestoreAcerServices = false;
            });

            var result = await AcerServiceManager.DisableAllAsync();
            await RefreshAcerServicesAsync();
            await RunOnUiAsync(() =>
            {
                AcerServicesStatusText = result.Success
                    ? "Acer services disabled."
                    : "Acer service change incomplete.";
                AcerServicesDetailText = result.Message;
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Acer services: disable command failed.", ex);
            await RefreshAcerServicesAsync();
            await RunOnUiAsync(() =>
            {
                AcerServicesStatusText = "Could not disable Acer services.";
                AcerServicesDetailText = ex.Message;
            });
        }
        finally
        {
            _acerServiceOperationInProgress = false;
        }
    }

    private async Task RestoreAcerServicesAsync()
    {
        if (_acerServiceOperationInProgress) return;

        try
        {
            if (MessageBox.Show(
                    "Restore the Acer services to the start types and running state saved by Nitrous?",
                    "Restore Acer services", MessageBoxButton.YesNo, MessageBoxImage.Question) !=
                MessageBoxResult.Yes)
                return;

            _acerServiceOperationInProgress = true;
            await RunOnUiAsync(() =>
            {
                AcerServicesStatusText = "Restoring Acer services...";
                AcerServicesDetailText = "This can take a few seconds.";
                CanDisableAcerServices = false;
                CanRestoreAcerServices = false;
            });

            var result = await AcerServiceManager.RestoreAllAsync();
            await RefreshAcerServicesAsync();
            await RunOnUiAsync(() =>
            {
                AcerServicesStatusText = result.Success
                    ? "Acer services restored."
                    : "Acer service restore incomplete.";
                AcerServicesDetailText = result.Message;
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Acer services: restore command failed.", ex);
            await RefreshAcerServicesAsync();
            await RunOnUiAsync(() =>
            {
                AcerServicesStatusText = "Could not restore Acer services.";
                AcerServicesDetailText = ex.Message;
            });
        }
        finally
        {
            _acerServiceOperationInProgress = false;
        }
    }

    private static string FormatBatteryPower(SystemInfoManager.BatteryPowerTelemetry battery)
    {
        if (battery.PowerOnline && battery.Charging && battery.ChargeRateMw > 500)
            return $"· {Math.Round(battery.ChargeRateMw / 1000.0, MidpointRounding.AwayFromZero):0} W";

        if (!battery.PowerOnline && battery.Discharging && battery.DischargeRateMw > 500)
            return $"· {Math.Round(battery.DischargeRateMw / 1000.0, MidpointRounding.AwayFromZero):0} W";

        return "";
    }

    private async Task ApplyGpuClockManagementAsync()
    {
        try
        {
            var profile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
            await _gpuManager.ApplyPowerProfileOcAsync(profile, allowStockReset: true, source: "enable");

            var (ok, core, memory, error) = await Task.Run(() =>
            {
                var read = _gpuManager.TryGetClocks(out var c, out var m, out var readError);
                return (read, c, m, readError);
            });

            await RunOnUiAsync(() =>
            {
                if (ok)
                {
                    SetGpuOffsets(core, memory);
                    GpuClockStatusText =
                        $"Nitrous manages GPU clocks. {profile} is active at " +
                        $"{core:+#;-#;0}/{memory:+#;-#;0} MHz.";
                }
                else
                {
                    GpuClockStatusText =
                        $"GPU clock management is on, but the offsets could not be read ({error}). " +
                        "The GPU may be asleep; Nitrous will retry at the next profile change or watchdog tick.";
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("GPU OC: enabling clock management failed.", ex);
            await RunOnUiAsync(() =>
                GpuClockStatusText = $"GPU clock management is on, but applying the offsets failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-reads the live P0 offsets. Used after a profile change that happened outside this
    /// window (tray hotkey, auto-switch, AC/DC) and after disabling clock management.
    /// The read is skipped off the GPU page and while the dGPU is in D3, because both would
    /// touch NVAPI without showing the result.
    /// </summary>
    public void RefreshGpuOffsets()
    {
        if (!_gpuPageActive) return;

        _ = Task.Run(() =>
        {
            var read = _gpuManager.TryGetClocks(out var core, out var memory, out var error);
            if (!read)
            {
                Logger.Info($"GPU OC: could not read clocks for the offset display: {error}");
                if (_gpuManager.IsGpuSleeping)
                {
                    _ = RunOnUiAsync(() =>
                        GpuClockStatusText =
                            "The GPU is asleep, so there are no live clocks to show. " +
                            "GPU clock writes stay paused until it wakes.");
                }

                return;
            }

            _ = RunOnUiAsync(() =>
            {
                SetGpuOffsets(core, memory);
            });
        });
    }

    private bool ConfirmEnableGpuClockManagement()
    {
        var afterburnerRunning = IsAfterburnerRunning();
        var afterburnerStarts = !afterburnerRunning && IsAfterburnerSetToStart();

        var message = new System.Text.StringBuilder();
        message.AppendLine("Nitrous will own the GPU core and memory clock offsets.");
        message.AppendLine();
        message.AppendLine("Per-profile offsets will be written at startup, profile switches, hotkeys, AC/DC changes, resume, and every 30 seconds.");
        message.AppendLine("A profile set to 0/0 MHz returns the GPU to stock clocks.");
        message.AppendLine("Nitrous writes only P0 clock deltas. It cannot preserve a voltage/frequency curve applied by another tool.");

        if (afterburnerRunning)
        {
            message.AppendLine();
            message.AppendLine("MSI Afterburner is running right now. Two tools writing GPU clocks will fight, and your mV curve may reset.");
            message.AppendLine("Before enabling, set Afterburner's Core and Memory sliders to 0 or close Afterburner.");
        }
        else if (afterburnerStarts)
        {
            message.AppendLine();
            message.AppendLine("MSI Afterburner is set to start with Windows. If it also applies clocks, the two tools will fight and your mV curve may reset.");
        }

        message.AppendLine();
        message.Append("Enable GPU clock management?");

        return MessageBox.Show(
                   message.ToString(),
                   "Nitrous GPU clocks",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static bool IsAfterburnerRunning()
    {
        try
        {
            return Process.GetProcessesByName("MSIAfterburner").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAfterburnerSetToStart()
    {
        try
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                using var runKey = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (runKey == null) continue;

                foreach (var valueName in runKey.GetValueNames())
                {
                    var command = runKey.GetValue(valueName) as string;
                    if (command != null &&
                        command.Contains("MSIAfterburner", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Detection is advisory only; never block the toggle on a registry error.
        }

        return false;
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static string FormatGpuName(string name)
    {
        var display = name.Trim();

        if (display.StartsWith("NVIDIA GeForce ", StringComparison.OrdinalIgnoreCase))
            display = "NVIDIA " + display["NVIDIA GeForce ".Length..];
        else if (display.StartsWith("GeForce ", StringComparison.OrdinalIgnoreCase))
            display = display["GeForce ".Length..].TrimStart();

        if (display.EndsWith(" GPU", StringComparison.OrdinalIgnoreCase))
            display = display[..^" GPU".Length].TrimEnd();

        if (string.IsNullOrWhiteSpace(display))
            return "NVIDIA GPU";

        if (display.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase))
            return display;

        if (display.StartsWith("RTX ", StringComparison.OrdinalIgnoreCase) ||
            display.StartsWith("GTX ", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA " + display;

        return display;
    }

    private void StartTelemetryPolling()
    {
        StopTelemetryPolling();
        _pollingCts = new CancellationTokenSource();
        var token = _pollingCts.Token;

        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!_telemetryEnabled)
                    {
                        await timer.WaitForNextTickAsync(token);
                        continue;
                    }

                    try
                    {
                        var wmiTask = Task.Run(() => AcerWmiManager.GetSystemTelemetry(), token);
                        var batteryTask = Task.Run(() => SystemInfoManager.GetBatteryPowerTelemetry(), token);

                        Task<NvidiaGpuManager.GpuTelemetry>? smiTask = null;
                        if (DeepGpuTelemetry && _gpuPageActive)
                        {
                            smiTask = _gpuManager.GetNvmlTelemetryAsync(token);
                            await Task.WhenAll(wmiTask, batteryTask, smiTask);
                        }
                        else
                        {
                            await Task.WhenAll(wmiTask, batteryTask);
                        }

                        var telemetry = await wmiTask;
                        var battery = await batteryTask;
                        var smi = smiTask != null ? await smiTask : null;
                        var gpuAsleep = _gpuManager.IsGpuSleeping;
                        _telemetryErrorLogged = false;

                        if (smi != null && !smi.HasData && DeepGpuTelemetry)
                        {
                            if (gpuAsleep)
                            {
                                // Expected on battery: the device is in D3 and Nitrous does not
                                // wake it to read sensors. This is not a driver failure.
                                _deepTelemetryFailures = 0;
                            }
                            else if (++_deepTelemetryFailures >= 3)
                            {
                                // The device is powered but NVML still returns nothing (for
                                // example a driver whose power sensors stopped working after a
                                // resume). Stop polling and say so.
                                _deepTelemetryFailures = 0;
                                _ = RunOnUiAsync(() =>
                                {
                                    if (!DeepGpuTelemetry) return;

                                    DeepGpuTelemetry = false;
                                    GpuClockStatusText =
                                        "The GPU is powered but NVML stopped returning data. Deep GPU Telemetry " +
                                        "was turned off automatically.";
                                    Logger.Info("GPU telemetry: dGPU is awake but NVML returned no data; telemetry turned off automatically.");
                                });
                            }
                        }
                        else
                        {
                            _deepTelemetryFailures = 0;
                        }

                        _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            PowerPillWattsText = FormatBatteryPower(battery);

                            CpuTempText = telemetry.CpuTemp > 0 ? $"{telemetry.CpuTemp} C" : "-- C";
                            CpuRpmText = telemetry.CpuRpm > 0 ? $"{telemetry.CpuRpm} RPM" : "-- RPM";
                            CpuTempColor = telemetry.CpuTemp > 85 ? "#FF453A" : "White";

                            GpuTempText = telemetry.GpuTemp > 0 ? $"{telemetry.GpuTemp} C" : "-- C";
                            GpuRpmText = telemetry.GpuRpm > 0 ? $"{telemetry.GpuRpm} RPM" : "-- RPM";
                            GpuTempColor = telemetry.GpuTemp > 85 ? "#FF453A" : "White";

                            if (smi != null && !string.IsNullOrEmpty(smi.Name) && smi.Name != "Unknown")
                            {
                                GpuNameText = FormatGpuName(smi.Name);
                                GpuArchText = smi.Architecture;

                                GpuLoadText = $"{smi.GpuLoad}%";
                                GpuLoadColor = smi.GpuLoad >= 95 ? "#FF453A" : (smi.GpuLoad >= 80 ? "#FF9F0A" : "White");

                                GpuVramText = $"{smi.VramUsedMb} / {smi.VramTotalMb} MB";

                                GpuDeepTempText = $"{smi.CoreTemp} C";
                                GpuDeepTempColor = smi.CoreTemp >= 85 ? "#FF453A" : (smi.CoreTemp >= 78 ? "#FF9F0A" : "White");

                                GpuPStateText = smi.PState;
                                GpuCoreClockText = $"{smi.CurrentCoreClock} MHz";
                                GpuMemClockText = $"{smi.CurrentMemoryClock} MHz";

                                if (smi.EnforcedPowerLimitW > 0 && smi.MaxPowerLimitW > 0)
                                    GpuPowerText = $"{smi.PowerDrawW:0.0} / {smi.EnforcedPowerLimitW:0} / {smi.MaxPowerLimitW:0} W";
                                else if (smi.EnforcedPowerLimitW > 0)
                                    GpuPowerText = $"{smi.PowerDrawW:0.0} / {smi.EnforcedPowerLimitW:0} W";
                                else
                                    GpuPowerText = $"{smi.PowerDrawW:0.0} W";
                            }
                            else if (smi != null)
                            {
                                // No NVML data. The GPU is either in D3 (expected, and Nitrous
                                // deliberately does not wake it) or NVML is not reporting yet.
                                GpuLoadText = "--";
                                GpuVramText = "-- / -- MB";
                                GpuDeepTempText = "-- C";
                                GpuPStateText = gpuAsleep ? "Asleep" : "--";
                                GpuCoreClockText = "-- MHz";
                                GpuMemClockText = "-- MHz";
                                GpuPowerText = "-- W";
                            }
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!_telemetryErrorLogged)
                        {
                            Logger.Warn("Telemetry: poll failed; will retry.", ex);
                            _telemetryErrorLogged = true;
                        }
                    }

                    await timer.WaitForNextTickAsync(token);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopTelemetryPolling();

        // Persist the last slider position even if the window is closed before the 250 ms
        // debounce elapses.
        _fanDebouncer.Flush();
        _fanDebouncer.Dispose();
        _gpuManager.Dispose();
    }
}
