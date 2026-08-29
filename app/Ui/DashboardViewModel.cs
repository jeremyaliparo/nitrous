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

    private string _cpuRpmText = "0 RPM";
    public string CpuRpmText { get => _cpuRpmText; set => SetProperty(ref _cpuRpmText, value); }

    private string _gpuTempText = "--°C";
    public string GpuTempText { get => _gpuTempText; set => SetProperty(ref _gpuTempText, value); }

    private string _gpuRpmText = "Sleep";
    public string GpuRpmText { get => _gpuRpmText; set => SetProperty(ref _gpuRpmText, value); }

    private string _gpuTempColor = "White";
    public string GpuTempColor { get => _gpuTempColor; set => SetProperty(ref _gpuTempColor, value); }

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

    private async void StartTelemetryPolling()
    {
        while (true)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var telemetry = AcerWmiManager.GetSystemTelemetry();

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    // Update CPU
                    CpuTempText = telemetry.CpuTemp > 0 ? $"{telemetry.CpuTemp}°C" : "--°C";
                    CpuRpmText = $"{telemetry.CpuRpm} RPM";
                    CpuTempColor = telemetry.CpuTemp > 85 ? "#FF453A" : "White";

                    // Update GPU
                    GpuTempText = telemetry.GpuTemp > 0 ? $"{telemetry.GpuTemp}°C" : "--°C";

                    if (telemetry.GpuTemp > 0)
                    {
                        // GPU is awake! If RPM is 0, the WMI tachometer is locked/unsupported, so we omit it.
                        GpuRpmText = telemetry.GpuRpm > 0 ? $"{telemetry.GpuRpm} RPM" : "";
                    }
                    else
                    {
                        // GPU is genuinely deeply asleep (0°C)
                        GpuRpmText = "Sleep";
                    }

                    GpuTempColor = telemetry.GpuTemp > 85 ? "#FF453A" : "White";
                });
            });

            await System.Threading.Tasks.Task.Delay(2000);
        }
    }
}
