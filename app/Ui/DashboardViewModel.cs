using System;
using System.Windows.Input;
using Nitrous.Enums;
using Nitrous.Managers;
using Nitrous.Mvvm;

namespace Nitrous.Ui;

public class DashboardViewModel : ObservableObject
{
    private readonly ActionDebouncer _fanDebouncer = new ActionDebouncer();

    public DashboardViewModel()
    {
        // Initialize Fan State
        _cpuFanSpeed = SettingsManager.Get("CustomFanSpeedCpu", 50);
        _gpuFanSpeed = SettingsManager.Get("CustomFanSpeedGpu", 50);
        _isUnifiedFans = SettingsManager.Get("UnifiedFans", 1) == 1;

        var activeFan = Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile f) ? f : FanProfile.Auto;
        IsCustomFanEnabled = activeFan == FanProfile.Medium;

        // Initialize Settings State
        _chargeLimit = SettingsManager.Get("ChargeLimit", 0) == 1;
        _autoSwitch = SettingsManager.Get("AutoSwitch", 0) == 1;
        _refreshAutoSwitch = SettingsManager.Get("RefreshAutoSwitch", 0) == 1;
        _runOnStartup = SystemOSManager.CheckStartupTask();

        // Setup UI Routing Commands
        SetPowerCommand = new RelayCommand(param =>
        {
            if (Enum.TryParse(param?.ToString(), out PowerProfile mode))
            {
                _ = AcerWmiManager.SetPowerModeAsync(mode);
                SettingsManager.Save("LastPowerMode", (int)mode);
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
            }
        });
    }

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

        // Wait 250ms after user stops dragging to update the motherboard
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
                SystemOSManager.ToggleStartupTask(value, System.Windows.Forms.Application.ExecutablePath);
        }
    }

    // --- COMMANDS ---
    public ICommand SetPowerCommand { get; }
    public ICommand SetFanCommand { get; }
}
