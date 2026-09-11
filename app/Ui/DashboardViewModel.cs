using System;
using System.Windows.Input;
using Nitrous.Enums;
using Nitrous.Managers;
using Nitrous.Mvvm;

namespace Nitrous.Ui;

public class DashboardViewModel : ObservableObject
{
    private readonly ActionDebouncer _fanDebouncer = new ActionDebouncer();

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

    private string _applyBtnColor = "#B388FF";
    public string ApplyBtnColor { get => _applyBtnColor; set => SetProperty(ref _applyBtnColor, value); }

    private readonly NvidiaGpuManager _gpuManager = new();

    private int _gpuCoreOffset;
    public int GpuCoreOffset { get => _gpuCoreOffset; set => SetProperty(ref _gpuCoreOffset, value); }

    private int _gpuMemoryOffset;
    public int GpuMemoryOffset { get => _gpuMemoryOffset; set => SetProperty(ref _gpuMemoryOffset, value); }

    private string _gpuNameText = "NVIDIA GPU";
    public string GpuNameText { get => _gpuNameText; set => SetProperty(ref _gpuNameText, value); }

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

    public DashboardViewModel()
    {
        // Initialize Fan State
        _cpuFanSpeed = SettingsManager.Get("CustomFanSpeedCpu", 50);
        _gpuFanSpeed = SettingsManager.Get("CustomFanSpeedGpu", 50);
        _isUnifiedFans = SettingsManager.Get("UnifiedFans", 1) == 1;
        var activeFan = Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile f) ? f : FanProfile.Auto;
        IsCustomFanEnabled = activeFan == FanProfile.Medium;

        // Initialize Refresh Rate Label
        int maxHz = DisplayManager.GetPrimaryMaxRefreshRate();
        MaxRefreshText = $"{maxHz}Hz";

        // Initialize Settings State
        _chargeLimit = SettingsManager.Get("ChargeLimit", 0) == 1;
        _autoSwitch = SettingsManager.Get("AutoSwitch", 0) == 1;
        _refreshAutoSwitch = SettingsManager.Get("RefreshAutoSwitch", 0) == 1;

        System.Threading.Tasks.Task.Run(() =>
        {
            bool isTaskEnabled = StartupManager.CheckStartupTask();
            _runOnStartup = isTaskEnabled;
            OnPropertyChanged(nameof(RunOnStartup));
        });

        // Setup Commands
        SetPowerCommand = new RelayCommand(param =>
        {
            if (Enum.TryParse(param?.ToString(), out PowerProfile mode))
            {
                _ = AcerWmiManager.SetPowerModeAsync(mode);
                SettingsManager.Save("LastPowerMode", (int)mode);
                bool isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                SettingsManager.Save(isOnline ? "LastAcPowerMode" : "LastDcPowerMode", (int)mode);
            }
        });

        SetFanCommand = new RelayCommand(param =>
        {
            if (Enum.TryParse(param?.ToString(), out FanProfile mode))
            {
                IsCustomFanEnabled = mode == FanProfile.Medium;
                if (mode == FanProfile.Medium)
                    _ = AcerWmiManager.SetCustomFansAsync(CpuFanSpeed, GpuFanSpeed);
                else
                    _ = AcerWmiManager.SetFansAsync(mode);

                SettingsManager.Save("LastFanMode", mode.ToString());
                bool isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                SettingsManager.Save(isOnline ? "LastAcFanMode" : "LastDcFanMode", mode.ToString());
            }
        });

        SetRefreshCommand = new RelayCommand(param =>
        {
            if (Enum.TryParse(param?.ToString(), out RefreshProfile profile))
            {
                SettingsManager.Save("RefreshMode", (int)profile);
                bool isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                DisplayManager.ApplyRefreshProfile(profile, isOnline);
            }
        });

        if (_gpuManager.IsValid && _gpuManager.GetClocks(out int core, out int memory))
        {
            GpuCoreOffset = core;
            GpuMemoryOffset = memory;
        }

        ApplyGpuClocksCommand = new RelayCommand(async _ =>
        {
            int result = _gpuManager.SetClocks(GpuCoreOffset, GpuMemoryOffset);

            if (result == 1)
            {
                ApplyBtnText = "APPLIED!";
                ApplyBtnColor = "#34C759"; // Green
            }
            else
            {
                ApplyBtnText = "ERROR";
                ApplyBtnColor = "#FF453A"; // Red
            }

            // Keep the status visible for 2 seconds
            await System.Threading.Tasks.Task.Delay(2000);

            // Revert back to default state
            ApplyBtnText = "APPLY";
            ApplyBtnColor = "#B388FF";
        });

        ResetGpuClocksCommand = new RelayCommand(_ =>
        {
            _gpuManager.ResetOverclock();
            if (_gpuManager.GetClocks(out int c, out int m))
            {
                GpuCoreOffset = c;
                GpuMemoryOffset = m;
            }
        });

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
            if (SetProperty(ref _cpuFanSpeed, value))
            {
                if (IsUnifiedFans) GpuFanSpeed = value;
                TriggerFanSave();
            }
        }
    }

    private int _gpuFanSpeed;
    public int GpuFanSpeed
    {
        get => _gpuFanSpeed;
        set
        {
            if (SetProperty(ref _gpuFanSpeed, value))
            {
                if (IsUnifiedFans) CpuFanSpeed = value;
                TriggerFanSave();
            }
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
            if (SetProperty(ref _isCustomFanEnabled, value))
            {
                OnPropertyChanged(nameof(CustomFanOpacity));
            }
        }
    }

    public double CustomFanOpacity => IsCustomFanEnabled ? 1.0 : 0.4;

    private void TriggerFanSave()
    {
        if (!IsCustomFanEnabled) return;
        _fanDebouncer.Debounce(250, () =>
        {
            SettingsManager.Save("CustomFanSpeedCpu", CpuFanSpeed);
            SettingsManager.Save("CustomFanSpeedGpu", GpuFanSpeed);
            _ = AcerWmiManager.SetCustomFansAsync(CpuFanSpeed, GpuFanSpeed);
        });
    }

    // --- SETTINGS PROPERTIES & LOGIC ---
    private bool _chargeLimit;
    public bool ChargeLimit
    {
        get => _chargeLimit;
        set
        {
            if (SetProperty(ref _chargeLimit, value))
            {
                SettingsManager.Save("ChargeLimit", value ? 1 : 0);
                _ = AcerWmiManager.SetChargeLimitAsync(value);
            }
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

    private bool _runOnStartup;
    public bool RunOnStartup
    {
        get => _runOnStartup;
        set
        {
            if (SetProperty(ref _runOnStartup, value))
                StartupManager.ToggleStartupTask(value, System.Windows.Forms.Application.ExecutablePath);
        }
    }

    // --- COMMANDS ---
    public ICommand SetPowerCommand { get; }
    public ICommand SetFanCommand { get; }
    public ICommand SetRefreshCommand { get; }
    public ICommand ApplyGpuClocksCommand { get; }
    public ICommand ResetGpuClocksCommand { get; }

    private async void StartTelemetryPolling()
    {
        while (true)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var telemetry = AcerWmiManager.GetSystemTelemetry();
                var smi = NvidiaGpuManager.GetSmiTelemetry();

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    CpuTempText = telemetry.CpuTemp > 0 ? $"{telemetry.CpuTemp}°C" : "--°C";
                    CpuRpmText = telemetry.CpuRpm > 0 ? $"{telemetry.CpuRpm} RPM" : "-- RPM";
                    CpuTempColor = telemetry.CpuTemp > 90 ? "#FF453A" : (telemetry.CpuTemp >= 85 ? "#FF9F0A" : "White");

                    GpuTempText = telemetry.GpuTemp > 0 ? $"{telemetry.GpuTemp}°C" : "--°C";
                    GpuRpmText = telemetry.GpuRpm > 0 ? $"{telemetry.GpuRpm} RPM" : "-- RPM";
                    GpuTempColor = telemetry.GpuTemp > 85 ? "#FF453A" : (telemetry.GpuTemp >= 78 ? "#FF9F0A" : "White");

                    if (!string.IsNullOrEmpty(smi.Name) && smi.Name != "Unknown")
                    {
                        GpuNameText = smi.Name;
                        GpuLoadText = $"{smi.GpuLoad}%";
                        GpuLoadColor = smi.GpuLoad >= 95 ? "#FF453A" : (smi.GpuLoad >= 80 ? "#FF9F0A" : "White");

                        GpuVramText = $"{smi.VramUsedMb} / {smi.VramTotalMb} MB";

                        GpuDeepTempText = $"{smi.CoreTemp} C";
                        GpuDeepTempColor = smi.CoreTemp >= 85 ? "#FF453A" : (smi.CoreTemp >= 78 ? "#FF9F0A" : "White");

                        GpuPStateText = smi.PState;
                        GpuCoreClockText = $"{smi.CurrentCoreClock} MHz";
                        GpuMemClockText = $"{smi.CurrentMemoryClock} MHz";

                        if (smi.EnforcedPowerLimitW > 0 && smi.MaxPowerLimitW > 0)
                        {
                            GpuPowerText = $"{smi.PowerDrawW:0.0} / {smi.EnforcedPowerLimitW:0} / {smi.MaxPowerLimitW:0} W";
                        }
                        else if (smi.EnforcedPowerLimitW > 0) // Fallback if only enforced limit is detected
                        {
                            GpuPowerText = $"{smi.PowerDrawW:0.0} / {smi.EnforcedPowerLimitW:0} W";
                        }
                        else // Fallback if limits are unavailable (e.g., GPU is asleep)
                        {
                            GpuPowerText = $"{smi.PowerDrawW:0.0} W";
                        }
                    }
                });
            });

            await System.Threading.Tasks.Task.Delay(2000);
        }
    }
}
