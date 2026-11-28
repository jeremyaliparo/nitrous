using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;
using Nitrous.Managers;
using Nitrous.Mvvm;
using Nitrous.Ui.Theme;

using MessageBox = System.Windows.MessageBox;

namespace Nitrous.Ui;

public class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly ActionDebouncer _fanDebouncer = new();
    private readonly NvidiaGpuManager _gpuManager = new();
    private CancellationTokenSource? _pollingCts;
    private volatile bool _telemetryEnabled = true;
    private volatile bool _gpuTelemetryActive;
    private bool _gpuTempFromDriverLogged;
    private bool _telemetryErrorLogged;

    // CPU usage is the idle/total delta between two polls; the first poll only records a baseline.
    private ulong _cpuIdle;
    private ulong _cpuKernel;
    private ulong _cpuUser;
    private bool _haveCpuSample;

    // Temperature history: one timestamped sample per poll, kept for the largest window (15 min at
    // 2 s). The 2/5/15 minute views are slices of the same data, so switching never drops samples.
    private const int HistoryCapacity = 450;
    private const double ChartWidth = 288;
    private const double ChartHeight = 50;

    /// <summary>A gap this long is the dashboard having been hidden, not a slow poll.</summary>
    private static readonly TimeSpan ChartGap = TimeSpan.FromSeconds(12);

    private readonly object _historyGate = new();
    private readonly List<HistorySample> _history = new(HistoryCapacity);

    private int _chartSpanMinutes = 2;
    private bool _showCpuChart = true;
    private bool _showGpuChart = true;

    /// <summary>One visible sample in chart pixels; the hover readout uses it without rebuilding anything.</summary>
    public readonly record struct ChartSample(double X, double CpuY, double GpuY, DateTime At, int Cpu, int Gpu);

    private readonly record struct HistorySample(
        DateTime At, int Cpu, int Gpu, int CpuRpm, int GpuRpm, int CpuUsage, int GpuUsage,
        int GpuClock, double GpuPower, double GpuPowerLimit);

    private Geometry _cpuHistoryGeometry = Geometry.Empty;
    public Geometry CpuHistoryGeometry
    {
        get => _cpuHistoryGeometry;
        set => SetProperty(ref _cpuHistoryGeometry, value);
    }

    private Geometry _gpuHistoryGeometry = Geometry.Empty;
    public Geometry GpuHistoryGeometry
    {
        get => _gpuHistoryGeometry;
        set => SetProperty(ref _gpuHistoryGeometry, value);
    }

    private IReadOnlyList<ChartSample> _chartSamples = Array.Empty<ChartSample>();
    public IReadOnlyList<ChartSample> ChartSamples
    {
        get => _chartSamples;
        set => SetProperty(ref _chartSamples, value);
    }

    private string _chartAxisTop = "";
    public string ChartAxisTop
    {
        get => _chartAxisTop;
        set => SetProperty(ref _chartAxisTop, value);
    }

    private string _chartAxisMid = "";
    public string ChartAxisMid
    {
        get => _chartAxisMid;
        set => SetProperty(ref _chartAxisMid, value);
    }

    private string _chartAxisBottom = "";
    public string ChartAxisBottom
    {
        get => _chartAxisBottom;
        set => SetProperty(ref _chartAxisBottom, value);
    }

    private string _chartTimeLeft = "-2m";
    public string ChartTimeLeft
    {
        get => _chartTimeLeft;
        set => SetProperty(ref _chartTimeLeft, value);
    }

    private string _chartTimeMid = "-1m";
    public string ChartTimeMid
    {
        get => _chartTimeMid;
        set => SetProperty(ref _chartTimeMid, value);
    }

    private string _cpuHistoryStatsText = "";
    public string CpuHistoryStatsText
    {
        get => _cpuHistoryStatsText;
        set => SetProperty(ref _cpuHistoryStatsText, value);
    }

    private string _gpuHistoryStatsText = "";
    public string GpuHistoryStatsText
    {
        get => _gpuHistoryStatsText;
        set => SetProperty(ref _gpuHistoryStatsText, value);
    }

    private string _auxHistoryStatsText = "";
    public string AuxHistoryStatsText
    {
        get => _auxHistoryStatsText;
        set => SetProperty(ref _auxHistoryStatsText, value);
    }

    /// <summary>2, 5 or 15 minutes, remembered across sessions; anything else falls back to 2.</summary>
    public int ChartSpanMinutes
    {
        get => _chartSpanMinutes;
        set
        {
            if (value is not (2 or 5 or 15)) return;
            if (!SetProperty(ref _chartSpanMinutes, value)) return;
            SettingsManager.Save("ChartSpanMinutes", value);
            OnPropertyChanged(nameof(ChartSpan2));
            OnPropertyChanged(nameof(ChartSpan5));
            OnPropertyChanged(nameof(ChartSpan15));
            UpdateHistoryChart();
        }
    }

    public bool ChartSpan2
    {
        get => _chartSpanMinutes == 2;
        set { if (value) ChartSpanMinutes = 2; }
    }

    public bool ChartSpan5
    {
        get => _chartSpanMinutes == 5;
        set { if (value) ChartSpanMinutes = 5; }
    }

    public bool ChartSpan15
    {
        get => _chartSpanMinutes == 15;
        set { if (value) ChartSpanMinutes = 15; }
    }

    public bool ShowCpuChart
    {
        get => _showCpuChart;
        set
        {
            if (!SetProperty(ref _showCpuChart, value)) return;
            OnPropertyChanged(nameof(CpuLegendOpacity));
            UpdateHistoryChart();
        }
    }

    public bool ShowGpuChart
    {
        get => _showGpuChart;
        set
        {
            if (!SetProperty(ref _showGpuChart, value)) return;
            OnPropertyChanged(nameof(GpuLegendOpacity));
            UpdateHistoryChart();
        }
    }

    public double CpuLegendOpacity => _showCpuChart ? 1 : 0.35;
    public double GpuLegendOpacity => _showGpuChart ? 1 : 0.35;

    public RelayCommand ToggleCpuChartCommand { get; private set; } = null!;
    public RelayCommand ToggleGpuChartCommand { get; private set; } = null!;

    // GPU page: power-draw history and the meters. Power is recorded only while the GPU page is open,
    // because that is the only time NVML is polled; a closed page is a gap, not a zero.
    private int _gpuChartSpanMinutes = 2;

    /// <summary>One visible sample in the power plot; the hover readout binary-searches it.</summary>
    public readonly record struct GpuPowerPoint(double X, double Y, DateTime At, double Watts);

    private Geometry _gpuPowerLineGeometry = Geometry.Empty;
    public Geometry GpuPowerLineGeometry
    {
        get => _gpuPowerLineGeometry;
        set => SetProperty(ref _gpuPowerLineGeometry, value);
    }

    private Geometry _gpuPowerAreaGeometry = Geometry.Empty;
    public Geometry GpuPowerAreaGeometry
    {
        get => _gpuPowerAreaGeometry;
        set => SetProperty(ref _gpuPowerAreaGeometry, value);
    }

    private IReadOnlyList<GpuPowerPoint> _gpuPowerSamples = Array.Empty<GpuPowerPoint>();
    public IReadOnlyList<GpuPowerPoint> GpuPowerSamples
    {
        get => _gpuPowerSamples;
        set => SetProperty(ref _gpuPowerSamples, value);
    }

    private double _gpuPowerLimitY = ChartHeight;
    public double GpuPowerLimitY
    {
        get => _gpuPowerLimitY;
        set => SetProperty(ref _gpuPowerLimitY, value);
    }

    private bool _hasGpuPowerLimit;
    public bool HasGpuPowerLimit
    {
        get => _hasGpuPowerLimit;
        set => SetProperty(ref _hasGpuPowerLimit, value);
    }

    private string _gpuChartTimeLeft = "-2m";
    public string GpuChartTimeLeft
    {
        get => _gpuChartTimeLeft;
        set => SetProperty(ref _gpuChartTimeLeft, value);
    }

    private string _gpuChartTimeMid = "-1m";
    public string GpuChartTimeMid
    {
        get => _gpuChartTimeMid;
        set => SetProperty(ref _gpuChartTimeMid, value);
    }

    private string _gpuPowerValueText = "-- W";
    public string GpuPowerValueText
    {
        get => _gpuPowerValueText;
        set => SetProperty(ref _gpuPowerValueText, value);
    }

    private string _gpuPowerLimitText = "";
    public string GpuPowerLimitText
    {
        get => _gpuPowerLimitText;
        set => SetProperty(ref _gpuPowerLimitText, value);
    }

    private string _gpuPowerFooterText = "";
    public string GpuPowerFooterText
    {
        get => _gpuPowerFooterText;
        set => SetProperty(ref _gpuPowerFooterText, value);
    }

    private string _gpuVramShortText = "--";
    public string GpuVramShortText
    {
        get => _gpuVramShortText;
        set => SetProperty(ref _gpuVramShortText, value);
    }

    private string _gpuTempMeterText = "--";
    public string GpuTempMeterText
    {
        get => _gpuTempMeterText;
        set => SetProperty(ref _gpuTempMeterText, value);
    }

    private double _gpuLoadPercent;
    public double GpuLoadPercent
    {
        get => _gpuLoadPercent;
        set => SetProperty(ref _gpuLoadPercent, value);
    }

    private double _gpuVramPercent;
    public double GpuVramPercent
    {
        get => _gpuVramPercent;
        set => SetProperty(ref _gpuVramPercent, value);
    }

    private double _gpuTempPercent;
    public double GpuTempPercent
    {
        get => _gpuTempPercent;
        set => SetProperty(ref _gpuTempPercent, value);
    }

    private bool _gpuAsleep;
    public bool GpuAsleep
    {
        get => _gpuAsleep;
        set
        {
            if (!SetProperty(ref _gpuAsleep, value)) return;
            OnPropertyChanged(nameof(GpuAwake));
        }
    }

    public bool GpuAwake => !_gpuAsleep;

    private string _gpuStateText = "Asleep";
    public string GpuStateText
    {
        get => _gpuStateText;
        set => SetProperty(ref _gpuStateText, value);
    }

    /// <summary>The driver's current thermal target as last read on the GPU page; 0 when unknown.</summary>
    private int _gpuThermalTargetNow;
    public int GpuThermalTargetNow
    {
        get => _gpuThermalTargetNow;
        set => SetProperty(ref _gpuThermalTargetNow, value);
    }

    /// <summary>The driver's current core-clock cap as last read on the GPU page; 0 when none.</summary>
    private int _gpuClockCapNow;
    public int GpuClockCapNow
    {
        get => _gpuClockCapNow;
        set => SetProperty(ref _gpuClockCapNow, value);
    }

    private string _gpuLimitSummaryText = "";
    public string GpuLimitSummaryText
    {
        get => _gpuLimitSummaryText;
        set => SetProperty(ref _gpuLimitSummaryText, value);
    }

    /// <summary>
    /// The applied limits, shown beside the live telemetry so the meters and the tuning card agree.
    /// Uses the values read from the driver, not the staged sliders: a chosen-but-unapplied limit
    /// must not appear as active.
    /// </summary>
    public void UpdateGpuLimitSummary()
    {
        var parts = new List<string>(2);

        var thermal = GpuThermalTargetNow > 0 ? GpuThermalTargetNow : GpuThermalMaximum;
        if (thermal > 0) parts.Add($"Thermal target {TemperatureDisplay.Short(thermal)}");

        if (GpuClockMaximum > 0)
            parts.Add(GpuClockCapNow > 0 && GpuClockCapNow < GpuClockMaximum
                ? $"Clock cap {GpuClockCapNow} MHz"
                : "No clock cap");

        GpuLimitSummaryText = string.Join(" \u00b7 ", parts);
    }

    /// <summary>2, 5 or 15 minutes for the power plot, remembered; anything else falls back to 2.</summary>
    public int GpuChartSpanMinutes
    {
        get => _gpuChartSpanMinutes;
        set
        {
            if (value is not (2 or 5 or 15)) return;
            if (!SetProperty(ref _gpuChartSpanMinutes, value)) return;
            SettingsManager.Save("GpuChartSpanMinutes", value);
            OnPropertyChanged(nameof(GpuChartSpan2));
            OnPropertyChanged(nameof(GpuChartSpan5));
            OnPropertyChanged(nameof(GpuChartSpan15));
            UpdateHistoryChart();
        }
    }

    public bool GpuChartSpan2
    {
        get => _gpuChartSpanMinutes == 2;
        set { if (value) GpuChartSpanMinutes = 2; }
    }

    public bool GpuChartSpan5
    {
        get => _gpuChartSpanMinutes == 5;
        set { if (value) GpuChartSpanMinutes = 5; }
    }

    public bool GpuChartSpan15
    {
        get => _gpuChartSpanMinutes == 15;
        set { if (value) GpuChartSpanMinutes = 15; }
    }

    // GPU MUX (BIOS). Offered only where the firmware has the switch and NVIDIA DDS is not the
    // control: Advanced Optimus machines switch the display live in the NVIDIA app.
    private bool _gpuMuxAvailable;
    public bool GpuMuxAvailable
    {
        get => _gpuMuxAvailable;
        set
        {
            if (!SetProperty(ref _gpuMuxAvailable, value)) return;
            OnPropertyChanged(nameof(GpuMuxCanApply));
        }
    }

    private bool _gpuDdsNoticeVisible;
    public bool GpuDdsNoticeVisible
    {
        get => _gpuDdsNoticeVisible;
        set => SetProperty(ref _gpuDdsNoticeVisible, value);
    }

    private bool _gpuMuxAutomaticAvailable;
    public bool GpuMuxAutomaticAvailable
    {
        get => _gpuMuxAutomaticAvailable;
        set => SetProperty(ref _gpuMuxAutomaticAvailable, value);
    }

    private GpuMuxMode _gpuMuxSelected = GpuMuxMode.Hybrid;
    private bool _gpuMuxLoading;

    public bool GpuMuxHybrid
    {
        get => _gpuMuxSelected == GpuMuxMode.Hybrid;
        set { if (value) StageGpuMux(GpuMuxMode.Hybrid); }
    }

    public bool GpuMuxDiscrete
    {
        get => _gpuMuxSelected == GpuMuxMode.Discrete;
        set { if (value) StageGpuMux(GpuMuxMode.Discrete); }
    }

    public bool GpuMuxAutomatic
    {
        get => _gpuMuxSelected == GpuMuxMode.Automatic;
        set { if (value) StageGpuMux(GpuMuxMode.Automatic); }
    }

    public GpuMuxMode SelectedGpuMux => _gpuMuxSelected;

    private string _gpuMuxCurrentText = "";
    public string GpuMuxCurrentText
    {
        get => _gpuMuxCurrentText;
        set => SetProperty(ref _gpuMuxCurrentText, value);
    }

    private string _gpuMuxHintText = "";
    public string GpuMuxHintText
    {
        get => _gpuMuxHintText;
        set => SetProperty(ref _gpuMuxHintText, value);
    }

    private string _gpuMuxStatusText = "";
    public string GpuMuxStatusText
    {
        get => _gpuMuxStatusText;
        set => SetProperty(ref _gpuMuxStatusText, value);
    }

    private bool _gpuMuxRestartPending;
    public bool GpuMuxRestartPending
    {
        get => _gpuMuxRestartPending;
        set => SetProperty(ref _gpuMuxRestartPending, value);
    }

    private bool _gpuMuxBusy;
    public bool GpuMuxBusy
    {
        get => _gpuMuxBusy;
        set
        {
            if (!SetProperty(ref _gpuMuxBusy, value)) return;
            OnPropertyChanged(nameof(GpuMuxCanApply));
        }
    }

    public bool GpuMuxCanApply => GpuMuxAvailable && !_gpuMuxBusy;

    public void LoadGpuMux(GpuMuxState? state, bool ddsSupported)
    {
        _gpuMuxLoading = true;
        try
        {
            var capable = state is { Supported: true };
            GpuMuxAvailable = capable && !ddsSupported;
            GpuDdsNoticeVisible = capable && ddsSupported;
            GpuMuxAutomaticAvailable = capable && state!.Value.Automatic;
            GpuMuxRestartPending = false;
            GpuMuxBusy = false;
            GpuMuxStatusText = "";

            if (GpuMuxAvailable)
            {
                _gpuMuxSelected = state!.Value.Current;
                GpuMuxCurrentText = $"Stored: {GpuMux.Name(_gpuMuxSelected)}";
                GpuMuxHintText = GpuMux.Describe(_gpuMuxSelected);
            }
            else
            {
                GpuMuxCurrentText = "";
                GpuMuxHintText = "";
            }
        }
        finally
        {
            _gpuMuxLoading = false;
        }

        RaiseGpuMuxSelection();
    }

    private void StageGpuMux(GpuMuxMode mode)
    {
        if (_gpuMuxLoading) return;
        if (mode == GpuMuxMode.Automatic && !GpuMuxAutomaticAvailable) return;

        _gpuMuxSelected = mode;
        GpuMuxHintText = GpuMux.Describe(mode);
        GpuMuxStatusText = "";
        RaiseGpuMuxSelection();
    }

    private void RaiseGpuMuxSelection()
    {
        OnPropertyChanged(nameof(GpuMuxHybrid));
        OnPropertyChanged(nameof(GpuMuxDiscrete));
        OnPropertyChanged(nameof(GpuMuxAutomatic));
    }

    public void ShowGpuMuxResult(GpuMuxResult result)
    {
        GpuMuxBusy = false;
        GpuMuxStatusText = result.Message;
        GpuMuxRestartPending = result.Ok && result.Stored is not null;

        if (result.Ok && result.Stored is { } stored)
        {
            GpuMuxCurrentText = $"Stored: {GpuMux.Name(stored)}";
            GpuMuxHintText = GpuMux.Describe(stored);
        }
    }

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

    private string _cpuTempText = $"-- {TemperatureDisplay.Unit}";
    public string CpuTempText { get => _cpuTempText; set => SetProperty(ref _cpuTempText, value); }

    private string _cpuTempColor = ThemeManager.TextPrimaryHex;
    public string CpuTempColor { get => _cpuTempColor; set => SetProperty(ref _cpuTempColor, value); }

    private string _cpuRpmText = "-- RPM";
    public string CpuRpmText { get => _cpuRpmText; set => SetProperty(ref _cpuRpmText, value); }

    /// <summary>Short "· 12%" suffix; empty until a second sample exists.</summary>
    private string _cpuUsageText = "";
    public string CpuUsageText { get => _cpuUsageText; set => SetProperty(ref _cpuUsageText, value); }

    private string _gpuUsageText = "";
    public string GpuUsageText { get => _gpuUsageText; set => SetProperty(ref _gpuUsageText, value); }

    private PointCollection _cpuHistoryPoints = new();
    public PointCollection CpuHistoryPoints
    {
        get => _cpuHistoryPoints;
        set => SetProperty(ref _cpuHistoryPoints, value);
    }

    private PointCollection _gpuHistoryPoints = new();
    public PointCollection GpuHistoryPoints
    {
        get => _gpuHistoryPoints;
        set => SetProperty(ref _gpuHistoryPoints, value);
    }

    private string _gpuTempText = $"-- {TemperatureDisplay.Unit}";
    public string GpuTempText { get => _gpuTempText; set => SetProperty(ref _gpuTempText, value); }

    private string _gpuRpmText = "-- RPM";
    public string GpuRpmText { get => _gpuRpmText; set => SetProperty(ref _gpuRpmText, value); }

    private string _gpuTempColor = ThemeManager.TextPrimaryHex;
    public string GpuTempColor { get => _gpuTempColor; set => SetProperty(ref _gpuTempColor, value); }

    private string _applyBtnText = "APPLY";
    public string ApplyBtnText { get => _applyBtnText; set => SetProperty(ref _applyBtnText, value); }

    private string _applyBtnColor = ThemeManager.AccentDeepHex;
    public string ApplyBtnColor { get => _applyBtnColor; set => SetProperty(ref _applyBtnColor, value); }

    /// <summary>
    /// Re-reads the colours that come from the accent. ApplyBtnColor is a captured string rather
    /// than a DynamicResource, so without this the GPU page's APPLY button keeps the accent that
    /// was in force when the window opened. The transient APPLIED!/ERROR colours are left alone;
    /// the reset after them already reads the current accent.
    /// </summary>
    /// <summary>
    /// Writes all four settings for the active profile: the two clock offsets and the two limits. They share
    /// one Apply because the limits sat directly above the button, and applying them on selection made the
    /// button look like it did less than it did.
    /// </summary>
    private async Task ApplyGpuTuningAsync(string source)
    {
        if (_isApplyingClocks) return;

        _isApplyingClocks = true;
        try
        {
            var currentProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);

            _gpuManager.SaveCustomProfileOc(currentProfile, GpuCoreOffset, GpuMemoryOffset);
            var offsetsApplied = _gpuManager.TrySetClocks(
                GpuCoreOffset, GpuMemoryOffset, out var error, source);

            var applied = new List<string>();
            var refused = new List<string>();

            if (offsetsApplied)
                applied.Add($"{FormatOffset(GpuCoreOffset)} / {FormatOffset(GpuMemoryOffset)} MHz offsets");
            else if (_gpuManager.IsGpuSleeping)
                refused.Add("the GPU is asleep, so the offsets were saved but not written");
            else
                refused.Add($"offsets ({error})");

            if (GpuThermalEditable)
            {
                var target = GpuThermalLimit >= GpuThermalMaximum ? 0 : GpuThermalLimit;
                var result = GpuThermalTarget.Apply(target == 0 ? null : target);
                if (result.Ok)
                {
                    GpuThermalTarget.SaveFor(currentProfile, target);
                    applied.Add(target == 0 ? "the driver's temperature" : TemperatureDisplay.Short(target));
                }
                else
                {
                    refused.Add($"thermal target ({result.Message})");
                }
            }

            if (GpuClockEditable)
            {
                var cap = GpuClockCap >= GpuClockMaximum ? 0 : GpuClockCap;
                var result = GpuClockLimit.Apply(cap == 0 ? null : cap);
                if (result.Ok)
                {
                    GpuClockLimit.SaveFor(currentProfile, cap);
                    applied.Add(cap == 0 ? "no clock cap" : $"{cap} MHz cap");
                }
                else
                {
                    refused.Add($"clock cap ({result.Message})");
                }
            }

            var ok = refused.Count == 0;

            ApplyBtnText = ok ? (source == "reset" ? "RESET!" : "APPLIED!") : "ERROR";
            ApplyBtnColor = ok ? ThemeManager.SuccessHex : ThemeManager.DangerHex;

            GpuClockStatusText = ok
                ? $"Applied {string.Join(", ", applied)} to {currentProfile}."
                : $"{currentProfile}: {string.Join("; ", refused)}.";

            if (!ok) Logger.Warn($"GPU tuning: {GpuClockStatusText}");

            GpuTuningApplied?.Invoke();

            await Task.Delay(2000);
            ApplyBtnText = "APPLY";
            ApplyBtnColor = ThemeManager.AccentDeepHex;
        }
        catch (Exception ex)
        {
            Logger.Warn("GPU tuning: apply failed.", ex);
        }
        finally
        {
            _isApplyingClocks = false;
        }
    }

    /// <summary>Raised after Apply or Reset, so the page can re-read what the driver now holds.</summary>
    public event Action? GpuTuningApplied;

    public void RefreshAccentColours()
    {
        if (ApplyBtnText == "APPLY") ApplyBtnColor = ThemeManager.AccentDeepHex;
    }

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

    // --- the two limits: staged like the offsets and committed by the same Apply -----------

    private int _gpuThermalMinimum = 75;
    public int GpuThermalMinimum
    {
        get => _gpuThermalMinimum;
        set
        {
            if (!SetProperty(ref _gpuThermalMinimum, value)) return;
            OnPropertyChanged(nameof(GpuThermalEditable));
            OnPropertyChanged(nameof(GpuThermalMinimumText));
        }
    }

    public string GpuThermalMinimumText => TemperatureDisplay.Short(_gpuThermalMinimum);

    private int _gpuThermalMaximum;
    /// <summary>The policy's own default: the top of the slider, and the "follow the driver" choice.</summary>
    public int GpuThermalMaximum
    {
        get => _gpuThermalMaximum;
        set
        {
            if (!SetProperty(ref _gpuThermalMaximum, value)) return;
            OnPropertyChanged(nameof(GpuThermalEditable));
            OnPropertyChanged(nameof(GpuThermalLimitText));
            OnPropertyChanged(nameof(GpuThermalMaximumText));
        }
    }

    public string GpuThermalMaximumText =>
        _gpuThermalMaximum <= 0 ? "—" : $"{TemperatureDisplay.Short(_gpuThermalMaximum)} default";

    private int _gpuThermalLimit;
    public int GpuThermalLimit
    {
        get => _gpuThermalLimit;
        set
        {
            if (GpuThermalMaximum > 0) value = Math.Clamp(value, GpuThermalMinimum, GpuThermalMaximum);
            if (!SetProperty(ref _gpuThermalLimit, value)) return;
            OnPropertyChanged(nameof(GpuThermalLimitText));
        }
    }

    public string GpuThermalLimitText =>
        GpuThermalMaximum <= 0 ? "—"
        : GpuThermalLimit >= GpuThermalMaximum ? $"{TemperatureDisplay.Short(GpuThermalLimit)} (default)"
        : TemperatureDisplay.Short(GpuThermalLimit);

    /// <summary>Re-reads the unit choice for the limits that are shown as text, not polled.</summary>
    public void RefreshTemperatureLabels()
    {
        OnPropertyChanged(nameof(GpuThermalLimitText));
        OnPropertyChanged(nameof(GpuThermalMinimumText));
        OnPropertyChanged(nameof(GpuThermalMaximumText));
    }

    public bool GpuThermalEditable => ManageGpuClocks && GpuThermalMaximum > GpuThermalMinimum;

    private int _gpuClockMinimum = GpuClockLimit.FloorMhz;
    public int GpuClockMinimum
    {
        get => _gpuClockMinimum;
        set
        {
            if (!SetProperty(ref _gpuClockMinimum, value)) return;
            OnPropertyChanged(nameof(GpuClockEditable));
        }
    }

    private int _gpuClockMaximum;
    /// <summary>The GPU's maximum boost: the top of the slider, and the "no cap" choice.</summary>
    public int GpuClockMaximum
    {
        get => _gpuClockMaximum;
        set
        {
            if (!SetProperty(ref _gpuClockMaximum, value)) return;
            OnPropertyChanged(nameof(GpuClockEditable));
            OnPropertyChanged(nameof(GpuClockLimitText));
        }
    }

    private int _gpuClockCap;
    public int GpuClockCap
    {
        get => _gpuClockCap;
        set
        {
            if (GpuClockMaximum > 0) value = Math.Clamp(value, GpuClockMinimum, GpuClockMaximum);
            if (!SetProperty(ref _gpuClockCap, value)) return;
            OnPropertyChanged(nameof(GpuClockLimitText));
        }
    }

    public string GpuClockLimitText =>
        GpuClockMaximum <= 0 ? "—"
        : GpuClockCap >= GpuClockMaximum ? "No cap"
        : $"{GpuClockCap} MHz";

    public bool GpuClockEditable => ManageGpuClocks && GpuClockMaximum > GpuClockMinimum;

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

    private bool _showScrollbars;

    /// <summary>Appearance: the four pages use the wider, clearly visible scrollbar.</summary>
    public bool ShowScrollbars
    {
        get => _showScrollbars;
        set => SetProperty(ref _showScrollbars, value);
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
                OnPropertyChanged(nameof(GpuThermalEditable));
                OnPropertyChanged(nameof(GpuClockEditable));
                    return;
                }

                // Save before applying so the tray watchdog (a separate process) sees the
                // new ownership immediately.
                SettingsManager.Save(NvidiaGpuManager.ManageGpuClocksKey, 1);
                _manageGpuClocks = true;
                GpuClockStatusText = "Nitrous manages GPU clocks. Applying the active profile offsets...";
                OnPropertyChanged(nameof(ManageGpuClocks));
                OnPropertyChanged(nameof(GpuThermalEditable));
                OnPropertyChanged(nameof(GpuClockEditable));
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
                OnPropertyChanged(nameof(GpuThermalEditable));
                OnPropertyChanged(nameof(GpuClockEditable));
                    return;
                }

                clearOffsets = choice == MessageBoxResult.Yes;
            }

            // Persist first so the tray watchdog stops writing immediately. Turning the
            // switch off is otherwise read-only.
            SettingsManager.Save(NvidiaGpuManager.ManageGpuClocksKey, 0);
            _manageGpuClocks = false;
            OnPropertyChanged(nameof(ManageGpuClocks));
                OnPropertyChanged(nameof(GpuThermalEditable));
                OnPropertyChanged(nameof(GpuClockEditable));

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

    private string _gpuPStateText = "P0";
    public string GpuPStateText { get => _gpuPStateText; set => SetProperty(ref _gpuPStateText, value); }

    private string _gpuLoadColor = ThemeManager.AccentHex;
    public string GpuLoadColor { get => _gpuLoadColor; set => SetProperty(ref _gpuLoadColor, value); }

    private string _gpuDeepTempColor = ThemeManager.AccentHex;
    public string GpuDeepTempColor { get => _gpuDeepTempColor; set => SetProperty(ref _gpuDeepTempColor, value); }

    private string _gpuCoreClockText = "0 MHz";
    public string GpuCoreClockText { get => _gpuCoreClockText; set => SetProperty(ref _gpuCoreClockText, value); }

    private string _gpuMemClockText = "0 MHz";
    public string GpuMemClockText { get => _gpuMemClockText; set => SetProperty(ref _gpuMemClockText, value); }

    // Seeded from the last run: the hardware check below runs after the window is up, so an
    // unsupported model used to show the Turbo button first and drop it a moment later.
    private bool _isTurboSupported = SettingsManager.Get("TurboModeSupported", 1) == 1;
    public bool IsTurboSupported { get => _isTurboSupported; set => SetProperty(ref _isTurboSupported, value); }

    /// <summary>Whether a vendor GPU tool was found; the GPU page hides its entry when not.</summary>
    private bool _isNvidiaToolAvailable;
    public bool IsNvidiaToolAvailable
    {
        get => _isNvidiaToolAvailable;
        set => SetProperty(ref _isNvidiaToolAvailable, value);
    }

    /// <summary>"NVIDIA App" or "NVIDIA Control Panel", whatever is installed.</summary>
    private string _nvidiaToolLabel = "NVIDIA App";
    public string NvidiaToolLabel
    {
        get => _nvidiaToolLabel;
        set => SetProperty(ref _nvidiaToolLabel, value);
    }

    /// <summary>Formatted binding, empty when none is set, for the hint under the button.</summary>
    private string _nvidiaHotkeyText = "";
    public string NvidiaHotkeyText
    {
        get => _nvidiaHotkeyText;
        set => SetProperty(ref _nvidiaHotkeyText, value);
    }

    /// <summary>Opens the vendor tool; the dashboard never writes driver settings itself.</summary>
    public RelayCommand OpenNvidiaToolCommand { get; }

    /// <summary>
    /// True when Nitrous has asked nvtopps for WhisperMode, which is what makes the NVIDIA App offer
    /// its WhisperMode controls. Read from settings rather than this process's own call, because the
    /// tray may be the one that changed the mode. It reflects the last request Nitrous made, not a
    /// live read of driver state.
    /// </summary>
    private bool _whisperModeActive;
    public bool WhisperModeActive { get => _whisperModeActive; set => SetProperty(ref _whisperModeActive, value); }

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
        _fanRampDown = SettingsManager.Get("FanRampDown", FanRamp.Balanced);
        if (Array.IndexOf(FanRamp.Options, _fanRampDown) < 0) _fanRampDown = FanRamp.Balanced;
        _isCurveModeEnabled = SettingsManager.Get("IsCurveModeEnabled", 0) == 1;
        _manageCpuPower = SettingsManager.Get("ManageCpuPower", 0) == 1;

        var savedSpan = SettingsManager.Get("ChartSpanMinutes", 2);
        _chartSpanMinutes = savedSpan is 2 or 5 or 15 ? savedSpan : 2;

        var savedGpuSpan = SettingsManager.Get("GpuChartSpanMinutes", 2);
        _gpuChartSpanMinutes = savedGpuSpan is 2 or 5 or 15 ? savedGpuSpan : 2;

        // GPU clock writes are opt-in. Default off for existing installs so an update never
        // starts writing clocks on a machine that has been tuned by another tool.
        _manageGpuClocks = NvidiaGpuManager.IsClockManagementEnabled();
        _gpuClockStatusText = _manageGpuClocks
            ? "Nitrous manages GPU clocks and the thermal limit. Per-profile values apply at startup, profile switches, AC/DC, resume, and the 30-second watchdog."
            : "Nitrous is not managing GPU clocks. Offsets below are read from the driver and are never written. " +
              "Enable the switch to apply per-profile core offsets, memory offsets and the thermal limit.";

        _deepGpuTelemetry = SettingsManager.Get("DeepGpuTelemetry", 1) == 1;
        if (!_deepGpuTelemetry) ClearDeepTelemetryUI();

        var activeFan = Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile f)
            ? f
            : FanProfile.Auto;
        IsCustomFanEnabled = activeFan == FanProfile.Medium;

        // Initialize settings state.
        _chargeLimit = SettingsManager.Get("ChargeLimit", 0) == 1;
        _autoSwitch = SettingsManager.Get("AutoSwitch", 0) == 1;
        _refreshAutoSwitch = SettingsManager.Get("RefreshAutoSwitch", 0) == 1;

        ActivePowerProfile = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);

        Task.Run(() =>
        {
            var isTurbo = AcerWmiManager.IsTurboModeSupported();
            var nvidia = NvidiaTools.Find();
            var isTaskEnabled = StartupManager.CheckStartupTask();
            var hasClocks = _gpuManager.TryGetClocks(out var core, out var memory, out var clockError);
            if (!hasClocks && !string.IsNullOrEmpty(clockError))
                Logger.Info($"GPU OC: initial clock read failed: {clockError}");

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                IsTurboSupported = isTurbo;
                SettingsManager.Save("TurboModeSupported", isTurbo ? 1 : 0);

                IsNvidiaToolAvailable = nvidia is not null;
                if (nvidia is { } vendorTool) NvidiaToolLabel = vendorTool.Label;

                _runOnStartup = isTaskEnabled;
                OnPropertyChanged(nameof(RunOnStartup));

                // Never overwrite the user's saved offsets with 0 just because the GPU was asleep.
                if (hasClocks)
                {
                    SetGpuOffsets(core, memory);
                }
            });
        });

        // Vendor GPU software is launched, never reimplemented.
        OpenNvidiaToolCommand = new RelayCommand(_ => NvidiaTools.Open());

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

            // Both the CPU cap and the GPU thermal policy are stored per profile; applying the mode is
            // where they are restored.
            CpuPowerCap.Reassert(mode, modeChanged: true);
            GpuThermalTarget.Reassert(mode);
            GpuClockLimit.Reassert(mode);

                // The request lives in settings; show it now rather than on the next sync tick.
                WhisperModeActive = mode == PowerProfile.Quiet &&
                                    SettingsManager.Get("WhisperActive", 0) == 1;

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

        ApplyGpuClocksCommand = new RelayCommand(async _ => await ApplyGpuTuningAsync("apply"));

        ToggleCpuChartCommand = new RelayCommand(_ => ShowCpuChart = !ShowCpuChart);
        ToggleGpuChartCommand = new RelayCommand(_ => ShowGpuChart = !ShowGpuChart);

        ResetGpuClocksCommand = new RelayCommand(async _ =>
        {
            // All four go back to neutral: stock offsets, the driver's own temperature, no clock cap.
            GpuCoreOffset = 0;
            GpuMemoryOffset = 0;
            if (GpuThermalMaximum > 0) GpuThermalLimit = GpuThermalMaximum;
            if (GpuClockMaximum > 0) GpuClockCap = GpuClockMaximum;

            await ApplyGpuTuningAsync("reset");
        });

        RestoreBoostCommand = new RelayCommand(async _ => await RestoreBoostAsync());
        DisableAcerServicesCommand = new RelayCommand(async _ => await DisableAcerServicesAsync());
        RestoreAcerServicesCommand = new RelayCommand(async _ => await RestoreAcerServicesAsync());

        _ = LoadBoostModesAsync();
        _ = RefreshAcerServicesAsync();

        StartTelemetryPolling();
    }

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

    /// <summary>How fast the curve engine may lower the fans; see <see cref="FanRamp"/>.</summary>
    public string[] FanRampOptions => FanRamp.Options;

    private string _fanRampDown = FanRamp.Balanced;
    public string FanRampDown
    {
        get => _fanRampDown;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (SetProperty(ref _fanRampDown, value))
                SettingsManager.Save("FanRampDown", value);
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
    private bool _batteryHealthAvailable;
    public bool BatteryHealthAvailable
    {
        get => _batteryHealthAvailable;
        set => SetProperty(ref _batteryHealthAvailable, value);
    }

    private string _batteryHealthDetail = "";
    public string BatteryHealthDetail
    {
        get => _batteryHealthDetail;
        set => SetProperty(ref _batteryHealthDetail, value);
    }

    /// <summary>Read once on open: design and full-charge capacity do not change while running.</summary>
    public async Task LoadBatteryHealthAsync()
    {
        var health = await Task.Run(SystemInfoManager.GetBatteryHealth);
        if (health is null) return;

        var detail = $"{health.Percent}% \u00b7 " +
                     $"{health.FullChargeCapacityMwh / 1000.0:0.0} Wh of " +
                     $"{health.DesignCapacityMwh / 1000.0:0.0} Wh when new" +
                     (health.CycleCount is { } cycles ? $" \u00b7 {cycles} cycles" : "");

        await RunOnUiAsync(() =>
        {
            BatteryHealthDetail = detail;
            BatteryHealthAvailable = true;
        });
    }

    private bool _chargeLimit;
    public bool ChargeLimit
    {
        get => _chargeLimit;
        set
        {
            if (!SetProperty(ref _chargeLimit, value)) return;

            SettingsManager.Save("ChargeLimit", value ? 1 : 0);
            _ = ApplyChargeLimitAsync(value);
        }
    }

    private string _chargeLimitStatus = "";
    public string ChargeLimitStatus
    {
        get => _chargeLimitStatus;
        set => SetProperty(ref _chargeLimitStatus, value);
    }

    /// <summary>
    /// The EC has no charge-limit readback, so the only honest signal is whether the write was
    /// accepted. The setting stays where the user put it - the tray re-asserts it on the next power
    /// event - and a refusal is shown instead of leaving the toggle looking successful.
    /// </summary>
    private async Task ApplyChargeLimitAsync(bool enable)
    {
        var ok = await AcerWmiManager.SetChargeLimitAsync(enable);
        ChargeLimitStatus = ok
            ? ""
            : enable
                ? "The firmware refused the limit. Connect the charger with the battery below 80%, then try again."
                : "The firmware refused the change; the limit may still be in force.";
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
        GpuStateText = "Deep GPU telemetry is off";
        GpuLoadText = "-- %";
        GpuLoadColor = ThemeManager.TextMutedHex;
        GpuLoadPercent = 0;
        GpuVramShortText = "--";
        GpuVramPercent = 0;
        GpuDeepTempColor = ThemeManager.TextMutedHex;
        GpuTempMeterText = "--";
        GpuTempPercent = 0;
        GpuPStateText = "--";
        GpuCoreClockText = "-- MHz";
        GpuMemClockText = "-- MHz";
        GpuPowerValueText = "-- W";
        GpuPowerLimitText = "";
        GpuPowerFooterText = "";
        GpuAsleep = false;
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
    /// NVML is polled only while a page that shows GPU figures is on screen: the GPU page, and now
    /// the dashboard, which shows GPU usage next to its temperature. Reading NVML wakes the dGPU,
    /// so every other page leaves it alone.
    /// </summary>
    public void SetGpuTelemetryActive(bool active)
    {
        if (_gpuTelemetryActive == active) return;

        _gpuTelemetryActive = active;
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
        if (!_gpuTelemetryActive) return;

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

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    /// <summary>
    /// CPU usage from the idle/total delta between polls. Kernel time includes idle, so the total
    /// is kernel + user. Null on the first call, which only records the baseline.
    /// </summary>
    private int? ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;

        if (!_haveCpuSample)
        {
            _haveCpuSample = true;
            _cpuIdle = idle;
            _cpuKernel = kernel;
            _cpuUser = user;
            return null;
        }

        var idleDelta = idle - _cpuIdle;
        var total = (kernel - _cpuKernel) + (user - _cpuUser);
        _cpuIdle = idle;
        _cpuKernel = kernel;
        _cpuUser = user;

        if (total == 0) return null;
        return (int)Math.Round(100.0 * ((double)total - idleDelta) / total);
    }

    /// <summary>
    /// Appends one timestamped sample. A zero (the EC could not answer) is kept as "no reading" rather
    /// than drawn at the floor, so a sleeping dGPU does not look like a drop to freezing.
    /// </summary>
    private void AppendHistory(int cpuTemp, int gpuTemp, int cpuRpm, int gpuRpm,
        int? cpuUsage, int? gpuUsage, int gpuClock, double gpuPower, double gpuPowerLimit)
    {
        if (cpuTemp <= 0 && gpuTemp <= 0) return;

        lock (_historyGate)
        {
            _history.Add(new HistorySample(
                DateTime.Now,
                cpuTemp,
                Math.Max(0, gpuTemp),
                Math.Max(0, cpuRpm),
                Math.Max(0, gpuRpm),
                Math.Max(0, cpuUsage ?? 0),
                Math.Max(0, gpuUsage ?? 0),
                Math.Max(0, gpuClock),
                Math.Max(0, gpuPower),
                Math.Max(0, gpuPowerLimit)));

            if (_history.Count > HistoryCapacity) _history.RemoveAt(0);
        }
    }

    /// <summary>
    /// Rebuilds the visible window: two frozen line geometries, the axis, the stats and the snapshot
    /// the hover readout binary-searches. Runs once per poll on the UI thread; the work is bounded by
    /// <see cref="HistoryCapacity"/> and nothing is allocated per mouse move.
    /// </summary>
    private void UpdateHistoryChart()
    {
        DateTime end = DateTime.Now;
        TimeSpan span = TimeSpan.FromMinutes(_chartSpanMinutes);
        DateTime begin = end - span;
        bool showCpu = _showCpuChart;
        bool showGpu = _showGpuChart;

        var visible = new List<HistorySample>(_chartSpanMinutes * 30);
        Geometry cpuGeometry;
        Geometry gpuGeometry;
        int yMin, yMax;

        lock (_historyGate)
        {
            foreach (var sample in _history)
                if (sample.At >= begin)
                    visible.Add(sample);

            (yMin, yMax) = ChooseRange(visible, showCpu, showGpu);
            cpuGeometry = BuildLine(visible, begin, span, yMin, yMax, cpu: true);
            gpuGeometry = BuildLine(visible, begin, span, yMin, yMax, cpu: false);
        }

        var samples = new List<ChartSample>(visible.Count);
        foreach (var sample in visible)
        {
            var x = ChartX(sample.At, begin, span);
            samples.Add(new ChartSample(
                x,
                sample.Cpu > 0 ? ChartY(sample.Cpu, yMin, yMax) : double.NaN,
                sample.Gpu > 0 ? ChartY(sample.Gpu, yMin, yMax) : double.NaN,
                sample.At, sample.Cpu, sample.Gpu));
        }

        CpuHistoryGeometry = cpuGeometry;
        GpuHistoryGeometry = gpuGeometry;
        ChartSamples = samples;

        ChartAxisTop = TemperatureDisplay.Axis(yMax);
        ChartAxisMid = TemperatureDisplay.Axis((int)Math.Round((yMin + yMax) / 2.0));
        ChartAxisBottom = TemperatureDisplay.Axis(yMin);

        var half = _chartSpanMinutes / 2.0;
        ChartTimeLeft = $"-{_chartSpanMinutes}m";
        ChartTimeMid = half % 1 == 0
            ? $"-{half:0}m"
            : $"-{(int)half}:{(int)((half % 1) * 60):00}";

        (CpuHistoryStatsText, GpuHistoryStatsText, AuxHistoryStatsText) =
            FormatStats(visible, showCpu, showGpu);

        if (_gpuTelemetryActive) UpdateGpuPowerChart(end);
    }

    /// <summary>
    /// Rebuilds the GPU page's power plot. Only runs while that page is open: power is polled there and
    /// nowhere else, so a closed page is a gap rather than a zero. One pair of geometries per poll.
    /// </summary>
    private void UpdateGpuPowerChart(DateTime end)
    {
        TimeSpan span = TimeSpan.FromMinutes(_gpuChartSpanMinutes);
        DateTime begin = end - span;

        var samples = new List<HistorySample>(_gpuChartSpanMinutes * 30);
        lock (_historyGate)
        {
            foreach (var sample in _history)
                if (sample.At >= begin && sample.GpuPower > 0)
                    samples.Add(sample);
        }

        var limit = samples.Count > 0 ? samples[^1].GpuPowerLimit : 0;
        var maxDraw = 0.0;
        foreach (var sample in samples)
            maxDraw = Math.Max(maxDraw, sample.GpuPower);

        var yMax = Math.Max(20.0, Math.Ceiling(Math.Max(limit, maxDraw * 1.05) / 10.0) * 10.0);

        var points = new List<GpuPowerPoint>(samples.Count);
        foreach (var sample in samples)
            points.Add(new GpuPowerPoint(
                ChartX(sample.At, begin, span),
                PowerY(sample.GpuPower, yMax),
                sample.At,
                sample.GpuPower));

        GpuPowerLineGeometry = BuildPowerGeometry(samples, begin, span, yMax, area: false);
        GpuPowerAreaGeometry = BuildPowerGeometry(samples, begin, span, yMax, area: true);
        GpuPowerSamples = points;
        HasGpuPowerLimit = limit > 0;
        // Keep the 1 px dashed line inside the plot when the limit is the top of the scale.
        GpuPowerLimitY = limit > 0 ? Math.Clamp(PowerY(limit, yMax), 0.5, ChartHeight - 0.5) : ChartHeight;

        var half = _gpuChartSpanMinutes / 2.0;
        GpuChartTimeLeft = $"-{_gpuChartSpanMinutes}m";
        GpuChartTimeMid = half % 1 == 0
            ? $"-{half:0}m"
            : $"-{(int)half}:{(int)((half % 1) * 60):00}";
    }

    private static double PowerY(double watts, double yMax) =>
        Math.Clamp((yMax - watts) / yMax, 0, 1) * ChartHeight;

    /// <summary>
    /// The line, or the same line closed to the baseline for the area fill. A gap longer than
    /// <see cref="ChartGap"/> starts a new figure, so hidden time is a hole.
    /// </summary>
    private static Geometry BuildPowerGeometry(List<HistorySample> samples, DateTime begin,
        TimeSpan span, double yMax, bool area)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var open = false;
            DateTime previous = default;
            double lastX = 0;

            foreach (var sample in samples)
            {
                if (sample.GpuPower <= 0) continue;

                var x = ChartX(sample.At, begin, span);
                var y = PowerY(sample.GpuPower, yMax);

                if (open && sample.At - previous > ChartGap)
                {
                    if (area) context.LineTo(new System.Windows.Point(lastX, ChartHeight), true, false);
                    open = false;
                }

                var point = new System.Windows.Point(x, y);

                if (!open)
                {
                    if (area)
                    {
                        context.BeginFigure(
                            new System.Windows.Point(x, ChartHeight), isFilled: true, isClosed: true);
                        context.LineTo(point, isStroked: true, isSmoothJoin: false);
                    }
                    else
                    {
                        context.BeginFigure(point, isFilled: false, isClosed: false);
                    }

                    open = true;
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }

                if (area) lastX = x;
                previous = sample.At;
            }

            if (area && open)
                context.LineTo(new System.Windows.Point(lastX, ChartHeight), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// A shared, readable range: at least 20 degrees tall, rounded to fives, so the lines use the plot
    /// without turning two degrees of drift into a mountain range.
    /// </summary>
    private static (int Min, int Max) ChooseRange(List<HistorySample> samples, bool cpu, bool gpu)
    {
        var lo = int.MaxValue;
        var hi = int.MinValue;

        foreach (var sample in samples)
        {
            if (cpu && sample.Cpu > 0)
            {
                lo = Math.Min(lo, sample.Cpu);
                hi = Math.Max(hi, sample.Cpu);
            }

            if (gpu && sample.Gpu > 0)
            {
                lo = Math.Min(lo, sample.Gpu);
                hi = Math.Max(hi, sample.Gpu);
            }
        }

        if (lo == int.MaxValue) return (20, 100);

        if (hi - lo < 20)
        {
            // Centre the short range on the data, then anchor a clean 20-degree window to it.
            var mid = (lo + hi) / 2.0;
            lo = (int)(Math.Floor((mid - 10) / 5.0) * 5);
            hi = lo + 20;
        }
        else
        {
            lo = (int)(Math.Floor(lo / 5.0) * 5);
            hi = (int)(Math.Ceiling(hi / 5.0) * 5);
        }

        lo = Math.Max(0, lo);
        hi = Math.Min(115, hi);
        if (hi - lo < 20) lo = Math.Max(0, hi - 20);

        return (lo, hi);
    }

    private static double ChartX(DateTime at, DateTime begin, TimeSpan span) =>
        Math.Clamp((at - begin).TotalSeconds / span.TotalSeconds, 0, 1) * ChartWidth;

    private static double ChartY(int value, int yMin, int yMax) =>
        Math.Clamp((yMax - value) / (double)(yMax - yMin), 0, 1) * ChartHeight;

    /// <summary>
    /// One figure per run of samples. A gap longer than <see cref="ChartGap"/> starts a new figure, so
    /// a window during which the dashboard was hidden is a hole rather than a straight line.
    /// </summary>
    private static Geometry BuildLine(List<HistorySample> samples, DateTime begin, TimeSpan span,
        int yMin, int yMax, bool cpu)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var open = false;
            DateTime previous = default;

            foreach (var sample in samples)
            {
                var value = cpu ? sample.Cpu : sample.Gpu;
                if (value <= 0)
                {
                    open = false;
                    continue;
                }

                if (open && sample.At - previous > ChartGap) open = false;

                var point = new System.Windows.Point(
                    ChartX(sample.At, begin, span),
                    ChartY(value, yMin, yMax));

                if (open)
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                else
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    open = true;
                }

                previous = sample.At;
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static (string Cpu, string Gpu, string Aux) FormatStats(
        List<HistorySample> samples, bool showCpu, bool showGpu)
    {
        var cpu = showCpu ? Range(samples, cpu: true) : "-";
        var gpu = showGpu ? Range(samples, cpu: false) : "-";

        var cpuRpm = 0;
        var gpuRpm = 0;
        var cpuLoad = 0;
        var gpuLoad = 0;

        foreach (var sample in samples)
        {
            cpuRpm = Math.Max(cpuRpm, sample.CpuRpm);
            gpuRpm = Math.Max(gpuRpm, sample.GpuRpm);
            cpuLoad = Math.Max(cpuLoad, sample.CpuUsage);
            gpuLoad = Math.Max(gpuLoad, sample.GpuUsage);
        }

        var aux = new List<string>(2);
        if (cpuRpm > 0) aux.Add(gpuRpm > 0 ? $"Fans {cpuRpm}/{gpuRpm} RPM" : $"Fans {cpuRpm} RPM");
        if (cpuLoad > 0) aux.Add(gpuLoad > 0 ? $"Load {cpuLoad}/{gpuLoad}%" : $"Load {cpuLoad}%");

        return ($"CPU {cpu}", $"GPU {gpu}", string.Join(" \u00b7 ", aux));
    }

    private static string Range(List<HistorySample> samples, bool cpu)
    {
        var lo = int.MaxValue;
        var hi = int.MinValue;
        long total = 0;
        var count = 0;

        foreach (var sample in samples)
        {
            var value = cpu ? sample.Cpu : sample.Gpu;
            if (value <= 0) continue;

            lo = Math.Min(lo, value);
            hi = Math.Max(hi, value);
            total += value;
            count++;
        }

        return count == 0
            ? "--"
            : $"{TemperatureDisplay.Convert(lo)}\u2013{TemperatureDisplay.Convert(hi)} \u00b7 " +
              $"avg {TemperatureDisplay.Convert((int)Math.Round((double)total / count))} {TemperatureDisplay.Unit}";
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
                        if (DeepGpuTelemetry && _gpuTelemetryActive)
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
                        var cpuUsage = ReadCpuUsage();
                        _telemetryErrorLogged = false;

                        // The EC reports 0 for the dGPU on some models, and while it sits in a low
                        // power state. Prefer whatever the driver already answered this tick, and
                        // only ask NVML for a second reading when it was not polled at all - the
                        // earlier order meant an enabled Deep GPU Telemetry suppressed the fallback
                        // it was meant to feed, leaving the dashboard on "-- C".
                        var driverGpuTemp = smi is { HasData: true, CoreTemp: > 0 } ? smi.CoreTemp : 0;
                        if (telemetry.GpuTemp == 0 && driverGpuTemp == 0 && _gpuManager.IsGpuAwake)
                        {
                            var fallback = await _gpuManager.GetNvmlTelemetryAsync(token);
                            if (fallback is { HasData: true, CoreTemp: > 0 })
                                driverGpuTemp = fallback.CoreTemp;
                        }

                        // Only meaningful when the EC is answering at all: an unelevated or failed
                        // read returns 0 for the CPU too, and that is not this model`s quirk.
                        if (telemetry.GpuTemp == 0 && telemetry.CpuTemp > 0 && driverGpuTemp > 0 &&
                            !_gpuTempFromDriverLogged)
                        {
                            // One line per session: if this model always needs it, the number is
                            // coming from the driver and a diagnostics bundle should say so.
                            _gpuTempFromDriverLogged = true;
                            Logger.Info($"GPU temp: the EC reports 0; using the driver's {driverGpuTemp} C.");
                        }

                        var chartCpuTemp = telemetry.CpuTemp;
                        var chartGpuTemp = telemetry.GpuTemp > 0 ? telemetry.GpuTemp : driverGpuTemp;
                        AppendHistory(chartCpuTemp, chartGpuTemp, telemetry.CpuRpm, telemetry.GpuRpm,
                            cpuUsage, smi is { HasData: true } ? smi.GpuLoad : null,
                            smi is { HasData: true } ? smi.CurrentCoreClock : 0,
                            smi is { HasData: true } ? smi.PowerDrawW : 0,
                            smi is { HasData: true } ? smi.EnforcedPowerLimitW : 0);

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

                            CpuTempText = TemperatureDisplay.Reading(telemetry.CpuTemp);
                            CpuRpmText = telemetry.CpuRpm > 0 ? $"{telemetry.CpuRpm} RPM" : "-- RPM";
                            CpuUsageText = cpuUsage is { } usage ? $"{usage}%" : "--%";
                            CpuTempColor = telemetry.CpuTemp > 85 ? ThemeManager.DangerHex : ThemeManager.TextPrimaryHex;

                            var gpuTemp = chartGpuTemp;
                            GpuTempText = TemperatureDisplay.Reading(gpuTemp);
                            GpuRpmText = telemetry.GpuRpm > 0 ? $"{telemetry.GpuRpm} RPM" : "-- RPM";
                            GpuUsageText = smi is { HasData: true } ? $"{smi.GpuLoad}%" : "--%";
                            GpuTempColor = gpuTemp > 85 ? ThemeManager.DangerHex : ThemeManager.TextPrimaryHex;

                            UpdateHistoryChart();

                            GpuAsleep = gpuAsleep;
                            GpuStateText = smi is { HasData: true } state
                                ? $"{state.PState} · awake"
                                : gpuAsleep ? "Asleep · D3" : "Waiting for the driver";

                            if (smi is { HasData: true } gpuData)
                            {
                                GpuNameText = FormatGpuName(gpuData.Name);
                                GpuArchText = gpuData.Architecture;

                                GpuLoadText = $"{gpuData.GpuLoad}%";
                                GpuLoadPercent = Math.Clamp(gpuData.GpuLoad, 0, 100);
                                GpuLoadColor = gpuData.GpuLoad >= 95 ? ThemeManager.DangerHex : (gpuData.GpuLoad >= 80 ? ThemeManager.WarnHex : ThemeManager.TextPrimaryHex);

                                var target = GpuThermalTargetNow > 0
                                    ? GpuThermalTargetNow
                                    : GpuThermalMaximum > 0 ? GpuThermalMaximum : 87;
                                GpuTempMeterText = $"{TemperatureDisplay.Convert(gpuData.CoreTemp)} / " +
                                                   $"{TemperatureDisplay.Convert(target)} {TemperatureDisplay.Unit}";
                                GpuTempPercent = Math.Clamp(100.0 * gpuData.CoreTemp / target, 0, 100);

                                GpuVramShortText = gpuData.VramTotalMb > 0
                                    ? $"{gpuData.VramUsedMb / 1024.0:0.0} / {gpuData.VramTotalMb / 1024.0:0.0} GB"
                                    : "--";
                                GpuVramPercent = gpuData.VramTotalMb > 0
                                    ? Math.Clamp(100.0 * gpuData.VramUsedMb / gpuData.VramTotalMb, 0, 100)
                                    : 0;

                                GpuDeepTempColor = gpuData.CoreTemp >= 85 ? ThemeManager.DangerHex : (gpuData.CoreTemp >= 78 ? ThemeManager.WarnHex : ThemeManager.TextPrimaryHex);

                                GpuPStateText = gpuData.PState;
                                GpuCoreClockText = $"{gpuData.CurrentCoreClock} MHz";
                                GpuMemClockText = $"{gpuData.CurrentMemoryClock} MHz";

                                GpuPowerValueText = $"{gpuData.PowerDrawW:0.0} W";
                                GpuPowerLimitText = gpuData.EnforcedPowerLimitW > 0 ? $"/ {gpuData.EnforcedPowerLimitW:0} W" : "";
                                GpuPowerFooterText = gpuData.EnforcedPowerLimitW > 0
                                    ? gpuData.MaxPowerLimitW > 0
                                        ? $"{gpuData.EnforcedPowerLimitW:0} W enforced · {gpuData.MaxPowerLimitW:0} W max"
                                        : $"{gpuData.EnforcedPowerLimitW:0} W enforced"
                                    : "";
                            }
                            else
                            {
                                // No NVML data. The GPU is either in D3 (expected, and Nitrous
                                // deliberately does not wake it) or NVML is not reporting yet.
                                GpuLoadText = "--";
                                GpuLoadPercent = 0;
                                GpuVramShortText = "--";
                                GpuVramPercent = 0;
                                GpuTempMeterText = "--";
                                GpuTempPercent = 0;
                                GpuPStateText = gpuAsleep ? "Asleep" : "--";
                                GpuCoreClockText = "-- MHz";
                                GpuMemClockText = "-- MHz";
                                GpuPowerValueText = "-- W";
                                GpuPowerLimitText = "";
                                GpuPowerFooterText = "";
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
