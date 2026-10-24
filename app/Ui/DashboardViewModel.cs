using System;
using System.Collections.Generic;
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
    private bool _telemetryErrorLogged;
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
    public int GpuCoreOffset { get => _gpuCoreOffset; set => SetProperty(ref _gpuCoreOffset, value); }

    private int _gpuMemoryOffset;
    public int GpuMemoryOffset { get => _gpuMemoryOffset; set => SetProperty(ref _gpuMemoryOffset, value); }

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
                    GpuCoreOffset = core;
                    GpuMemoryOffset = memory;
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

                await _gpuManager.ApplyPowerProfileOcAsync(mode, allowStockReset: true);

                if (_gpuManager.TryGetClocks(out var c, out var m, out var error))
                {
                    GpuCoreOffset = c;
                    GpuMemoryOffset = m;
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
                var applied = _gpuManager.TrySetClocks(GpuCoreOffset, GpuMemoryOffset, out var error);

                if (applied)
                {
                    ApplyBtnText = "APPLIED!";
                    ApplyBtnColor = "#34C759";
                }
                else
                {
                    ApplyBtnText = "ERROR";
                    ApplyBtnColor = "#FF453A";
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
                await _gpuManager.ResetProfileToDefaultsAsync(currentProfile);

                if (_gpuManager.TryGetClocks(out var c, out var m, out var error))
                {
                    GpuCoreOffset = c;
                    GpuMemoryOffset = m;
                }
                else
                {
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
        GpuNameText = "NVIDIA GPU (SLEEPING)";
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
                if (state.BackupExists) details.Add("backup ready");
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
                        if (DeepGpuTelemetry)
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
                        _telemetryErrorLogged = false;

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
