using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;
using Nitrous.Managers;
using Nitrous.Ui.Theme;

using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Nitrous.Ui;

public partial class NitrousDashboard : Window
{
    private bool _isDialogOpen;
    private bool _isDiagnosticsExporting;
    private bool _isEcProbing;
    private readonly DispatcherTimer _profileSyncTimer;
    private PowerProfile? _lastSyncedPowerMode;
    private FanProfile? _lastSyncedFanMode;
    private RefreshMode? _lastSyncedRefreshMode;
    private int _lastSyncedRefreshHz;
    private bool? _lastSyncedSyncEnabled;
    private string _lastSyncedSyncSource = "";
    private KeyboardEffect? _lastSyncedGlobalEffect;
    private string _lastSyncedAreaStyle = "";
    private KeyboardEffect? _lastSyncedAreaEffect;
    private (PowerProfile Mode, bool OnBattery)? _powerLimitsKey;
    private bool _cpuPowerCapUpdating;
    private string? _cpuPowerCapMessage;

    /// <summary>What this mode asks for, as read on the last refresh; the sliders' top and RESET land here.</summary>
    private CpuPowerCap.Limits? _cpuPowerCeiling;
    private bool _cpuLimitsPreview;

    private bool _gpuLimitsReading;
    private bool _gpuMuxReading;
    private bool _uiScaleLoading;
    private bool _dashboardOptionsLoading;
    private bool _appProfilesLoading;
    private static readonly AppFanMode[] FanChoices =
        [AppFanMode.Leave, AppFanMode.Auto, AppFanMode.Max, AppFanMode.Custom];
    private List<AppProfileRule> _appRules = new();
    private sealed record RunningApp(string Executable, string Title);
    private Nitrous.Enums.PowerProfile _cpuPowerProfile;

    /// <summary>The effect lists as offered on this machine: a five-effect controller hides the last two.</summary>
    private (string Name, KeyboardEffect Effect)[] _keyboardEffects = KeyboardEffects;
    private (string Name, KeyboardEffect Effect)[] _dynamicEffects = DynamicEffects;

    public NitrousDashboard()
    {
        InitializeComponent();

        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        InitializeAppearanceCustomizations();
        BuildAppearanceSwatches();
        BuildFontChoices();
        BuildOsdColourSwatches();

        // Shown unless it has been turned off; read before the binding exists so the toggle cannot
        // write a default back over the saved choice.
        var showScrollbars = SettingsManager.Get("ShowScrollbars", 1) == 1;
        DataContext = new DashboardViewModel
        {
            ShowScrollbars = showScrollbars
        };
        if (DataContext is DashboardViewModel gpuVm) gpuVm.GpuTuningApplied += RefreshGpuThermalTarget;
        ApplyScrollbarMode(showScrollbars);
        Loaded += (_, _) => ApplyScrollbarMode(SettingsManager.Get("ShowScrollbars", 1) == 1);
        InitializeAppProfiles();

        _setupTabLoading = true;
        SelectSetupTab(SettingsManager.Get("SetupTab", "Power"));
        _setupTabLoading = false;

        // The tray process owns the global hotkey and writes the active profile to
        // settings. Poll that cross-process state so the dashboard follows hotkey,
        // auto-switch, and AC/DC changes without being reopened.
        _profileSyncTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _profileSyncTimer.Tick += (_, _) => SyncProfileFromSettings();
        _profileSyncTimer.Start();

        DashVersionText.Text = GpuVersionText.Text = KeyboardVersionText.Text = SettingsVersionText.Text =
            $"Nitrous {UpdateManager.CurrentVersion}";

        _autoOffRearmTimer.Tick += (_, _) => RearmKeyboardAutoOff();
        _fnBrightnessTimer.Tick += (_, _) => FlushFnBrightness();

        InitializeKeyboardLighting();
        InitializeStickyKeys();
        InitializeUsbCharging();
        InitializeLcdOverdrive();
        InitializeBootLogo();

        _keepAwakeLoading = true;
        BtnKeepAwake.IsChecked = SettingsManager.Get("KeepAwake", 0) == 1;
        _keepAwakeLoading = false;

        // A cap saved by an older build's single-value model belongs to the active profile now; the
        // ceiling that bounds it is learned per profile and power source when the card refreshes.
        CpuPowerCap.MigrateLegacyCap(ActiveProfile());
        PrepareCpuPowerLimits();
        RefreshGpuThermalTarget();
        RefreshCpuPowerLimits((PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance));
        InitializeCoolBoost();

        if (DataContext is DashboardViewModel batteryVm)
            _ = batteryVm.LoadBatteryHealthAsync();

        System.Threading.Tasks.Task.Run(() =>
        {
            var modelName = SystemInfoManager.GetSystemModel();

            // Reads as a title rather than a bare asset tag: "The Nitro AN16S-61". A machine whose
            // model cannot be read keeps the neutral placeholder instead of "The Unknown System".
            var title = string.IsNullOrWhiteSpace(modelName) ||
                        modelName.Equals("Unknown System", StringComparison.OrdinalIgnoreCase)
                ? modelName
                : $"The {modelName}";

            Dispatcher.InvokeAsync(() => SystemModelText.Text = title);
        });

        SystemEvents.PowerModeChanged += OnPowerStateChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        StateChanged += Window_StateChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // A monitor was added, removed, or rearranged. Let the compositor settle, then make
        // sure the saved position still has a reachable title bar.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(EnsureWindowVisible));
    }

    private void OnPowerStateChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.StatusChange or PowerModes.Resume)) return;

        // Two passes: the first keeps the window current straight after wake, the second lands after the
        // tray has re-applied its hardware state (6 s settle plus its own 1.5 s), because reading before
        // that shows the firmware's value and then the cap appears later with no visible reason.
        foreach (var delay in new[] { 5500, 11000 })
        {
            System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.InvokeAsync(() =>
                {
                    RefreshDashboardState();
                    if (_powerLimitsKey is { } key) RefreshCpuPowerLimits(key.Mode, force: true);
                });
            });
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (DataContext is not DashboardViewModel vm) return;

        // The dashboard is a transient UI process; stop hammering WMI/NVML when minimized.
        vm.SetTelemetryEnabled(WindowState != WindowState.Minimized && IsVisible);
        UpdateGpuTelemetryScope();

        // A hotkey may have changed the profile while the window was minimized.
        if (WindowState != WindowState.Minimized && IsVisible)
            RefreshDashboardState();
    }

    private void SyncProfileFromSettings()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || DataContext is not DashboardViewModel)
            return;

        // The tray, a hotkey or another process may have asked for (or released) WhisperMode
        // without changing the mode, fan or refresh. That request is a registry flag, so follow
        // it every tick rather than only when one of the other values moves. The label only ever
        // claims On in Quiet: a stale request must not follow a mode that is no longer Quiet.
        var activeMode = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
        var whisperActive = activeMode == PowerProfile.Quiet &&
                            SettingsManager.Get("WhisperActive", 0) == 1;
        if (DataContext is DashboardViewModel whisperVm && whisperVm.WhisperModeActive != whisperActive)
            whisperVm.WhisperModeActive = whisperActive;

        UpdateAppProfileActiveText();

        var activeFan = Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile fan)
            ? fan
            : FanProfile.Auto;
        var activeRefresh = DisplayManager.GetMode();

        SyncLightingFromSettings();

        var modeChanged = activeMode != _lastSyncedPowerMode;

        if (!modeChanged &&
            activeFan == _lastSyncedFanMode &&
            activeRefresh == _lastSyncedRefreshMode &&
            DisplayManager.GetCurrentRefreshRate() == _lastSyncedRefreshHz)
            return;

        RefreshCpuPowerLimits(activeMode);

        // The GPU's limit cards show driver state, not the saved setting, so a mode change while the page
        // is open has to refresh them - after the tray has had time to re-assert the new profile's values.
        if (modeChanged && _activeNavIndex == 1)
        {
            RefreshGpuThermalTarget();
            _ = Task.Delay(2200).ContinueWith(_ =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.InvokeAsync(RefreshGpuThermalTarget);
            });
        }

        RefreshDashboardState();
    }

    /// <summary>
    /// Follows sync and effect changes made by the tray hotkeys. The UI is updated without applying
    /// or saving anything; the tray already did the hardware write.
    /// </summary>
    private void SyncLightingFromSettings()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || DataContext is not DashboardViewModel)
            return;

        var enabled = SettingsManager.Get("KbSyncEnabled", 0) == 1;
        var source = SettingsManager.Get("KbSyncSource", LightingSync.SourceScreen);
        var globalEffect = AcerKeyboard.ParseEffect(SettingsManager.Get("KbGlobal_Effect", "Static"));
        var areaStyle = SettingsManager.Get("KbStyle", "Static");
        var areaEffect = AcerKeyboard.ParseEffect(SettingsManager.Get("KbEffect", "Breathing"));

        var changed = _lastSyncedSyncEnabled != enabled
            || !string.Equals(_lastSyncedSyncSource, source, StringComparison.Ordinal)
            || _lastSyncedGlobalEffect != globalEffect
            || !string.Equals(_lastSyncedAreaStyle, areaStyle, StringComparison.OrdinalIgnoreCase)
            || _lastSyncedAreaEffect != areaEffect;

        _lastSyncedSyncEnabled = enabled;
        _lastSyncedSyncSource = source;
        _lastSyncedGlobalEffect = globalEffect;
        _lastSyncedAreaStyle = areaStyle;
        _lastSyncedAreaEffect = areaEffect;

        if (!changed) return;

        _syncToggleUpdating = true;
        try
        {
            _syncEnabled = enabled;
            _syncSource = source;
            TogScreenSync.IsChecked = enabled &&
                string.Equals(source, LightingSync.SourceScreen, StringComparison.OrdinalIgnoreCase);
            TogSoundSync.IsChecked = enabled &&
                string.Equals(source, LightingSync.SourceSound, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _syncToggleUpdating = false;
        }

        var wasLoading = _keyboardLoading;
        _keyboardLoading = true;
        try
        {
            var globalIndex = Array.FindIndex(_keyboardEffects, item => item.Effect == globalEffect);
            if (globalIndex >= 0 && GlobalEffectBox.SelectedIndex != globalIndex)
                GlobalEffectBox.SelectedIndex = globalIndex;
            _globalEffect = globalEffect;

            var areaIndex = Array.FindIndex(_dynamicEffects, item => item.Effect == areaEffect);
            if (areaIndex >= 0 && KeyboardEffectBox.SelectedIndex != areaIndex)
                KeyboardEffectBox.SelectedIndex = areaIndex;
            _keyboardEffect = areaEffect;

            if (string.Equals(areaStyle, "Dynamic", StringComparison.OrdinalIgnoreCase))
                KbDynamicTab.IsChecked = true;
            else
                KbStaticTab.IsChecked = true;
        }
        finally
        {
            _keyboardLoading = wasLoading;
        }

        if (_keyboardBrightnessAvailable)
        {
            _brightnessSyncUpdating = true;
            try
            {
                TogBrightnessSync.IsChecked = enabled &&
                    string.Equals(source, LightingSync.SourceSound, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                _brightnessSyncUpdating = false;
            }

            UpdateBrightnessSyncUi();
        }

        UpdateLightingInteractivity();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private int _hoverSampleIndex = -1;

    /// <summary>
    /// Nearest sample under the pointer. The overlay only moves when the sample changes, so a mouse
    /// sweep costs one binary search per sample column instead of a rebuild.
    /// </summary>
    private void TemperaturePlot_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
            UpdateTemperatureHover(vm, e.GetPosition(TemperaturePlot).X);
    }

    private void UpdateTemperatureHover(DashboardViewModel vm, double x)
    {
        var samples = vm.ChartSamples;
        if (samples.Count == 0)
        {
            ClearTemperatureHover();
            return;
        }

        var low = 0;
        var high = samples.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (samples[mid].X < x) low = mid + 1;
            else high = mid;
        }

        var index = low;
        if (index > 0 && Math.Abs(samples[index - 1].X - x) < Math.Abs(samples[index].X - x))
            index--;

        if (index == _hoverSampleIndex) return;
        _hoverSampleIndex = index;

        var sample = samples[index];
        var column = Math.Round(sample.X) + 0.5;

        TemperatureHoverLine.X1 = column;
        TemperatureHoverLine.X2 = column;
        TemperatureHoverLine.Visibility = Visibility.Visible;

        var cpu = vm.ShowCpuChart && sample.Cpu > 0;
        if (cpu)
        {
            Canvas.SetLeft(TemperatureHoverCpuDot, sample.X - 3);
            Canvas.SetTop(TemperatureHoverCpuDot, sample.CpuY - 3);
        }
        TemperatureHoverCpuDot.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;

        var gpu = vm.ShowGpuChart && sample.Gpu > 0;
        if (gpu)
        {
            Canvas.SetLeft(TemperatureHoverGpuDot, sample.X - 3);
            Canvas.SetTop(TemperatureHoverGpuDot, sample.GpuY - 3);
        }
        TemperatureHoverGpuDot.Visibility = gpu ? Visibility.Visible : Visibility.Collapsed;

        var seconds = Math.Max(0, (int)Math.Round((DateTime.Now - sample.At).TotalSeconds));
        var when = seconds < 60 ? $"-{seconds} s" : $"-{seconds / 60}:{seconds % 60:00}";

        var text = cpu ? $"{when} \u00b7 CPU {TemperatureDisplay.Short((int)sample.Cpu)}" : when;
        if (gpu) text += $" \u00b7 GPU {TemperatureDisplay.Short((int)sample.Gpu)}";

        TemperatureHoverText.Text = text;
        TemperatureHoverText.Visibility = Visibility.Visible;
        TemperatureStats.Visibility = Visibility.Collapsed;
    }

    private void TemperaturePlot_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ClearTemperatureHover();

    private void ClearTemperatureHover()
    {
        _hoverSampleIndex = -1;
        TemperatureHoverLine.Visibility = Visibility.Collapsed;
        TemperatureHoverCpuDot.Visibility = Visibility.Collapsed;
        TemperatureHoverGpuDot.Visibility = Visibility.Collapsed;
        TemperatureHoverText.Visibility = Visibility.Collapsed;
        TemperatureStats.Visibility = Visibility.Visible;
    }

    /// <summary>Capture-only: a capture has no mouse, so the readout is placed by fraction of width.</summary>
    internal void ShowTemperatureHoverForCapture(double fraction)
    {
        if (DataContext is DashboardViewModel vm)
            UpdateTemperatureHover(vm, Math.Clamp(fraction, 0, 1) * 288);
    }

    private int _gpuHoverSampleIndex = -1;

    private void GpuPowerPlot_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
            UpdateGpuPowerHover(vm, e.GetPosition(GpuPowerPlot).X);
    }

    private void UpdateGpuPowerHover(DashboardViewModel vm, double x)
    {
        var samples = vm.GpuPowerSamples;
        if (samples.Count == 0)
        {
            ClearGpuPowerHover();
            return;
        }

        var low = 0;
        var high = samples.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (samples[mid].X < x) low = mid + 1;
            else high = mid;
        }

        var index = low;
        if (index > 0 && Math.Abs(samples[index - 1].X - x) < Math.Abs(samples[index].X - x))
            index--;

        if (index == _gpuHoverSampleIndex) return;
        _gpuHoverSampleIndex = index;

        var sample = samples[index];
        var column = Math.Round(sample.X) + 0.5;

        GpuPowerHoverLine.X1 = column;
        GpuPowerHoverLine.X2 = column;
        GpuPowerHoverLine.Visibility = Visibility.Visible;

        Canvas.SetLeft(GpuPowerHoverDot, sample.X - 3);
        Canvas.SetTop(GpuPowerHoverDot, sample.Y - 3);
        GpuPowerHoverDot.Visibility = Visibility.Visible;

        var seconds = Math.Max(0, (int)Math.Round((DateTime.Now - sample.At).TotalSeconds));
        var when = seconds < 60 ? $"-{seconds} s" : $"-{seconds / 60}:{seconds % 60:00}";

        GpuPowerHoverText.Text = $"{when} \u00b7 {sample.Watts:0.0} W";
        GpuPowerHoverText.Visibility = Visibility.Visible;
        GpuPowerValuePanel.Visibility = Visibility.Collapsed;
    }

    private void GpuPowerPlot_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        ClearGpuPowerHover();

    private void ClearGpuPowerHover()
    {
        _gpuHoverSampleIndex = -1;
        GpuPowerHoverLine.Visibility = Visibility.Collapsed;
        GpuPowerHoverDot.Visibility = Visibility.Collapsed;
        GpuPowerHoverText.Visibility = Visibility.Collapsed;
        GpuPowerValuePanel.Visibility = Visibility.Visible;
    }

    /// <summary>Capture-only: place the power readout by fraction of the plot width.</summary>
    internal void ShowGpuPowerHoverForCapture(double fraction)
    {
        if (DataContext is DashboardViewModel vm)
            UpdateGpuPowerHover(vm, Math.Clamp(fraction, 0, 1) * 288);
    }

    /// <summary>Capture-only: render the sleeping state the hardware cannot be asked to enter.</summary>
    internal void ShowGpuAsleepForCapture()
    {
        if (DataContext is DashboardViewModel vm)
        {
            vm.GpuAsleep = true;
            vm.GpuStateText = "Asleep · D3";
        }
    }

    /// <summary>
    /// Capture-only: the two MUX states. A capture runs without the elevation the WMI read needs, and
    /// a DDS machine hides the card by design, so the layouts are rendered from the same state the
    /// real read produces.
    /// </summary>
    internal void ShowGpuMuxPreviewForCapture(bool dds)
    {
        if (DataContext is not DashboardViewModel vm) return;

        vm.LoadGpuMux(
            dds
                ? new GpuMuxState(Mask: 7, Mode: (byte)GpuMuxMode.Automatic)
                : new GpuMuxState(Mask: 3, Mode: (byte)GpuMuxMode.Hybrid),
            ddsSupported: dds);

        UpdateLayout();
        (dds ? GpuDdsCard : GpuMuxCard).BringIntoView();
    }

    private int _activeNavIndex;

    /// <summary>
    /// Switches to a page by name for <see cref="UiCapture"/>. Kept on the window rather than made
    /// public API because it is only for offscreen rendering. A Lighting name may also carry
    /// "area", "logo" or "dynamic" to open those tabs, so each half of the page can be rendered
    /// without clicking through it. Any page name may end in "-bottom" to render its lower half.
    /// </summary>
    internal void SelectPageForCapture(string page)
    {
        // Captures are headless; a modal capability question would block the render.
        _captureMode = true;

        // The card is hidden on this machine (AMD), so its Intel-only states cannot be reached here
        // without an override. "setup-limits" previews the not-installed state; "setup-limits-card"
        // fills the card with representative numbers so the site shot can show the sliders.
        var requested = page?.ToLowerInvariant() ?? string.Empty;
        if (requested.Contains("limits-card")) ShowCpuPowerCapPreview();
        else if (requested.Contains("limits")) ShowCpuPowerLimitsInstallNeeded();

        var key = requested;

        // Renders the three-tab navigation a model without a lighting controller would get.
        if (key.Contains("nolight"))
        {
            _keyboardAvailable = false;
            NavKeyboardBtn.Visibility = Visibility.Collapsed;
            NavKeyboardColumn.Width = new GridLength(0);
            SetActiveNav(0);
            return;
        }

        var bottom = key.EndsWith("-bottom");
        if (bottom) key = key[..^"-bottom".Length];

        if (key.StartsWith("setup"))
        {
            SelectSetupTab(
                key.Contains("system") ? "System"
                : key.Contains("keys") || key.Contains("keyboard") || key.Contains("hotkey") ? "Keyboard"
                : key.Contains("appearance") || key.Contains("theme") ? "Appearance"
                : "Power");
        }

        var index = key switch
        {
            _ when key.StartsWith("gpu") => 1,
            "kb" or "keyboard" => 2,
            "setup" or "settings" or "setup-hotkeys" or "setup-limits" => 3,
            _ => 0
        };

        // Any setup-<category> token is the SETUP page; the category decides which tab.
        if (key.StartsWith("setup")) index = 3;

        // "lighting-area", "lighting-logo", "lighting-area-dynamic" all open the same page.
        if (key.StartsWith("lighting")) index = 2;

        SetActiveNav(index);

        if (key == "gpu-thermal")
        {
            UpdateLayout();
            RefreshGpuThermalTarget();
            GpuThermalSlider.BringIntoView();
        }

        if (key == "gpu-clock")
        {
            UpdateLayout();
            RefreshGpuThermalTarget();
            GpuClockLimitSlider.BringIntoView();
        }

        if (key == "setup-hotkeys")
        {
            UpdateLayout();
            TxtHkCycle.BringIntoView();
        }

        if (requested.Contains("limits-card"))
        {
            // The pinned tabs sit outside the scroller, so scrolling the card to the top of the
            // viewport - not merely into view - is what puts both sliders in the frame.
            UpdateLayout();
            if (SettingsScroller.Content is FrameworkElement content)
            {
                var top = CpuPowerLimitsCard.TransformToAncestor(content)
                    .Transform(new System.Windows.Point(0, 0)).Y;
                SettingsScroller.ScrollToVerticalOffset(Math.Max(0, top - 8));
            }
        }

        if (key.StartsWith("lighting"))
        {
            if (key.Contains("area")) LightingAreaTab.IsChecked = true;
            if (key.Contains("global")) LightingGlobalTab.IsChecked = true;
            if (key.Contains("logo")) AreaLogoTab.IsChecked = true;
            if (key.Contains("dynamic")) KbDynamicTab.IsChecked = true;
            if (key.Contains("static")) KbStaticTab.IsChecked = true;

            // Renders the single-colour keyboard layout that SMBIOS hosts report; this machine
            // reports four zones, so it cannot be reached without forcing it.
            if (key.Contains("single"))
            {
                _keyboardZoned = false;
                UpdateZoneInteractivity();
            }
        }

        if (bottom)
        {
            // The scroll extent only exists after a layout pass, and the settle timer runs after it.
            UpdateLayout();
            var scroller = index switch
            {
                1 => GpuPage,
                2 => KeyboardPage,
                3 => SettingsScroller,
                _ => DashPage
            };
            scroller.ScrollToEnd();
        }
    }

    private void SetActiveNav(int index)
    {        _activeNavIndex = index;

        var pages = new UIElement[] { DashPage, GpuPage, KeyboardPage, SettingsPage };

        for (var i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        ApplyNavColours();

        UpdateGpuTelemetryScope();

        // Entering the GPU page should show live values immediately rather than the previous
        // page's last snapshot.
        if (index == 1 && DataContext is DashboardViewModel vm)
        {
            vm.RefreshGpuOffsets();

            // Opening the GPU page is what makes the thermal card readable: reading the policy is what
            // would wake a sleeping GPU, so it is only read once the page that needs it is shown.
            RefreshGpuThermalTarget();
            RefreshGpuMux();
        }

        // The LIGHT page is where the Fn hint belongs; read the EC only when it is shown.
        if (index == 2)
            SyncFnBrightnessHint();

        // First visit to Lighting on a model whose lid logo has never been confirmed: settle it
        // before any more writes are sent to device 0x83.
        if (index == 2 && !_keyboardLoading && !_captureMode && _keyboardAvailable &&
            AcerKeyboard.IsAvailable() && LogoAvailability < 0)
            Dispatcher.BeginInvoke(new Action(RunLogoCheck), DispatcherPriority.Background);
    }

    /// <summary>
    /// Separated from <see cref="SetActiveNav"/> so a theme change can repaint the rail without
    /// re-running the page-entry side effects.
    /// </summary>
    private void ApplyNavColours()
    {
        var icons = new Shape[] { NavDashIcon, NavGpuIcon, NavKeyboardIcon, NavSetIcon };
        var labels = new TextBlock[] { NavDashText, NavGpuText, NavKeyboardText, NavSetText };

        // Resolved from the palette rather than hard-coded so the rail follows the accent.
        var activeBrush = (Brush)FindResource("AccentBrush");
        var inactiveBrush = (Brush)FindResource("TextMutedBrush");

        for (var i = 0; i < icons.Length; i++)
        {
            var brush = i == _activeNavIndex ? activeBrush : inactiveBrush;
            icons[i].Fill = brush;
            labels[i].Foreground = brush;
        }
    }

    /// <summary>
    /// Named combinations of the three palette choices. "Custom" is derived: the list shows it
    /// whenever the current accent, background and card match none of these.
    /// </summary>
    private static readonly (string Name, string Accent, ThemePalette.ThemeSurface Surface, ThemePalette.ThemeCard Card)[] DarkThemePresets =
    [
        ("Nitrous", "#B388FF", ThemePalette.ThemeSurface.Midnight, ThemePalette.ThemeCard.Default),
        ("Ice", "#2FD4E6", ThemePalette.ThemeSurface.Slate, ThemePalette.ThemeCard.Default),
        ("Ember", "#FF8A3D", ThemePalette.ThemeSurface.Graphite, ThemePalette.ThemeCard.Default),
        ("Mono", "#C8C8D0", ThemePalette.ThemeSurface.Black, ThemePalette.ThemeCard.Default)
    ];

    private static readonly (string Name, string Accent, ThemePalette.ThemeSurface Surface, ThemePalette.ThemeCard Card)[] LightThemePresets =
    [
        ("Daylight", "#B388FF", ThemePalette.ThemeSurface.Paper, ThemePalette.ThemeCard.Default),
        ("Frost", "#5B9DFF", ThemePalette.ThemeSurface.Cool, ThemePalette.ThemeCard.White),
        ("Sand", "#FFB020", ThemePalette.ThemeSurface.Warm, ThemePalette.ThemeCard.Default),
        ("Mono", "#C8C8D0", ThemePalette.ThemeSurface.White, ThemePalette.ThemeCard.White)
    ];

    private static (string Name, string Accent, ThemePalette.ThemeSurface Surface, ThemePalette.ThemeCard Card)[] ActivePresets =>
        ThemeManager.IsLight ? LightThemePresets : DarkThemePresets;

    /// <summary>
    /// Builds the accent row from the palette instead of hand-written XAML: the selected ring has
    /// to track a runtime value, and eight near-identical blocks are easy to get wrong by hand.
    /// </summary>
    private void BuildAppearanceSwatches()
    {
        AccentSwatchRow.Children.Clear();
        AccentSwatchRow.Children.Add(CreateSwatch(ThemeManager.Auto, "Match the Windows accent", null));

        foreach (var (name, hex) in ThemePalette.Choices)
            AccentSwatchRow.Children.Add(CreateSwatch(hex, name, ThemePalette.Parse(hex)));

        AccentSwatchRow.Children.Add(CreateCustomSwatch());

        RefreshAppearanceSelection();
    }

    /// <summary>
    /// "Pick any colour". A fixed spectrum rather than the current choice, because the row is only
    /// rebuilt on theme change; the selection ring and the rest of the window show what is in use.
    /// </summary>
    private Button CreateCustomSwatch()
    {
        var inner = new Border
        {
            Width = 22,
            Height = 22,
            Background = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0xFF, 0x45, 0x3A), 0.0),
                    new GradientStop(Color.FromRgb(0xFF, 0x9F, 0x0A), 0.25),
                    new GradientStop(Color.FromRgb(0x34, 0xC7, 0x59), 0.5),
                    new GradientStop(Color.FromRgb(0x32, 0xAD, 0xE6), 0.75),
                    new GradientStop(Color.FromRgb(0xBF, 0x5A, 0xF5), 1.0)
                }
            }
        };

        var ring = new Border
        {
            Width = 28,
            Height = 28,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Child = inner
        };

        // Resource references, not captured values: the swatches must follow the Square corners
        // toggle while the window is open, not only when they are created.
        inner.SetResourceReference(Border.CornerRadiusProperty, "ChipCornerRadius");
        ring.SetResourceReference(Border.CornerRadiusProperty, "SwatchRingCornerRadius");

        var button = new Button
        {
            Style = (Style)FindResource("AccentSwatchStyle"),
            Content = ring,
            Tag = CustomSwatchTag,
            ToolTip = "Pick any colour",
            Margin = new Thickness(0)
        };

        button.Click += (_, _) =>
        {
            using var dialog = CreateColourDialog(System.Drawing.Color.FromArgb(
                ThemeManager.Accent.R, ThemeManager.Accent.G, ThemeManager.Accent.B));

            try
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                var chosen = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
                ThemeManager.Set(System.Windows.Application.Current, ThemePalette.Hex(chosen));
            }
            finally
            {
                RememberCustomColours(dialog);
            }
        };

        return button;
    }

    private const string CustomSwatchTag = "custom";

    private Button CreateSwatch(string choice, string tooltip, Color? colour)
    {
        var inner = new Border
        {
            Width = 22,
            Height = 22,
            Background = colour is null
                ? null
                : new SolidColorBrush(ThemeManager.DisplayAccent(choice))
        };

        if (colour is null)
        {
            // "Auto" has no colour of its own, so it shows a letter on a quiet outline.
            inner.BorderThickness = new Thickness(1);
            inner.BorderBrush = (Brush)FindResource("BorderSubtleBrush");
            inner.Child = new TextBlock
            {
                Text = "A",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
        }

        var ring = new Border
        {
            Width = 28,
            Height = 28,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Child = inner
        };

        inner.SetResourceReference(Border.CornerRadiusProperty, "ChipCornerRadius");
        ring.SetResourceReference(Border.CornerRadiusProperty, "SwatchRingCornerRadius");

        var button = new Button
        {
            Style = (Style)FindResource("AccentSwatchStyle"),
            Content = ring,
            Tag = choice,
            ToolTip = tooltip,
            Margin = new Thickness(0, 0, 4, 0)
        };

        // Appearance only: this writes a preference, never a hardware setting.
        button.Click += (_, _) => ThemeManager.Set(System.Windows.Application.Current, choice);
        return button;
    }

    /// <summary>
    /// Fills the Font list with the families this machine reports. Built here rather than in XAML
    /// because the list depends on what is installed: offering a family that is not present would
    /// show Segoe UI and look like the choice did nothing.
    /// </summary>
    private void BuildFontChoices()
    {
        _appearanceLoading = true;
        try
        {
            FontBox.Items.Clear();

            foreach (var (name, family) in ThemeManager.FontFamilies)
            {
                // Each entry previews its own face in the drop-down; the closed box shows the
                // selected one through the window's inherited font.
                var item = new ComboBoxItem { Content = name, Tag = family };
                if (family != ThemeManager.AutoFont)
                    item.FontFamily = new FontFamily(family);

                FontBox.Items.Add(item);
            }
        }
        finally
        {
            _appearanceLoading = false;
        }
    }

    private void RefreshAppearanceSelection()
    {
        var selected = (Brush)FindResource("TextPrimaryBrush");
        var choice = ThemeManager.Choice;

        // A colour the user picked is stored as its hex, so it matches none of the preset tags; the
        // custom swatch carries the ring instead.
        var isPreset = string.Equals(choice, ThemeManager.Auto, StringComparison.OrdinalIgnoreCase) ||
                       ThemePalette.Choices.Any(c =>
                           string.Equals(c.Hex, choice, StringComparison.OrdinalIgnoreCase));

        foreach (var button in AccentSwatchRow.Children.OfType<Button>())
        {
            if (button.Content is not Border ring)
                continue;

            var tag = button.Tag as string;
            var active = string.Equals(tag, CustomSwatchTag, StringComparison.Ordinal)
                ? !isPreset
                : string.Equals(tag, choice, StringComparison.OrdinalIgnoreCase);

            ring.BorderBrush = active ? selected : Brushes.Transparent;
        }

        _appearanceLoading = true;
        try
        {
            BuildAppearanceChoices();

            ThemeModeBox.SelectedIndex = ThemeManager.Mode switch
            {
                AppTheme.System => 0,
                AppTheme.Light => 1,
                _ => 2
            };

            var resolved = ThemeManager.IsLight ? "light" : "dark";
            ThemeModeHint.Text = ThemeManager.Mode switch
            {
                AppTheme.System => $"Following Windows: {resolved}. Editing the {resolved} theme.",
                AppTheme.Light => "Editing the light theme.",
                _ => "Editing the dark theme."
            };

            ThemePresetBox.SelectedIndex = IndexOfPreset();
            BackgroundBox.SelectedIndex = IndexOfSurface(ThemeManager.Surface);
            SectionsBox.SelectedIndex = IndexOfCard(ThemeManager.Card);
            FontBox.SelectedIndex = IndexOfFont(ThemeManager.FontChoice);
        }
        finally
        {
            _appearanceLoading = false;
        }
    }

    /// <summary>Rebuilds the mode-specific preset, background and section lists.</summary>
    private void BuildAppearanceChoices()
    {
        ThemePresetBox.Items.Clear();
        foreach (var preset in ActivePresets)
            ThemePresetBox.Items.Add(new ComboBoxItem { Content = preset.Name });
        ThemePresetBox.Items.Add(new ComboBoxItem { Content = "Custom", IsEnabled = false });

        BackgroundBox.Items.Clear();
        foreach (var (name, _) in ThemeManager.Surfaces)
            BackgroundBox.Items.Add(new ComboBoxItem { Content = name });

        SectionsBox.Items.Clear();
        foreach (var (name, _) in ThemeManager.Cards)
            SectionsBox.Items.Add(new ComboBoxItem { Content = name });
    }

    /// <summary>The matching preset, or the trailing "Custom" entry.</summary>
    private static int IndexOfPreset()
    {
        var presets = ActivePresets;
        var index = Array.FindIndex(presets, p =>
            string.Equals(p.Accent, ThemeManager.Choice, StringComparison.OrdinalIgnoreCase) &&
            p.Surface == ThemeManager.Surface &&
            p.Card == ThemeManager.Card);

        return index >= 0 ? index : presets.Length;
    }

    private void ThemeMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceLoading || ThemeModeBox.SelectedIndex < 0) return;

        var mode = ThemeModeBox.SelectedIndex switch
        {
            0 => AppTheme.System,
            1 => AppTheme.Light,
            _ => AppTheme.Dark
        };

        ThemeManager.SetMode(System.Windows.Application.Current, mode);
    }

    private void ThemePreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceLoading || ThemePresetBox.SelectedIndex < 0) return;

        var presets = ActivePresets;
        if (ThemePresetBox.SelectedIndex >= presets.Length) return;

        var preset = presets[ThemePresetBox.SelectedIndex];
        ThemeManager.SetPreset(
            System.Windows.Application.Current, preset.Accent, preset.Surface, preset.Card);
    }

    private static int IndexOfSurface(ThemePalette.ThemeSurface surface) =>
        Math.Max(0, Array.FindIndex(ThemeManager.Surfaces, s => s.Surface == surface));

    private static int IndexOfCard(ThemePalette.ThemeCard card) =>
        Math.Max(0, Array.FindIndex(ThemeManager.Cards, c => c.Card == card));

    private static int IndexOfFont(string family) =>
        Math.Max(0, Array.FindIndex(ThemeManager.FontFamilies,
            f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)));

    private void Background_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceLoading || BackgroundBox.SelectedIndex < 0) return;

        var index = Math.Min(BackgroundBox.SelectedIndex, ThemeManager.Surfaces.Length - 1);
        ThemeManager.SetBackground(System.Windows.Application.Current, ThemeManager.Surfaces[index].Surface);
    }

    private void Sections_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceLoading || SectionsBox.SelectedIndex < 0) return;

        var index = Math.Min(SectionsBox.SelectedIndex, ThemeManager.Cards.Length - 1);
        ThemeManager.SetSections(System.Windows.Application.Current, ThemeManager.Cards[index].Card);
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceLoading || FontBox.SelectedIndex < 0) return;

        var index = Math.Min(FontBox.SelectedIndex, ThemeManager.FontFamilies.Length - 1);
        ThemeManager.SetFont(System.Windows.Application.Current, ThemeManager.FontFamilies[index].Family);
    }

    /// <summary>
    /// Loads the appearance choices that are not part of the palette. The corner style and scale
    /// are shared with the curve editor; the remaining choices cover the dashboard and the tray.
    /// </summary>
    private void InitializeAppearanceCustomizations()
    {
        _dashboardOptionsLoading = true;
        try
        {
            CornerStyleBox.SelectedIndex = (int)CornerStyles.Current;
            TemperatureUnitBox.SelectedIndex = TemperatureDisplay.UseFahrenheit ? 1 : 0;
            TogCompactNav.IsChecked = SettingsManager.Get("CompactNav", 0) == 1;
            WindowOpacitySlider.Value = Math.Clamp(SettingsManager.Get("WindowOpacity", 100), 90, 100);
            TogCardTemperature.IsChecked = SettingsManager.Get("ShowTemperatureCard", 1) == 1;
            TogCardPower.IsChecked = SettingsManager.Get("ShowPowerCard", 1) == 1;
            TogCardDisplay.IsChecked = SettingsManager.Get("ShowDisplayCard", 1) == 1;
            TogCardFans.IsChecked = SettingsManager.Get("ShowFansCard", 1) == 1;

            OsdPositionBox.SelectedIndex =
                Math.Max(0, Array.IndexOf(UiPreferences.OsdPositions, UiPreferences.OsdPosition));
            OsdDesignBox.SelectedIndex = (int)UiPreferences.OsdDesign;
            OsdDurationBox.SelectedIndex =
                Math.Max(0, Array.IndexOf(UiPreferences.OsdDurations, UiPreferences.OsdDurationSeconds));
            OsdSizeBox.SelectedIndex =
                Math.Max(0, Array.IndexOf(UiPreferences.OsdSizes, UiPreferences.OsdSizePercent));
            TrayIconBox.SelectedIndex =
                Math.Max(0, Array.IndexOf(UiPreferences.TrayIconStyles, UiPreferences.TrayIconStyle));
        }
        finally
        {
            _dashboardOptionsLoading = false;
        }

        ApplyCornerStyle(CornerStyles.Current);
        ApplyCompactNav(TogCompactNav.IsChecked == true);
        ApplyWindowOpacity((int)WindowOpacitySlider.Value);
        ApplyDashCardVisibility();

        LoadUiScaleOptions(SettingsManager.Get("UiScale", 100));
    }

    private void CornerStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_dashboardOptionsLoading || CornerStyleBox.SelectedIndex < 0) return;

        var style = (CornerStyle)Math.Clamp(CornerStyleBox.SelectedIndex, 0, 2);
        CornerStyles.Save(style);
        ApplyCornerStyle(style);
    }

    /// <summary>
    /// Applies the shared corner choice to every corner in the dashboard. Square zeroes them,
    /// Soft halves them, Rounded is the shipped look; the nav buttons carry per-edge radii in
    /// Tag, which the style binds.
    /// </summary>
    private void ApplyCornerStyle(CornerStyle style)
    {
        var radii = CornerStyles.Radii(style);
        Resources["FrameCornerRadius"] = new CornerRadius(radii.Frame);
        Resources["HeroCornerRadius"] = new CornerRadius(radii.Hero);
        Resources["CardCornerRadius"] = new CornerRadius(radii.Card);
        Resources["SurfaceCornerRadius"] = new CornerRadius(radii.Surface);
        Resources["ChipCornerRadius"] = new CornerRadius(radii.Chip);
        Resources["MicroCornerRadius"] = new CornerRadius(radii.Micro);
        Resources["SwatchRingCornerRadius"] = new CornerRadius(radii.Ring);
        Resources["NavBarCornerRadius"] = new CornerRadius(0, 0, radii.Nav, radii.Nav);

        NavDashBtn.Tag = $"0,0,0,{radii.Nav:0}";
        NavGpuBtn.Tag = "0,0,0,0";
        NavKeyboardBtn.Tag = "0,0,0,0";
        NavSetBtn.Tag = $"0,0,{radii.Nav:0},0";
    }

    private void ApplyCompactNav(bool compact)
    {
        var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NavDashText.Visibility = visibility;
        NavGpuText.Visibility = visibility;
        NavKeyboardText.Visibility = visibility;
        NavSetText.Visibility = visibility;
        NavRow.Height = new GridLength(compact ? 42 : 54);
    }

    private void ApplyWindowOpacity(int percent)
    {
        percent = Math.Clamp(percent, 90, 100);
        Opacity = percent / 100.0;
        WindowOpacityText.Text = $"{percent}%";
    }

    private void ApplyDashCardVisibility()
    {
        TemperatureCard.Visibility =
            TogCardTemperature.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PowerSection.Visibility =
            TogCardPower.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DisplaySection.Visibility =
            TogCardDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FanSection.Visibility =
            TogCardFans.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CompactNav_Toggled(object sender, RoutedEventArgs e)
    {
        if (_dashboardOptionsLoading) return;

        var compact = TogCompactNav.IsChecked == true;
        SettingsManager.Save("CompactNav", compact ? 1 : 0);
        ApplyCompactNav(compact);
    }

    private void WindowOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider raises ValueChanged once while the XAML is built; the saved choice is
        // applied explicitly after InitializeAppearanceCustomizations has read it.
        if (_dashboardOptionsLoading || !IsLoaded) return;

        var percent = (int)Math.Round(e.NewValue);
        SettingsManager.Save("WindowOpacity", percent);
        ApplyWindowOpacity(percent);
    }

    private void DashCardOption_Toggled(object sender, RoutedEventArgs e)
    {
        if (_dashboardOptionsLoading) return;

        SettingsManager.Save("ShowTemperatureCard", TogCardTemperature.IsChecked == true ? 1 : 0);
        SettingsManager.Save("ShowPowerCard", TogCardPower.IsChecked == true ? 1 : 0);
        SettingsManager.Save("ShowDisplayCard", TogCardDisplay.IsChecked == true ? 1 : 0);
        SettingsManager.Save("ShowFansCard", TogCardFans.IsChecked == true ? 1 : 0);
        ApplyDashCardVisibility();
    }

    private void TemperatureUnit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_dashboardOptionsLoading || TemperatureUnitBox.SelectedIndex < 0) return;

        var unit = TemperatureUnitBox.SelectedIndex == 1
            ? TemperatureDisplay.Fahrenheit
            : TemperatureDisplay.Celsius;
        SettingsManager.Save("TemperatureUnit", unit);
        (DataContext as DashboardViewModel)?.RefreshTemperatureLabels();
        AppearanceChangeSignal.Notify();
    }

    private void OsdPosition_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_dashboardOptionsLoading || OsdPositionBox.SelectedIndex < 0) return;

        SettingsManager.Save("OsdPosition",
            UiPreferences.OsdPositions[Math.Min(OsdPositionBox.SelectedIndex, UiPreferences.OsdPositions.Length - 1)]);
    }

    private void OsdDesign_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_dashboardOptionsLoading || OsdDesignBox.SelectedIndex < 0) return;

        var design = (UiPreferences.ToastDesign)Math.Clamp(
            OsdDesignBox.SelectedIndex, 0, UiPreferences.OsdDesigns.Length - 1);
        SettingsManager.Save("OsdDesign", design.ToString());
    }

    private void OsdDuration_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_dashboardOptionsLoading || OsdDurationBox.SelectedIndex < 0) return;

        SettingsManager.Save("OsdDuration",
            UiPreferences.OsdDurations[Math.Min(OsdDurationBox.SelectedIndex, UiPreferences.OsdDurations.Length - 1)]);
    }

    private void OsdSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_dashboardOptionsLoading || OsdSizeBox.SelectedIndex < 0) return;

        SettingsManager.Save("OsdSize",
            UiPreferences.OsdSizes[Math.Min(OsdSizeBox.SelectedIndex, UiPreferences.OsdSizes.Length - 1)]);
    }

    private void TrayIcon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_dashboardOptionsLoading || TrayIconBox.SelectedIndex < 0) return;

        SettingsManager.Save("TrayIconStyle",
            UiPreferences.TrayIconStyles[Math.Min(TrayIconBox.SelectedIndex, UiPreferences.TrayIconStyles.Length - 1)]);
        AppearanceChangeSignal.Notify();
    }

    /// <summary>
    /// One swatch per mode for the toast and the tray icon. The initial tells them apart without a
    /// label, and its ink flips for a light fill.
    /// </summary>
    private void BuildOsdColourSwatches()
    {
        OsdColourRow.Children.Clear();

        foreach (var (profile, name, _) in ModeColors.Choices)
        {
            var colour = ModeColors.For(profile);
            var ink = ModeColors.InkFor(colour);

            var inner = new Border
            {
                Width = 22,
                Height = 22,
                Background = new SolidColorBrush(
                    Color.FromRgb(colour.R, colour.G, colour.B)),
                Child = new TextBlock
                {
                    Text = name[..1],
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(ink.R, ink.G, ink.B)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                }
            };
            inner.SetResourceReference(Border.CornerRadiusProperty, "ChipCornerRadius");

            var ring = new Border
            {
                Width = 28,
                Height = 28,
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                Child = inner
            };
            ring.SetResourceReference(Border.CornerRadiusProperty, "SwatchRingCornerRadius");

            var button = new Button
            {
                Style = (Style)FindResource("AccentSwatchStyle"),
                Content = ring,
                ToolTip = $"{name}: change the toast and tray colour",
                Margin = new Thickness(0, 0, 4, 0)
            };
            button.Click += (_, _) => PickOsdColour(profile, name);
            OsdColourRow.Children.Add(button);
        }
    }

    private void PickOsdColour(Nitrous.Enums.PowerProfile profile, string name)
    {
        using var dialog = CreateColourDialog(ModeColors.For(profile));

        try
        {
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            ModeColors.Save(profile, dialog.Color);
            BuildOsdColourSwatches();
            AppearanceChangeSignal.Notify();
        }
        finally
        {
            RememberCustomColours(dialog);
        }
    }

    private void OsdColourReset_Click(object sender, RoutedEventArgs e)
    {
        ModeColors.Reset();
        BuildOsdColourSwatches();
        AppearanceChangeSignal.Notify();
    }

    private void InitializeAppProfiles()
    {
        _appProfilesLoading = true;
        try
        {
            TogAppProfiles.IsChecked = AppProfileStore.Enabled;
            BuildNewAppProfileChoices();
            AppRunningBox.Items.Clear();
            AppRunningBox.Items.Add(new ComboBoxItem
            {
                Content = "Choose a running app",
                IsEnabled = false
            });
            AppRunningBox.SelectedIndex = 0;
        }
        finally
        {
            _appProfilesLoading = false;
        }

        _appRules = AppProfileStore.Load();
        BuildAppProfileList();
        UpdateAppProfileActiveText();
    }

    /// <summary>Turbo is omitted where the firmware does not expose it, matching the mode buttons.</summary>
    private PowerProfile[] AppProfileChoices()
    {
        var turbo = (DataContext as DashboardViewModel)?.IsTurboSupported != false;
        return turbo
            ? [PowerProfile.Quiet, PowerProfile.Balanced, PowerProfile.Performance, PowerProfile.Turbo]
            : [PowerProfile.Quiet, PowerProfile.Balanced, PowerProfile.Performance];
    }

    private PowerProfile SelectedNewProfile() =>
        NewAppProfileBox.SelectedItem is ComboBoxItem { Tag: PowerProfile profile }
            ? profile
            : PowerProfile.Performance;

    private AppFanMode SelectedNewFan() =>
        NewAppFanBox.SelectedItem is ComboBoxItem { Tag: AppFanMode fan }
            ? fan
            : AppFanMode.Leave;

    private void BuildNewAppProfileChoices()
    {
        NewAppProfileBox.Items.Clear();
        foreach (var profile in AppProfileChoices())
            NewAppProfileBox.Items.Add(new ComboBoxItem { Content = profile.ToString(), Tag = profile });

        NewAppProfileBox.SelectedIndex = Math.Min(2, NewAppProfileBox.Items.Count - 1);

        NewAppFanBox.Items.Clear();
        foreach (var mode in FanChoices)
            NewAppFanBox.Items.Add(new ComboBoxItem { Content = mode.ToString(), Tag = mode });

        NewAppFanBox.SelectedIndex = 0;
    }

    private void BuildAppProfileList()
    {
        _appProfilesLoading = true;
        try
        {
            AppProfileList.Children.Clear();

            if (_appRules.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "No rules yet. Add an app above.",
                    FontSize = 9.5,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
                AppProfileList.Children.Add(empty);
                return;
            }

            for (var i = 0; i < _appRules.Count; i++)
                AppProfileList.Children.Add(CreateAppProfileRow(_appRules[i], i));
        }
        finally
        {
            _appProfilesLoading = false;
        }
    }

    private Border CreateAppProfileRow(AppProfileRule rule, int index)
    {
        var name = new TextBlock
        {
            Text = rule.DisplayName,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var executable = new TextBlock
        {
            Text = rule.Executable,
            FontSize = 8.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        executable.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("ModernToggleStyle"),
            IsChecked = rule.Enabled,
            Tag = index,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Pause this rule without deleting it."
        };
        toggle.Checked += AppRuleEnabled_Toggled;
        toggle.Unchecked += AppRuleEnabled_Toggled;

        var remove = new Button
        {
            Content = "\u00d7",
            Style = (Style)FindResource("GhostBtn"),
            FontSize = 13,
            Padding = new Thickness(4, 0, 4, 0),
            Tag = index,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove this rule."
        };
        remove.Click += AppRuleRemove_Click;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(toggle, 1);
        Grid.SetColumn(remove, 2);
        top.Children.Add(name);
        top.Children.Add(toggle);
        top.Children.Add(remove);

        var choices = AppProfileChoices();
        var profile = new ComboBox
        {
            Width = 100,
            Style = (Style)FindResource("CompactComboBoxStyle"),
            Tag = index,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "The power mode this app switches to."
        };
        foreach (var choice in choices)
            profile.Items.Add(new ComboBoxItem { Content = choice.ToString(), Tag = choice });

        profile.SelectedIndex = Math.Max(0,
            Array.IndexOf(choices, AppProfileStore.ClampForThisMachine(rule.Profile)));
        profile.SelectionChanged += AppRuleProfile_Changed;

        var fan = new ComboBox
        {
            Width = 108,
            Style = (Style)FindResource("CompactComboBoxStyle"),
            Tag = index,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "What this app does to the fans."
        };
        foreach (var mode in FanChoices)
            fan.Items.Add(new ComboBoxItem { Content = $"Fans: {mode}", Tag = mode });

        fan.SelectedIndex = Math.Max(0, Array.IndexOf(FanChoices, rule.Fan));
        fan.SelectionChanged += AppRuleFan_Changed;

        var bottom = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(executable, 0);
        Grid.SetColumn(profile, 1);
        Grid.SetColumn(fan, 2);
        bottom.Children.Add(executable);
        bottom.Children.Add(profile);
        bottom.Children.Add(fan);

        var content = new StackPanel();
        content.Children.Add(top);
        content.Children.Add(bottom);

        var border = new Border
        {
            Padding = new Thickness(8, 6, 6, 6),
            Margin = new Thickness(0, 4, 0, 0),
            Child = content
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceHoverBrush");
        border.SetResourceReference(Border.CornerRadiusProperty, "SurfaceCornerRadius");
        return border;
    }

    private void AppProfiles_Toggled(object sender, RoutedEventArgs e)
    {
        if (_appProfilesLoading) return;
        AppProfileStore.SetEnabled(TogAppProfiles.IsChecked == true);
        AppProfileChangeSignal.Notify();
    }

    private void AppRuleProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appProfilesLoading || sender is not ComboBox { Tag: int index } combo) return;
        if (index < 0 || index >= _appRules.Count) return;
        if (combo.SelectedItem is not ComboBoxItem { Tag: PowerProfile profile }) return;

        _appRules[index].Profile = profile;
        SaveAppRules();
    }

    private void AppRuleFan_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appProfilesLoading || sender is not ComboBox { Tag: int index } combo) return;
        if (index < 0 || index >= _appRules.Count) return;
        if (combo.SelectedItem is not ComboBoxItem { Tag: AppFanMode fan }) return;

        _appRules[index].Fan = fan;
        SaveAppRules();
    }

    private void AppRuleEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_appProfilesLoading || sender is not CheckBox { Tag: int index } toggle) return;
        if (index < 0 || index >= _appRules.Count) return;

        _appRules[index].Enabled = toggle.IsChecked == true;
        SaveAppRules();
    }

    private void AppRuleRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        if (index < 0 || index >= _appRules.Count) return;

        _appRules.RemoveAt(index);
        SaveAppRules();
        BuildAppProfileList();
        UpdateAppProfileStatus("Rule removed.");
    }

    private void AppBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;

        var executable = System.IO.Path.GetFileName(dialog.FileName);
        string? display = null;
        try { display = System.Diagnostics.FileVersionInfo.GetVersionInfo(dialog.FileName).ProductName; }
        catch { }

        AddAppRule(executable, display, SelectedNewProfile(), SelectedNewFan());
    }

    private void AppRunningBox_DropDownOpened(object? sender, EventArgs e)
    {
        var previous = (AppRunningBox.SelectedItem as ComboBoxItem)?.Tag as RunningApp;
        AppRunningBox.Items.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<RunningApp>();

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var title = process.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title)) continue;

                var executable = process.ProcessName + ".exe";
                if (string.Equals(process.ProcessName, "Nitrous", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seen.Add(executable)) continue;

                apps.Add(new RunningApp(executable, title.Trim()));
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var app in apps.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ComboBoxItem
            {
                Content = $"{app.Title} \u00b7 {app.Executable}",
                Tag = app
            };
            AppRunningBox.Items.Add(item);

            if (previous is not null &&
                string.Equals(previous.Executable, app.Executable, StringComparison.OrdinalIgnoreCase))
            {
                AppRunningBox.SelectedItem = item;
            }
        }

        if (AppRunningBox.Items.Count == 0)
            AppRunningBox.Items.Add(new ComboBoxItem { Content = "No windowed apps found", IsEnabled = false });
    }

    private void AppRunningAdd_Click(object sender, RoutedEventArgs e)
    {
        if (AppRunningBox.SelectedItem is not ComboBoxItem { Tag: RunningApp app })
        {
            UpdateAppProfileStatus("Pick a running app first.", warn: true);
            return;
        }

        AddAppRule(app.Executable, app.Title, SelectedNewProfile(), SelectedNewFan());
    }

    private void AddAppRule(string executable, string? displayName, PowerProfile profile, AppFanMode fan)
    {
        executable = System.IO.Path.GetFileName(executable);
        if (string.IsNullOrWhiteSpace(executable)) return;

        if (_appRules.Exists(r => string.Equals(r.Executable, executable, StringComparison.OrdinalIgnoreCase)))
        {
            UpdateAppProfileStatus($"{executable} is already in the list.", warn: true);
            return;
        }

        _appRules.Add(new AppProfileRule
        {
            Executable = executable,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? System.IO.Path.GetFileNameWithoutExtension(executable)
                : displayName.Trim(),
            Profile = AppProfileStore.ClampForThisMachine(profile),
            Fan = fan,
            Enabled = true
        });

        SaveAppRules();
        BuildAppProfileList();
        UpdateAppProfileStatus($"{executable} added. It takes effect within a few seconds.");
    }

    private void SaveAppRules()
    {
        AppProfileStore.Save(_appRules);
        AppProfileChangeSignal.Notify();
    }

    private void UpdateAppProfileStatus(string message, bool warn = false)
    {
        AppProfileStatus.Text = message;
        AppProfileStatus.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        AppProfileStatus.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "SuccessBrush");
    }

    /// <summary>Shows which rule is holding the mode; the tray writes the value.</summary>
    private void UpdateAppProfileActiveText()
    {
        var active = SettingsManager.Get(AppProfileStore.ActiveKey, "");
        var text = string.IsNullOrWhiteSpace(active) ? "No app rule active" : $"Active: {active}";

        if (AppProfileActiveText.Text != text)
            AppProfileActiveText.Text = text;

        AppProfileActiveText.Foreground = (Brush)FindResource(
            string.IsNullOrWhiteSpace(active) ? "TextDimBrush" : "AccentBrush");
    }

    private void LoadUiScaleOptions(int saved)
    {
        _uiScaleLoading = true;
        try
        {
            var work = SystemParameters.WorkArea;
            var baseHeight = ReadWindowBaseHeight();
            UiScale110Item.IsEnabled = baseHeight * 1.10 <= work.Height - 24;
            UiScale125Item.IsEnabled = baseHeight * 1.25 <= work.Height - 24;

            var allowed = new List<int> { 90, 100 };
            if (UiScale110Item.IsEnabled) allowed.Add(110);
            if (UiScale125Item.IsEnabled) allowed.Add(125);

            var chosen = allowed.Contains(saved) ? saved : allowed[^1];
            UiScaleBox.SelectedIndex = chosen switch
            {
                90 => 0,
                110 => 2,
                125 => 3,
                _ => 1
            };

            ApplyUiScale(chosen);
            if (chosen != saved) SettingsManager.Save("UiScale", chosen);
        }
        finally
        {
            _uiScaleLoading = false;
        }
    }

    private void UiScale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_uiScaleLoading || UiScaleBox.SelectedIndex < 0) return;

        var percent = UiScaleBox.SelectedIndex switch
        {
            0 => 90,
            2 => 110,
            3 => 125,
            _ => 100
        };

        SettingsManager.Save("UiScale", percent);
        ApplyUiScale(percent);
    }

    /// <summary>
    /// Scales the whole dashboard, not just its text: the window grows by the same factor and the
    /// root carries a LayoutTransform, so every card, plot and control stays proportional and crisp.
    /// ComboBox popups are separate windows and get the same transform through UiScaleTransform.
    /// </summary>
    private void ApplyUiScale(int percent)
    {
        var work = CurrentWorkArea();
        var baseHeight = ReadWindowBaseHeight();
        var maxScale = Math.Min((work.Height - 24) / (double)baseHeight, (work.Width - 24) / 375.0);
        var scale = percent / 100.0;

        if (scale > maxScale)
        {
            var fit = new[] { 125, 110, 100, 90 }.FirstOrDefault(p => p / 100.0 <= maxScale, 90);
            Logger.Info($"UI scale: {percent}% does not fit this screen; using {fit}%.");
            percent = fit;
            scale = percent / 100.0;

            _uiScaleLoading = true;
            try
            {
                UiScaleBox.SelectedIndex = percent switch
                {
                    90 => 0,
                    110 => 2,
                    125 => 3,
                    _ => 1
                };
            }
            finally
            {
                _uiScaleLoading = false;
            }

            SettingsManager.Save("UiScale", percent);
        }

        MinWidth = Math.Round(375 * scale);
        MaxWidth = MinWidth;
        Width = Math.Round(375 * scale);
        MinHeight = Math.Round(MinBaseHeight * scale);
        MaxHeight = Math.Max(MinHeight, work.Height - 16);
        Height = Math.Min(Math.Round(baseHeight * scale), MaxHeight);

        if (Content is FrameworkElement root)
            root.LayoutTransform = new ScaleTransform(scale, scale);

        Resources["UiScaleTransform"] = new ScaleTransform(scale, scale);
        ClampWindowToWorkArea();
    }

    private const int MinBaseHeight = 520;
    private const int MaxBaseHeight = 2000;

    private static int ReadWindowBaseHeight() =>
        Math.Clamp(SettingsManager.Get("WindowHeight", 660), MinBaseHeight, MaxBaseHeight);

    private void WindowResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Content is not FrameworkElement root ||
            root.LayoutTransform is not ScaleTransform transform)
        {
            return;
        }

        var scale = Math.Max(0.1, transform.ScaleY);
        var work = CurrentWorkArea();
        Height = Math.Clamp(Height + e.VerticalChange,
            MinBaseHeight * scale,
            Math.Max(MinBaseHeight * scale, work.Height - 16));
    }

    private void WindowResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Content is not FrameworkElement root ||
            root.LayoutTransform is not ScaleTransform transform)
        {
            return;
        }

        var baseHeight = (int)Math.Round(Height / Math.Max(0.1, transform.ScaleY));
        SettingsManager.Save("WindowHeight", Math.Clamp(baseHeight, MinBaseHeight, MaxBaseHeight));
    }

    /// <summary>The work area of the screen the window is on, in WPF device-independent units.</summary>
    private Rect CurrentWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(handle)
            : System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return SystemParameters.WorkArea;

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            screen.WorkingArea.Left / dpi.DpiScaleX,
            screen.WorkingArea.Top / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX,
            screen.WorkingArea.Height / dpi.DpiScaleY);
    }

    private void ClampWindowToWorkArea()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;

        var work = CurrentWorkArea();
        if (Width > work.Width) Width = work.Width;
        if (Height > work.Height) Height = work.Height;

        if (Left < work.Left) Left = work.Left;
        if (Top < work.Top) Top = work.Top;
        if (Left + Width > work.Right) Left = Math.Max(work.Left, work.Right - Width);
        if (Top + Height > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
    }

    private void OnThemeChanged()
    {
        ApplyNavColours();

        // The accent row shows the clamped accent, so it is rebuilt when the mode or accent moves.
        BuildAppearanceSwatches();
        AppearanceChangeSignal.Notify();

        // The GPU APPLY button's colour is a captured string, not a resource, so it has to be
        // re-read explicitly when the accent changes.
        if (DataContext is DashboardViewModel vm) vm.RefreshAccentColours();
    }

    /// <summary>
    /// NVML polling is only allowed while the GPU page is the visible page. Reading NVML wakes
    /// the dGPU, and no other page displays its values.
    /// </summary>
    private void UpdateGpuTelemetryScope()
    {
        if (DataContext is not DashboardViewModel vm) return;

        var visible = Visibility == Visibility.Visible && WindowState != WindowState.Minimized;
        vm.SetGpuTelemetryActive(visible && _activeNavIndex is 0 or 1);
    }

    private void NavDashBtn_Click(object sender, RoutedEventArgs e) => SetActiveNav(0);
    private void NavGpuBtn_Click(object sender, RoutedEventArgs e) => SetActiveNav(1);
    private void NavKeyboardBtn_Click(object sender, RoutedEventArgs e) => SetActiveNav(2);
    private void NavSetBtn_Click(object sender, RoutedEventArgs e) => SetActiveNav(3);

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
            vm.SetTelemetryEnabled(Visibility == Visibility.Visible && WindowState != WindowState.Minimized);

        UpdateGpuTelemetryScope();

        if (Visibility == Visibility.Visible)
        {
            RefreshDashboardState();
            SyncFnBrightnessHint();
        }
    }

    public void RefreshDashboardState()
    {
        var isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                       System.Windows.Forms.PowerLineStatus.Online;
        var powerColor = (Brush)FindResource(isOnline ? "DangerBrush" : "SuccessBrush");
        var powerText = isOnline ? "AC POWER" : "BATTERY";

        var acGeom = Geometry.Parse("M16,7V3H14V7H10V3H8V7C8,10 9.79,11.4 11,11.83V16H13V11.83C14.21,11.4 16,10 16,7M10,18H14V22H10V18Z");
        var battGeom = Geometry.Parse("M16.67,4H15V2H9V4H7.33A1.33,1.33 0 0,0 6,5.33V20.67C6,21.4 6.6,22 7.33,22H16.67A1.33,1.33 0 0,0 18,20.67V5.33C18,4.6 17.4,4 16.67,4Z");

        DashPowerPillBorder.BorderBrush = powerColor;
        DashPowerPillIcon.Fill = powerColor;
        DashPowerPillText.Foreground = powerColor;
        DashPowerPillText.Text = powerText;
        DashPowerPillIcon.Data = isOnline ? acGeom : battGeom;

        var activeMode = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
        var activeFan = Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile f) ? f : FanProfile.Auto;
        var activeRefresh = DisplayManager.GetMode();

        BtnPowerQuiet.IsChecked = activeMode == PowerProfile.Quiet;
        BtnPowerBal.IsChecked = activeMode == PowerProfile.Balanced;
        BtnPowerPerf.IsChecked = activeMode == PowerProfile.Performance;
        BtnPowerTurbo.IsChecked = activeMode == PowerProfile.Turbo;

        BtnFanAuto.IsChecked = activeFan == FanProfile.Auto;
        BtnFanMax.IsChecked = activeFan == FanProfile.Max;
        BtnFanCustom.IsChecked = activeFan == FanProfile.Medium;

        SyncRefreshControls();

        if (DataContext is DashboardViewModel vm)
        {
            vm.ActivePowerProfile = activeMode;
            vm.IsCustomFanEnabled = activeFan == FanProfile.Medium;
            vm.WhisperModeActive = SettingsManager.Get("WhisperActive", 0) == 1;

            // The tray applies the new profile's offsets on a hotkey or AC/DC change.
            // Reflect the live values so the sliders match what is actually applied.
            if (vm.ManageGpuClocks)
                vm.RefreshGpuOffsets();
        }

        if (TxtHkCycle != null)
        {
            TxtHkCycle.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_CyclePower", ""));
            TxtHkDash.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_Dashboard", ""));
            TxtHkFan.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_CycleFan", ""));
            TxtHkScreenSync.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_ScreenSync", ""));
            TxtHkSoundSync.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_SoundSync", ""));
            TxtHkEffect.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_NextEffect", ""));

            var nvidiaBinding = SettingsManager.Get("Hotkey_NvidiaApp", "");
            TxtHkNvidia.Text = HotkeyBinding.Format(nvidiaBinding);

            // The GPU page shows the same binding next to its OPEN button.
            if (DataContext is DashboardViewModel hotkeys)
                hotkeys.NvidiaHotkeyText = string.IsNullOrWhiteSpace(nvidiaBinding)
                    ? string.Empty
                    : $"Hotkey: {HotkeyBinding.Format(nvidiaBinding)}";
        }

        var profileChanged =
            activeMode != _lastSyncedPowerMode || activeFan != _lastSyncedFanMode;

        _lastSyncedPowerMode = activeMode;
        _lastSyncedFanMode = activeFan;
        _lastSyncedRefreshMode = activeRefresh;

        // CoolBoost hides in Max, and a mode change can reset it, so show what the firmware holds.
        UpdateCoolBoostVisibility();
        if (_coolBoostAvailable && profileChanged) RefreshCoolBoost();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Topmost = SettingsManager.Get("IsPinned", false);

        var top = SettingsManager.Get("WindowTop", -9999);
        var left = SettingsManager.Get("WindowLeft", -9999);
        if (top != -9999 && left != -9999)
        {
            Top = top;
            Left = left;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // The saved scale was applied before the window had a handle; re-apply it now that the real
        // screen and DPI are known, so a 125% choice cannot hang off a shorter display.
        ApplyUiScale(SettingsManager.Get("UiScale", 100));

        // The saved position can belong to a monitor that is no longer connected. Wait until
        // layout has produced a real window rect, then recover if necessary.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsureWindowVisible));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    // --- Sticky keys ---------------------------------------------------------------------
    // A Windows accessibility setting, not firmware: Acer's toggle flips SKF_STICKYKEYSON in the
    // same per-user preference. The two calls below are the documented way to read and change it.

    [StructLayout(LayoutKind.Sequential)]
    private struct StickyKeysState
    {
        public uint cbSize;
        public uint dwFlags;
    }

    private const uint SpiGetStickyKeys = 0x003A;
    private const uint SpiSetStickyKeys = 0x003B;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;
    private const uint SkfStickyKeySon = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, ref StickyKeysState pvParam, uint fWinIni);

    private bool _stickyKeysLoading = true;

    private void InitializeStickyKeys()
    {
        _stickyKeysLoading = true;

        try
        {
            var state = new StickyKeysState { cbSize = (uint)Marshal.SizeOf<StickyKeysState>() };
            if (SystemParametersInfo(SpiGetStickyKeys, 0, ref state, 0))
            {
                TogStickyKeys.IsChecked = (state.dwFlags & SkfStickyKeySon) != 0;
            }
            else
            {
                TogStickyKeys.IsEnabled = false;
                TogStickyKeys.ToolTip = "Windows did not report the sticky keys setting.";
                Logger.Warn("Sticky Keys: the setting could not be read.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Sticky Keys: could not read the setting.", ex);
        }

        _stickyKeysLoading = false;
    }

    private void StickyKeys_Toggled(object sender, RoutedEventArgs e)
    {
        if (_stickyKeysLoading) return;

        try
        {
            // Read first: the flags carry other preferences (confirmation, sound) that must survive.
            var state = new StickyKeysState { cbSize = (uint)Marshal.SizeOf<StickyKeysState>() };
            if (!SystemParametersInfo(SpiGetStickyKeys, 0, ref state, 0))
            {
                Logger.Warn("Sticky Keys: could not read the setting before writing it.");
                return;
            }

            var wanted = TogStickyKeys.IsChecked == true;
            state.dwFlags = wanted
                ? state.dwFlags | SkfStickyKeySon
                : state.dwFlags & ~SkfStickyKeySon;

            if (!SystemParametersInfo(SpiSetStickyKeys, 0, ref state, SpifUpdateIniFile | SpifSendChange))
                Logger.Warn("Sticky Keys: Windows refused the change.");
        }
        catch (Exception ex)
        {
            Logger.Warn("Sticky Keys: could not apply the change.", ex);
        }
    }

    private void EnsureWindowVisible()
    {
        if (WindowState != WindowState.Normal) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        // The custom title bar is the top 50 px. Require a usable strip of it to be inside a
        // monitor's work area; a fully off-screen or mostly hidden window gets recentered.
        var titleHeight = Math.Min(50, height);
        var titleTop = rect.Top;

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var left = Math.Max(rect.Left, area.Left);
            var right = Math.Min(rect.Right, area.Right);
            var top = Math.Max(titleTop, area.Top);
            var bottom = Math.Min(titleTop + titleHeight, area.Bottom);

            if (right - left >= Math.Min(120, width) && bottom - top >= 24)
                return;
        }

        // Nothing usable is on screen. Center on the primary work area in WPF's DIP space.
        WindowStartupLocation = WindowStartupLocation.Manual;
        var workArea = SystemParameters.WorkArea;
        var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        var windowHeight = ActualHeight > 0 ? ActualHeight : Height;

        Left = workArea.Left + Math.Max(0, (workArea.Width - windowWidth) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - windowHeight) / 2);
        Logger.Info($"Dashboard: recovered the window from ({rect.Left},{rect.Top}) to ({Left:0},{Top:0}).");
    }

    /// <summary>
    /// Keeps the machine and screen awake. The saved value is what the tray applies on its own thread, so
    /// the request survives this window closing; this process applies it too for an instant response.
    /// </summary>
    private void KeepAwake_Toggled(object sender, RoutedEventArgs e)
    {
        if (_keepAwakeLoading) return;

        var on = BtnKeepAwake.IsChecked == true;
        SettingsManager.Save("KeepAwake", on ? 1 : 0);
        KeepAwake.Apply(on);
    }

    /// <summary>Appearance: the binding has already updated the view model; the value is remembered here.</summary>
    private bool _setupTabLoading;

    /// <summary>
    /// SETUP shows one category at a time. The tabs are pinned above the scroller, so the header and the
    /// categories stay reachable however long the selected one is.
    /// </summary>
    private void SetupTab_Checked(object sender, RoutedEventArgs e)
    {
        if (SetupPowerPanel is null) return;

        var power = SetupPowerTab.IsChecked == true;
        var system = SetupSystemTab.IsChecked == true;
        var keyboard = SetupKeyboardTab.IsChecked == true;

        SetupPowerPanel.Visibility = power ? Visibility.Visible : Visibility.Collapsed;
        SetupSystemPanel.Visibility = system ? Visibility.Visible : Visibility.Collapsed;
        SetupKeyboardPanel.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
        SetupAppearancePanel.Visibility = !power && !system && !keyboard
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_setupTabLoading) return;

        SettingsScroller.ScrollToTop();

        if (!_captureMode)
        {
            SettingsManager.Save("SetupTab",
                power ? "Power" : system ? "System" : keyboard ? "Keyboard" : "Appearance");
        }
    }

    private void SelectSetupTab(string? name)
    {
        var tab = name switch
        {
            "System" => SetupSystemTab,
            "Keyboard" or "Keys" => SetupKeyboardTab,
            "Appearance" or "Theme" => SetupAppearanceTab,
            _ => SetupPowerTab
        };

        tab.IsChecked = true;
    }

    private void Scrollbars_Toggled(object sender, RoutedEventArgs e)
    {
        var show = TogScrollbars.IsChecked == true;
        SettingsManager.Save("ShowScrollbars", show ? 1 : 0);
        ApplyScrollbarMode(show);
    }

    /// <summary>
    /// Appearance: shows or hides the scrollbar on the four pages. Hidden only takes the bar away -
    /// the wheel, the keyboard and touch panning still scroll the page.
    /// </summary>
    private void ApplyScrollbarMode(bool show)
    {
        var mode = show ? System.Windows.Controls.ScrollBarVisibility.Auto
                        : System.Windows.Controls.ScrollBarVisibility.Hidden;

        DashPage.VerticalScrollBarVisibility = mode;
        GpuPage.VerticalScrollBarVisibility = mode;
        KeyboardPage.VerticalScrollBarVisibility = mode;
        SettingsScroller.VerticalScrollBarVisibility = mode;

        Logger.Info($"Appearance: scrollbar {(show ? "shown" : "hidden")} on the four pages.");
    }

    private void Window_Activated(object sender, EventArgs e)
    {
        SyncFnBrightnessHint();

        // A monitor may have been plugged in, unplugged or switched while the window was in the background.
        RefreshDisplaySummary();
    }

    /// <summary>
    /// Reads the EC backlight level once, on LIGHT open or window activation, and explains a
    /// difference from the saved preset. This is deliberately not polled: each call opens the HID
    /// and performs three exchanges, which is too much to do continuously.
    /// </summary>
    private void SyncFnBrightnessHint()
    {
        if (KeyboardPage.Visibility != Visibility.Visible) return;

        if (!_keyboardAvailable || _syncEnabled)
        {
            HideFnBrightnessHints();
            return;
        }

        if (_fnBrightnessReading) return;
        if ((DateTime.UtcNow - _lastFnBrightnessReadUtc).TotalMilliseconds < 1500) return;

        _fnBrightnessReading = true;
        _lastFnBrightnessReadUtc = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            var level = AcerWmiManager.GetKeyboardBacklightTimeout()?.Brightness;

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _fnBrightnessReading = false;
                    ApplyFnBrightnessHint(level);
                }));
            }
            catch (InvalidOperationException)
            {
                _fnBrightnessReading = false;
            }
            catch (TaskCanceledException)
            {
                _fnBrightnessReading = false;
            }
        });
    }

    private void ApplyFnBrightnessHint(int? level)
    {
        if (!_keyboardAvailable || _syncEnabled || level is not int value)
        {
            HideFnBrightnessHints();
            return;
        }

        _fnBrightnessLevel = value;

        var text = $"Fn keys are at {value}%. Moving this slider applies the saved preset.";
        GlobalFnBrightnessHint.Text = text;
        // The register only has the five steps, so compare like with like or a 43% slider would
        // keep reporting a difference after it has been applied.
        GlobalFnBrightnessHint.Visibility =
            value != AcerWmiManager.NearestKeyboardBrightness(_globalBrightness)
                ? Visibility.Visible
                : Visibility.Collapsed;

        AreaFnBrightnessHint.Text = text;
        AreaFnBrightnessHint.Visibility =
            value != AcerWmiManager.NearestKeyboardBrightness(_keyboardBrightness)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void HideFnBrightnessHints()
    {
        if (GlobalFnBrightnessHint != null) GlobalFnBrightnessHint.Visibility = Visibility.Collapsed;
        if (AreaFnBrightnessHint != null) AreaFnBrightnessHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The Fn register and the lighting controller's brightness are separate on a model with a
    /// controller: Fn writes the EC, the sliders write the controller. The hint promises that
    /// moving a slider applies the saved preset, so the EC level is written too; without this the
    /// hint returned on every reopen because the Fn level never changed.
    /// </summary>
    private void PushFnBrightness(int level)
    {
        if (IgnoreLightingChanges || _syncEnabled || !_keyboardAvailable) return;

        // Null means the EC register could not be read on this model, so there is nothing to keep
        // in step and no point paying for an HID open on every slider move.
        if (_fnBrightnessLevel is null) return;

        // The register has five steps; writing 43 stores 50, so compare and cache the snapped value.
        level = AcerWmiManager.NearestKeyboardBrightness(level);
        if (_fnBrightnessLevel == level) return;

        _fnBrightnessPending = level;
        _fnBrightnessTimer.Stop();
        _fnBrightnessTimer.Start();
    }

    private void FlushFnBrightness()
    {
        _fnBrightnessTimer.Stop();
        if (_fnBrightnessPending is not { } level) return;
        _fnBrightnessPending = null;

        // Each write opens the EC HID and takes ~60-100 ms, so keep it off the UI thread.
        _ = Task.Run(() =>
        {
            var written = AcerWmiManager.SetKeyboardBrightness(level);
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!written) return;
                    _fnBrightnessLevel = level;
                    HideFnBrightnessHints();
                }));
            }
            catch (InvalidOperationException) { }
            catch (TaskCanceledException) { }
        });
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (Topmost || _isDialogOpen) return;

        WindowState = WindowState.Minimized;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _profileSyncTimer.Stop();
        _fnBrightnessTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerStateChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        StateChanged -= Window_StateChanged;
        SettingsManager.Save("WindowTop", (int)Top);
        SettingsManager.Save("WindowLeft", (int)Left);
        SettingsManager.Save("IsPinned", Topmost);

        (DataContext as DashboardViewModel)?.Dispose();
    }

    private FanCurveWindow? _activeCurveWindow;

    private void OpenCurveEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCurveWindow is { IsLoaded: true })
        {
            _activeCurveWindow.Activate();
            return;
        }

        _activeCurveWindow = new FanCurveWindow((DashboardViewModel)DataContext)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        var currentLeft = double.IsNaN(Left)
            ? (SystemParameters.WorkArea.Width / 2) - (Width / 2)
            : Left;
        var currentTop = double.IsNaN(Top)
            ? (SystemParameters.WorkArea.Height / 2) - (Height / 2)
            : Top;

        var targetLeft = currentLeft - _activeCurveWindow.Width - 10;
        if (targetLeft < 0) targetLeft = currentLeft + Width + 10;

        _activeCurveWindow.Left = targetLeft;
        _activeCurveWindow.Top = currentTop;

        _activeCurveWindow.Closed += (s, args) =>
        {
            _isDialogOpen = false;
            _activeCurveWindow = null;
        };

        _isDialogOpen = true;
        _activeCurveWindow.Show();
    }

    private readonly KeyboardColor[] _zoneColors =
    {
        KeyboardColor.FromHex("#B388FF"),
        KeyboardColor.FromHex("#B388FF"),
        KeyboardColor.FromHex("#B388FF"),
        KeyboardColor.FromHex("#B388FF")
    };

    private int _keyboardBrightness = 100;
    private int _keyboardSpeed = 4;
    private KeyboardEffect _keyboardEffect = KeyboardEffect.Static;
    private bool _keyboardAvailable;
    private bool _keyboardZoned = true;
    private bool _keyboardLoading = true;

    // Global writes the same effect to both devices; the logo keeps its own values for Area.
    private KeyboardEffect _globalEffect = KeyboardEffect.Static;
    private KeyboardColor _globalColor = KeyboardColor.FromHex("#B388FF");
    private int _globalBrightness = 100;
    private int _globalSpeed = 4;

    /// <summary>Software screen/audio sync, run by the tray. The dashboard only owns the switches.</summary>
    private bool _syncEnabled;
    private string _syncSource = LightingSync.SourceScreen;
    private bool _fnBrightnessReading;
    private DateTime _lastFnBrightnessReadUtc = DateTime.MinValue;
    private string _syncTarget = LightingSync.TargetBoth;
    private bool _syncToggleUpdating;
    private bool _screenPulse;
    private bool _appearanceLoading;
    private bool _coolBoostAvailable;
    private bool _coolBoostLoading;

    private KeyboardEffect _logoEffect = KeyboardEffect.Static;
    private KeyboardColor _logoColor = KeyboardColor.FromHex("#B388FF");
    private int _logoBrightness = 100;
    private int _logoSpeed = 3;

    private bool _blinkOnModeChange;

    // The lid logo cannot be discovered from the protocol: one question settles it, and the answer
    // is remembered. -1 not answered yet, 0 absent, 1 present.
    private bool _logoCheckBusy;
    private bool _captureMode;

    private static int LogoAvailability => SettingsManager.Get("KbLogoAvailable", -1);

    /// <summary>Bumped when the test colour changes, so an old ambiguous answer is asked again.</summary>
    private const int LogoCheckRevision = 2;

    /// <summary>
    /// Only write to the lid once the user has confirmed it exists. Unknown is treated as "not
    /// yet", so a model without a logo never receives a pointless write.
    /// </summary>
    private static bool LogoIsUsable => AcerKeyboard.IsAvailable() && LogoAvailability == 1;

    // The lighting write switches the backlight back on, but the auto-off is a separate countdown
    // on the EC that only a write carrying it arms. Debounced so dragging the brightness slider
    // costs one EC exchange at the end rather than one per tick.
    private readonly DispatcherTimer _autoOffRearmTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };

    /// <summary>
    /// The Fn backlight register is written from the LIGHT sliders too, but each write opens the
    /// EC HID (~60-100 ms), so a drag coalesces into one write.
    /// </summary>
    private readonly DispatcherTimer _fnBrightnessTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(350)
    };

    /// <summary>The level the Fn register was last seen or set to; null when it could not be read.</summary>
    private int? _fnBrightnessLevel;
    private int? _fnBrightnessPending;

    // The labels NitroSense shows, mapped onto the controller's own mode bytes. Three of them are
    // renamed by Acer: Ripple is Shifting (8), Snake is Meteor (10), Disco is Twinkling (11).
    private static readonly (string Name, KeyboardEffect Effect)[] KeyboardEffects =
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

    /// <summary>
    /// The Dynamic panel's list. Static is not an animation and has its own panel, so it is not
    /// offered here, which is also how the OEM software presents it.
    /// </summary>
    private static readonly (string Name, KeyboardEffect Effect)[] DynamicEffects =
    {
        ("Breathing", KeyboardEffect.Breathing),
        ("Neon", KeyboardEffect.Neon),
        ("Wave", KeyboardEffect.Wave),
        ("Ripple", KeyboardEffect.Shifting),
        ("Zoom", KeyboardEffect.Zoom),
        ("Snake", KeyboardEffect.Meteor),
        ("Disco", KeyboardEffect.Twinkling)
    };

    /// <summary>
    /// The cover logo only implements three of them. Selecting anything else leaves it on its
    /// previous effect rather than sending a mode the firmware would silently ignore.
    /// </summary>
    private static readonly (string Name, KeyboardEffect Effect)[] LogoEffects =
    {
        ("Static", KeyboardEffect.Static),
        ("Breathing", KeyboardEffect.Breathing),
        ("Neon", KeyboardEffect.Neon)
    };

    /// <summary>The logo exposes five speed steps where the keyboard exposes nine.</summary>
    private const int LogoMaxSpeed = 5;

    /// <summary>Animated effects use the speed byte; Static ignores it in the firmware.</summary>
    private static bool IsAnimated(KeyboardEffect effect) => effect != KeyboardEffect.Static;

    /// <summary>Older builds saved Acer's internal names. Keep them meaning the same effect.</summary>
    private static string MigrateEffectName(string? saved) => saved switch
    {
        "Shifting" => "Ripple",
        "Meteor" => "Snake",
        "Twinkling" => "Disco",
        _ => saved ?? "Static"
    };

    /// <summary>Backlight auto-off choices, in seconds (0 = never).</summary>
    private static readonly (string Name, int Seconds)[] KeyboardTimeouts =
    {
        ("Never", 0),
        ("30 seconds", 30),
        ("1 minute", 60),
        ("5 minutes", 300)
    };

    private bool _keyboardTimeoutAvailable;
    private bool _keyboardAutoOffViaWmi;

    /// <summary>Brightness on models with no lighting controller: EC HID or APGeAction.</summary>
    private bool _keyboardBrightnessAvailable;
    private bool _keyboardBrightnessViaWmi;
    private int _keyboardBrightnessLevel = 100;
    private bool _brightnessSyncUpdating;

    /// <summary>
    /// Reads both copies of the package power limit when PawnIO is installed, and offers the caps.
    /// Read-only until the user asks otherwise: nothing here writes until a value is chosen.
    /// </summary>
    private void RefreshCpuPowerLimits(PowerProfile mode, bool force = false)
    {
        // Keyed on the power source as well as the mode: Acer re-applies the limits when the adapter
        // comes out, and a write to 0x610 has been measured moving 0x59A0.
        var onBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus !=
                        System.Windows.Forms.PowerLineStatus.Online;
        if (!force && _powerLimitsKey == (mode, onBattery)) return;

        // A refusal describes the mode it happened in, so it should not outlive the mode.
        if (_powerLimitsKey is { } previous && previous.Mode != mode) _cpuPowerCapMessage = null;

        _powerLimitsKey = (mode, onBattery);

        _ = Task.Run(async () =>
        {
            // The EC applies a mode a moment after the request and MCHBAR follows it, so read after
            // it settles rather than catching the previous mode's numbers.
            await Task.Delay(1500);

            // A mode change does not rewrite the register, so a cap set in a higher mode can sit above
            // what the new mode asks for. Correct that before reading, never upward. A forced refresh
            // follows a write, where the register already holds the choice - skipping it keeps a
            // selection down to its own write and read-back instead of adding four more driver calls,
            // which is the shortest path on a machine where this IOCTL is audible.
            if (!force) CpuPowerCap.Reassert(mode);

            var firmware = CpuPowerLimits.ReadFirmware();
            var register = CpuPowerLimits.ReadRegister();
            if (firmware is null && register is null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_cpuLimitsPreview) return;

                    // No limits on this machine at all: the card does not exist on AMD Nitros.
                    if (!PawnIo.HasIntelCpu)
                    {
                        CpuPowerLimitsCard.Visibility = Visibility.Collapsed;
                        return;
                    }

                    ApplyCpuPowerLimitsUnavailable(
                        "PawnIO did not answer, so these limits cannot be read.",
                        "Check that the PawnIO driver is running; the log has the reason.");
                });
                return;
            }

            // Read here rather than on the UI thread: every one of these goes through the driver.
            var writable = CpuPowerCap.IsWritable();
            var ceilings = CpuPowerCap.Ceilings(mode);

            var source = onBattery ? "on battery" : "on AC";
            var detail = $"CPU power limits: {mode} mode {source}";
            if (firmware is { } asks)
                detail += $", MCHBAR 0x59A0 asks {asks.Pl1Watts:0} W / {asks.Pl2Watts:0} W" +
                          $"{(asks.Locked ? " (locked)" : "")}";
            if (register is { } holds)
                detail += $", MSR 0x610 holds {holds.Pl1Watts:0} W / {holds.Pl2Watts:0} W" +
                          $"{(holds.Locked ? " (locked)" : "")}";

            Logger.Info($"{detail}.");

            // Neither copy stands alone: 0x610 is seeded at boot and is what a tuning tool writes,
            // and writing it has been measured moving 0x59A0. Both are shown, labelled, unranked.
            var agree = firmware is { } left && register is { } right &&
                        Math.Abs(left.Pl1Watts - right.Pl1Watts) < 0.5 &&
                        Math.Abs(left.Pl2Watts - right.Pl2Watts) < 0.5;

            await Dispatcher.InvokeAsync(() =>
            {
                var text = $"{mode} mode";

                if (agree && register is { } same)
                {
                    text += $" · {same.Pl1Watts:0} W / {same.Pl2Watts:0} W";
                }
                else
                {
                    if (firmware is { } asking)
                        text += $" · asks {asking.Pl1Watts:0} W / {asking.Pl2Watts:0} W";
                    if (register is { } holding)
                        text += $" · register {holding.Pl1Watts:0} W / {holding.Pl2Watts:0} W";
                }

                if (onBattery) text += " · on battery";

                CpuPowerLimitsText.Text = text;
                CpuPowerLimitsCard.Visibility = Visibility.Visible;
                CpuPowerLimitsCaption.Visibility = Visibility.Visible;
                PopulateCpuPowerCap(ceilings, writable, mode);
            });
        });
    }

    /// <summary>
    /// Intel machines always see the card, so the feature is discoverable and says what it needs.
    /// Anything else keeps it hidden: these limits do not exist there, and an empty control would be
    /// noise on every AMD Nitro.
    /// </summary>
    private void PrepareCpuPowerLimits()
    {
        if (!PawnIo.HasIntelCpu) return;

        CpuPowerLimitsCard.Visibility = Visibility.Visible;
        if (PawnIo.IsInstalled) return;

        ShowCpuPowerLimitsInstallNeeded();
    }

    /// <summary>The card on an Intel machine where PawnIO is absent: present, explained, not usable.</summary>
    private void ShowCpuPowerLimitsInstallNeeded()
    {
        _cpuLimitsPreview = true;
        ApplyCpuPowerLimitsUnavailable("PawnIO is not installed, so these limits cannot be read.", string.Empty);
    }

    /// <summary>
    /// Capture-only: the Intel card with representative numbers. The card is hidden on this AMD machine,
    /// and a site screenshot of it has no other way to exist. Nothing is read or written; the values are
    /// the known ANV15-51 case - Balanced asks 45 W / 115 W, capped to 29 W sustained.
    /// </summary>
    private void ShowCpuPowerCapPreview()
    {
        _cpuLimitsPreview = true;

        CpuPowerLimitsCard.Visibility = Visibility.Visible;
        CpuPowerLimitsText.Text = "Balanced mode · asks 45 W / 115 W · register 29 W / 115 W";
        CpuPowerLimitsCaption.Visibility = Visibility.Visible;

        _cpuPowerCapUpdating = true;
        try
        {
            CpuPowerCapSlider.Minimum = CpuPowerCap.FloorWatts;
            CpuPowerCapSlider.Maximum = 45;
            CpuPowerCapSlider.Value = 29;

            CpuPowerCapBurstSlider.Minimum = 29;
            CpuPowerCapBurstSlider.Maximum = 115;
            CpuPowerCapBurstSlider.Value = 115;

            UpdateCpuPowerCapLabels(new CpuPowerCap.Limits(45, 115));
        }
        finally
        {
            _cpuPowerCapUpdating = false;
        }

        CpuPowerCapSlider.IsEnabled = true;
        CpuPowerCapBurstSlider.IsEnabled = true;
        CpuPowerCapApplyButton.IsEnabled = true;
        CpuPowerCapResetButton.IsEnabled = true;

        CpuPowerCapStatus.Text = "Holding 29 W sustained / 115 W burst.";
        CpuPowerCapStatus.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The single shape of "no numbers to show": present, explained, the controls inert. Both the
    /// not-installed and the did-not-answer cases go through here so they cannot drift apart.
    /// </summary>
    private void ApplyCpuPowerLimitsUnavailable(string text, string hint)
    {
        _cpuPowerCapUpdating = true;
        try
        {
            CpuPowerLimitsCard.Visibility = Visibility.Visible;
            CpuPowerLimitsText.Text = text;
            CpuPowerLimitsCaption.Visibility = Visibility.Collapsed;

            _cpuPowerCeiling = null;
            CpuPowerCapSlider.IsEnabled = false;
            CpuPowerCapBurstSlider.IsEnabled = false;
            CpuPowerCapApplyButton.IsEnabled = false;
            CpuPowerCapResetButton.IsEnabled = false;
            CpuPowerCapValueText.Text = "—";
            CpuPowerCapBurstValueText.Text = "—";

            CpuPowerCapStatus.Text = hint;
            CpuPowerCapStatus.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _cpuPowerCapUpdating = false;
        }
    }
    /// <summary>
    /// The GPU thermal target and clock cap. Both are read on a background thread: NVAPI and NVML
    /// initialisation are driver calls that block for a moment, and doing them while switching to this page
    /// made that switch feel slow. The cards render when the reads land; until then they keep what they had.
    /// </summary>
    private void RefreshGpuThermalTarget()
    {
        if (_gpuLimitsReading) return;
        _gpuLimitsReading = true;

        _ = Task.Run(() =>
        {
            GpuLimitState state;
            try
            {
                var mode = ActiveProfile();
                state = new GpuLimitState(
                    mode,
                    GpuThermalTarget.Read(),
                    GpuThermalTarget.SavedFor(mode),
                    GpuThermalTarget.Managing,
                    GpuClockLimit.MaximumMhz(),
                    GpuClockLimit.CurrentCapMhz(),
                    GpuClockLimit.SavedFor(mode));
            }
            catch (Exception ex)
            {
                Logger.Warn("GPU: the limit state could not be read.", ex);
                state = new GpuLimitState(
                    (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance),
                    null, 0, false, null, null, 0);
            }

            Dispatcher.InvokeAsync(() =>
            {
                _gpuLimitsReading = false;
                RenderGpuLimits(state);
            });
        });
    }

    /// <summary>
    /// Reads the BIOS MUX registers and the NVIDIA DDS status. On a DDS-capable machine the BIOS
    /// switch is not offered: the NVIDIA app changes the display live, and a stored BIOS mode would
    /// only apply at the next boot. Both reads are off the UI thread; neither writes anything.
    /// </summary>
    private void RefreshGpuMux()
    {
        if (_gpuMuxReading) return;
        _gpuMuxReading = true;

        _ = Task.Run(async () =>
        {
            GpuMuxState? state = null;
            bool? dds = null;
            try
            {
                state = await GpuMux.ReadAsync();

                // Only a mask that lists Automatic can be an Advanced Optimus machine. A mask of
                // Hybrid + Discrete is a BIOS MUX by definition (the ANV16-41 answers 3), so it is
                // shown without loading nvcpl.dll at all.
                if (state is { Supported: true, Automatic: true }) dds = NvidiaDds.IsSupported();
            }
            catch (Exception ex)
            {
                Logger.Warn("GPU MUX: the state could not be read.", ex);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _gpuMuxReading = false;
                if (DataContext is DashboardViewModel vm) vm.LoadGpuMux(state, dds == true);
            });
        });
    }

    private async void GpuMuxApply_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm || !vm.GpuMuxCanApply) return;

        var mode = vm.SelectedGpuMux;
        var answer = MessageBox.Show(
            $"Switch the GPU mode to {GpuMux.Name(mode)}?\n\n{GpuMux.Describe(mode)}\n\n" +
            "This is stored in the firmware and only takes effect after a restart. Save your work before restarting.",
            "Nitrous - GPU mode",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        vm.GpuMuxBusy = true;
        var result = await GpuMux.ApplyAsync(mode);
        vm.ShowGpuMuxResult(result);
    }

    private void GpuMuxRestart_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Restart now to apply the stored GPU mode?\n\nSave your work first.",
            "Nitrous - Restart",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "shutdown", "/r /t 5 /c \"Nitrous: applying the GPU mode\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("GPU MUX: the restart could not be started.", ex);
        }
    }

    private readonly record struct GpuLimitState(
        PowerProfile Mode,
        GpuThermalTarget.Limits? Thermal,
        int SavedThermal,
        bool Managing,
        int? MaximumMhz,
        int? CurrentCapMhz,
        int SavedCap);

    /// <summary>
    /// Paints the two limit sliders from a state that was read off the UI thread. The sliders are staged:
    /// nothing is written until Apply, so a slider position is a choice, not a command.
    /// </summary>
    private void RenderGpuLimits(GpuLimitState state)
    {
        if (DataContext is DashboardViewModel vm)
        {
            if (state.Thermal is { } policy)
            {
                vm.GpuThermalTargetNow = policy.Current;
                vm.GpuThermalMinimum = policy.Min;
                vm.GpuThermalMaximum = policy.Default;
                vm.GpuThermalLimit = state.SavedThermal == 0
                    ? policy.Default
                    : Math.Clamp(state.SavedThermal, policy.Min, policy.Default);
            }
            else
            {
                // Asleep or no policy: the slider cannot describe a range, so it is disabled and blank.
                vm.GpuThermalTargetNow = 0;
                vm.GpuThermalMinimum = 0;
                vm.GpuThermalMaximum = 0;
                vm.GpuThermalLimit = 0;
            }

            vm.GpuClockCapNow = state.CurrentCapMhz ?? 0;

            if (state.MaximumMhz is { } ceiling && ceiling > GpuClockLimit.FloorMhz)
            {
                vm.GpuClockMinimum = GpuClockLimit.FloorMhz;
                vm.GpuClockMaximum = ceiling;
                vm.GpuClockCap = state.SavedCap > 0 ? Math.Min(state.SavedCap, ceiling) : ceiling;
            }
            else
            {
                vm.GpuClockMinimum = 0;
                vm.GpuClockMaximum = 0;
                vm.GpuClockCap = 0;
            }

            vm.UpdateGpuLimitSummary();
        }

        GpuThermalHint.Text = state.Thermal is { } thermal
            ? $"Allowed {TemperatureDisplay.Short(thermal.Min)}\u2013{TemperatureDisplay.Short(thermal.Default)}; " +
              $"{TemperatureDisplay.Short(thermal.Default)} is the driver default. " +
              "Lower runs cooler and quieter at the cost of boost."
            : "The GPU is asleep, so the target cannot be read or set.";

        GpuClockLimitHint.Text = state.MaximumMhz is { } maximum && maximum > GpuClockLimit.FloorMhz
            ? $"Up to {maximum} MHz. The far right is no cap; drag it left to cap the boost."
            : "The GPU does not report a maximum clock while it is asleep.";
    }

    /// <summary>The shared "Manage GPU tuning" switch changed: apply the profile's values, then re-read.</summary>
    private void GpuTuning_Toggled(object sender, RoutedEventArgs e)
    {
        if (GpuThermalTarget.Managing)
        {
            GpuThermalTarget.Reassert(ActiveProfile());
            GpuClockLimit.Reassert(ActiveProfile());
        }

        RefreshGpuThermalTarget();
    }

    /// <summary>The profile the settings point at, falling back to what the EC reports.</summary>
    private static Nitrous.Enums.PowerProfile ActiveProfile() =>
        AcerWmiManager.GetEcPowerMode()
        ?? (Nitrous.Enums.PowerProfile)SettingsManager.Get("LastPowerMode", (int)Nitrous.Enums.PowerProfile.Performance);

    /// <summary>
    /// Fills both sliders from this mode's ceiling and stages what is in use. The sustained slider's top
    /// is the mode's own request and the burst slider starts where sustained sits, so an invalid pair
    /// cannot be chosen. A cap already above today's request keeps its value instead of being pulled down.
    /// </summary>
    private void PopulateCpuPowerCap(CpuPowerCap.Limits? ceilings, bool writable, PowerProfile profile)
    {
        _cpuPowerProfile = profile;

        _cpuPowerCapUpdating = true;
        try
        {
            if (ceilings is not { } mode)
            {
                _cpuPowerCeiling = null;
                CpuPowerCapSlider.IsEnabled = false;
                CpuPowerCapBurstSlider.IsEnabled = false;
                CpuPowerCapApplyButton.IsEnabled = false;
                CpuPowerCapResetButton.IsEnabled = false;
                return;
            }

            _cpuPowerCeiling = mode;

            var saved = CpuPowerCap.SavedFor(_cpuPowerProfile);
            var applied = CpuPowerCap.Applied() ?? mode;

            // The sliders reach up to the mode's request, but never hide the value the register or the
            // saved cap already holds while that request has dipped below it: the slider would otherwise
            // show 20 W over a register holding 25 W, which reads exactly like the cap being reset.
            var sustainedCeiling = Math.Max(CpuPowerCap.FloorWatts,
                Math.Max(Math.Min(mode.Pl1, mode.Pl2), Math.Max(saved.Pl1, applied.Pl1)));
            var burstCeiling = Math.Max(sustainedCeiling, Math.Max(mode.Pl2, Math.Max(saved.Pl2, applied.Pl2)));

            var sustained = Math.Clamp(saved.Pl1 > 0 ? saved.Pl1 : applied.Pl1,
                CpuPowerCap.FloorWatts, sustainedCeiling);
            var burst = Math.Clamp(saved.Pl2 > 0 ? saved.Pl2 : applied.Pl2,
                Math.Max(CpuPowerCap.FloorWatts, sustained), burstCeiling);

            CpuPowerCapSlider.Minimum = CpuPowerCap.FloorWatts;
            CpuPowerCapSlider.Maximum = sustainedCeiling;
            CpuPowerCapSlider.Value = sustained;

            CpuPowerCapBurstSlider.Minimum = sustained;
            CpuPowerCapBurstSlider.Maximum = burstCeiling;
            CpuPowerCapBurstSlider.Value = burst;

            UpdateCpuPowerCapLabels(mode);

            CpuPowerCapSlider.IsEnabled = writable;
            CpuPowerCapBurstSlider.IsEnabled = writable;
            CpuPowerCapApplyButton.IsEnabled = writable;
            CpuPowerCapResetButton.IsEnabled = writable;

            // Results and refusals land on the status line the way the other cards report state.
            CpuPowerCapStatus.Text = !writable
                ? "The firmware has locked the power limit until the next reboot."
                : _cpuPowerCapMessage
                  ?? (applied.Pl1 == mode.Pl1 && applied.Pl2 == mode.Pl2
                      ? "Following this mode's own limits."
                      : $"Holding {applied.Pl1} W sustained / {applied.Pl2} W burst.");
            CpuPowerCapStatus.Visibility =
                CpuPowerCapStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _cpuPowerCapUpdating = false;
        }
    }

    /// <summary>The live number beside each slider, and the ends of its range.</summary>
    private void UpdateCpuPowerCapLabels(CpuPowerCap.Limits? mode)
    {
        var sustained = (int)Math.Round(CpuPowerCapSlider.Value);
        var burst = (int)Math.Round(CpuPowerCapBurstSlider.Value);

        CpuPowerCapValueText.Text = $"{sustained} W";
        CpuPowerCapBurstValueText.Text = $"{burst} W";

        CpuPowerCapFloorText.Text = $"{CpuPowerCap.FloorWatts} W";
        CpuPowerCapBurstFloorText.Text = $"{sustained} W";

        // Only call the top "this mode" when it really is the mode's request; a value kept above a
        // request that has since dipped needs no such claim.
        var top = (int)Math.Round(CpuPowerCapSlider.Maximum);
        CpuPowerCapCeilingText.Text = mode?.Pl1 == top ? $"{top} W · this mode" : $"{top} W";
        CpuPowerCapBurstCeilingText.Text = $"{Math.Round(CpuPowerCapBurstSlider.Maximum)} W";
    }

    private void CpuPowerCapSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Minimum/Maximum in the markup fire this while the window is still being parsed, before the
        // elements declared below the slider exist; there is nothing to show yet in that case.
        if (_cpuPowerCapUpdating || !IsLoaded) return;

        // PL1 above PL2 is not a valid pair, so raising sustained pushes burst along with it.
        CpuPowerCapBurstSlider.Minimum = CpuPowerCapSlider.Value;
        UpdateCpuPowerCapLabels(_cpuPowerCeiling);
    }

    private void CpuPowerCapBurstSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cpuPowerCapUpdating || !IsLoaded) return;
        UpdateCpuPowerCapLabels(_cpuPowerCeiling);
    }

    private async void CpuPowerCapApply_Click(object sender, RoutedEventArgs e) =>
        await ApplyCpuPowerCapAsync();

    /// <summary>
    /// Returns both limits to this mode's own values. The sliders land on the mode's request and that pair
    /// is applied like any other, so the register keeps following the mode and the saved cap is cleared.
    /// </summary>
    private async void CpuPowerCapReset_Click(object sender, RoutedEventArgs e)
    {
        if (_cpuPowerCapUpdating) return;
        if (_cpuPowerCeiling is not { } mode) return;

        _cpuPowerCapUpdating = true;
        try
        {
            CpuPowerCapSlider.Minimum = CpuPowerCap.FloorWatts;
            CpuPowerCapSlider.Maximum = Math.Max(CpuPowerCap.FloorWatts, mode.Pl1);
            CpuPowerCapSlider.Value = CpuPowerCapSlider.Maximum;

            CpuPowerCapBurstSlider.Minimum = CpuPowerCapSlider.Value;
            CpuPowerCapBurstSlider.Maximum = Math.Max(CpuPowerCapSlider.Value, mode.Pl2);
            CpuPowerCapBurstSlider.Value = CpuPowerCapBurstSlider.Maximum;

            UpdateCpuPowerCapLabels(mode);
        }
        finally
        {
            _cpuPowerCapUpdating = false;
        }

        await ApplyCpuPowerCapAsync();
    }

    private async Task ApplyCpuPowerCapAsync()
    {
        if (_cpuPowerCapUpdating) return;
        if (_cpuPowerCeiling is null) return;

        var sustained = (int)Math.Round(CpuPowerCapSlider.Value);
        var burst = (int)Math.Round(CpuPowerCapBurstSlider.Value);

        CpuPowerCapSlider.IsEnabled = false;
        CpuPowerCapBurstSlider.IsEnabled = false;
        CpuPowerCapApplyButton.IsEnabled = false;
        CpuPowerCapResetButton.IsEnabled = false;

        var result = await Task.Run(() => CpuPowerCap.Apply(_cpuPowerProfile, sustained, burst));

        if (result.Ok && CpuPowerCap.Ceilings(_cpuPowerProfile) is { } ceilings)
            CpuPowerCap.SaveFor(_cpuPowerProfile,
                sustained == ceilings.Pl1 ? 0 : sustained,
                burst == ceilings.Pl2 ? 0 : burst);

        // The window can be closed while a write is in flight; nothing here is safe to touch then.
        if (Dispatcher.HasShutdownStarted) return;

        _cpuPowerCapMessage = result.Ok ? null : result.Message;
        CpuPowerCapStatus.Text = result.Message;
        CpuPowerCapStatus.Visibility = Visibility.Visible;

        // The register line on the card changed with the write; rebuild it instead of leaving the
        // numbers from before the write on screen.
        if (_powerLimitsKey is { } key) RefreshCpuPowerLimits(key.Mode, force: true);
        else
        {
            CpuPowerCapSlider.IsEnabled = true;
            CpuPowerCapBurstSlider.IsEnabled = true;
            CpuPowerCapApplyButton.IsEnabled = true;
            CpuPowerCapResetButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Loads the saved lighting state and shows it. The keyboard's lighting interface is a
    /// separate HID device; models without it keep the card visible but disabled.
    /// </summary>
    private void InitializeKeyboardLighting()
    {
        // The old red test was ambiguous with the Performance mode. A Yes from it is cleared once;
        // the green test (revision 2) asks again, and anything but an explicit Yes falls back to
        // Keyboard for the sync target.
        if (SettingsManager.Get("KbLogoCheckRevision", 1) < LogoCheckRevision)
        {
            SettingsManager.Save("KbLogoCheckRevision", LogoCheckRevision);
            // Absent means "not answered", which is what the green test needs to run again.
            if (SettingsManager.Get("KbLogoAvailable", -1) == 1)
                SettingsManager.Delete("KbLogoAvailable");
        }

        _keyboardAvailable = KeyboardLighting.IsAvailable();

        // A model with no lighting controller gets no LIGHT tab at all. A page that can only say it
        // cannot do anything is worse than no page: it invites a click that can never work.
        if (!_keyboardAvailable)
        {
            NavKeyboardBtn.Visibility = Visibility.Collapsed;
            NavKeyboardColumn.Width = new GridLength(0);
        }

        // SMBIOS says whether the keyboard has one colour or RGB. A single-colour keyboard can
        // still have a backlight, so only the zones are withheld: offering four swatches on a
        // keyboard that cannot light them is what made this look broken on an unlisted model.
        _keyboardZoned = AcerCapabilitiesProvider.Current.Smbios.KeyboardColor is not 1;

        for (var i = 0; i < AcerKeyboard.ZoneCount; i++)
            _zoneColors[i] = KeyboardColor.FromHex(SettingsManager.Get($"KbZone{i + 1}", _zoneColors[i].ToHex()));

        _keyboardBrightness = Math.Clamp(SettingsManager.Get("KbBrightness", 100), 0, 100);

        _dynamicEffects = DynamicEffects.Where(e => KeyboardLighting.SupportsEffect(e.Effect)).ToArray();
        _keyboardEffects = KeyboardEffects.Where(e => KeyboardLighting.SupportsEffect(e.Effect)).ToArray();

        foreach (var (name, _) in _dynamicEffects)
            KeyboardEffectBox.Items.Add(name);

        foreach (var (name, _) in _keyboardEffects)
            GlobalEffectBox.Items.Add(name);

        foreach (var (name, _) in LogoEffects)
            LogoEffectBox.Items.Add(name);

        _keyboardEffect = LoadEffect(KeyboardEffectBox, _dynamicEffects, "KbEffect");
        _globalEffect = LoadEffect(GlobalEffectBox, _keyboardEffects, "KbGlobal_Effect");
        _logoEffect = LoadEffect(LogoEffectBox, LogoEffects, "KbLogo_Effect");

        _keyboardSpeed = Math.Clamp(
            SettingsManager.Get("KbSpeed", 4), AcerKeyboard.MinSpeed, AcerKeyboard.MaxSpeed);
        KbSpeed.Value = _keyboardSpeed;
        KbSpeedText.Text = _keyboardSpeed.ToString();

        _globalColor = KeyboardColor.FromHex(
            SettingsManager.Get("KbGlobal_Color", _zoneColors[0].ToHex()));
        _globalBrightness = Math.Clamp(SettingsManager.Get("KbGlobal_Brightness", 100), 0, 100);

        // Global speed is five steps, matching the OEM software: keyboard 1/3/5/7/9 and logo 1/2/3/4/5.
        _globalSpeed = Math.Clamp(SettingsManager.Get("KbGlobal_Speed", 3), 1, LogoMaxSpeed);
        GlobalBrightness.Value = _globalBrightness;
        GlobalBrightnessText.Text = $"{_globalBrightness}%";
        GlobalSpeed.Value = _globalSpeed;
        GlobalSpeedText.Text = _globalSpeed.ToString();

        _logoColor = KeyboardColor.FromHex(
            SettingsManager.Get("KbLogo_Color", _zoneColors[0].ToHex()));
        _logoBrightness = Math.Clamp(SettingsManager.Get("KbLogo_Brightness", 100), 0, 100);
        _logoSpeed = Math.Clamp(SettingsManager.Get("KbLogo_Speed", 3), 1, LogoMaxSpeed);
        LogoBrightness.Value = _logoBrightness;
        LogoBrightnessText.Text = $"{_logoBrightness}%";
        LogoSpeed.Value = _logoSpeed;
        LogoSpeedText.Text = _logoSpeed.ToString();

        _blinkOnModeChange = SettingsManager.Get("KbBlinkOnMode", true);
        TogBlinkOnMode.IsChecked = _blinkOnModeChange;

        // The worker lives in the tray; these switches only write the request.
        var syncEnabled = SettingsManager.Get("KbSyncEnabled", -1);
        if (syncEnabled < 0)
        {
            // Migration from the screen-only prototype.
            syncEnabled = SettingsManager.Get("KbScreenSync", 0) == 1 ? 1 : 0;
            SettingsManager.Save("KbSyncEnabled", syncEnabled);
            if (syncEnabled == 1) SettingsManager.Save("KbSyncSource", LightingSync.SourceScreen);
        }

        _syncEnabled = syncEnabled == 1;
        _syncSource = LightingSync.Normalize(
            SettingsManager.Get("KbSyncSource", LightingSync.SourceScreen));
        _syncTarget = LightingSync.NormalizeTarget(
            SettingsManager.Get("KbSyncTarget", LightingSync.TargetBoth));

        _screenPulse = SettingsManager.Get("KbScreenPulse", 0) == 1;
        TogScreenPulse.IsChecked = _screenPulse;

        var floor = Math.Clamp(SettingsManager.Get("KbSyncFloor", 0), 0, 10);
        ScreenFloor.Value = floor;
        ScreenFloorText.Text = $"{floor}%";

        var sensitivity = Math.Clamp(SettingsManager.Get("KbSoundSensitivity", 4), 1, 10);
        SoundSensitivity.Value = sensitivity;
        SoundSensitivityText.Text = sensitivity.ToString();

        var saturation = Math.Clamp(SettingsManager.Get("KbSyncSaturation", 100), 0, 200);
        ScreenSaturation.Value = saturation;
        ScreenSaturationText.Text = $"{saturation}%";

        var soundMode = SettingsManager.Get("KbSoundMode", LightingSync.SoundModeSpectrum) switch
        {
            var m when string.Equals(m, LightingSync.SoundModePeak, StringComparison.OrdinalIgnoreCase)
                => LightingSync.SoundModePeak,
            var m when string.Equals(m, LightingSync.SoundModeBeat, StringComparison.OrdinalIgnoreCase)
                => LightingSync.SoundModeBeat,
            var m when string.Equals(m, LightingSync.SoundModeVibe, StringComparison.OrdinalIgnoreCase)
                => LightingSync.SoundModeVibe,
            _ => LightingSync.SoundModeSpectrum
        };

        SoundModeBox.Items.Add("Peak");
        SoundModeBox.Items.Add("Spectrum");
        SoundModeBox.Items.Add("Beat");
        SoundModeBox.Items.Add("Vibe");
        SoundModeBox.SelectedIndex = soundMode == LightingSync.SoundModePeak ? 0
            : soundMode == LightingSync.SoundModeBeat ? 2
            : soundMode == LightingSync.SoundModeVibe ? 3
            : 1;

        SyncTargetBox.Items.Add("Keyboard");
        SyncTargetBox.Items.Add("Keyboard + lid logo");
        SyncTargetBox.SelectedIndex = _syncTarget == LightingSync.TargetKeyboard ? 0 : 1;

        TogScreenSync.IsChecked = _syncEnabled && _syncSource == LightingSync.SourceScreen;
        TogSoundSync.IsChecked = _syncEnabled && _syncSource == LightingSync.SourceSound;

        // The source is restored below from the saved tab. Opening the dashboard must not record
        // the tab it happens to start on: doing so overwrote a saved Area preset with Global, so
        // the next sync frame switched the keyboard back to the Global effect.

        // Restore which half of the keyboard card was last in use, before the first apply below.
        if (SettingsManager.Get("KbStyle", "Static") == "Dynamic")
            KbDynamicTab.IsChecked = true;

        PaintColourButton(GlobalColorBtn, _globalColor);
        PaintColourButton(LogoColorBtn, _logoColor);

        KeyboardBrightness.Value = _keyboardBrightness;
        KeyboardBrightnessText.Text = $"{_keyboardBrightness}%";

        // The auto-off lives on the embedded controller, not the lighting controller, so it can be
        // missing even where the zones work. A controller that answers but stores nothing is
        // remembered as not supporting it, so the box does not look usable on every launch.
        var timeoutRefused = SettingsManager.Get("KbTimeoutRefused", 0) == 1;
        var controllerTimeout = timeoutRefused ? null : AcerWmiManager.GetKeyboardBacklightTimeout();
        _keyboardTimeoutAvailable = controllerTimeout is not null;

        // Where there is no EC HID, the same setting is a firmware timeout reachable over
        // APGeAction. It is only a 30-second on/off there, so the list is trimmed to those two.
        _keyboardAutoOffViaWmi = false;
        bool? firmwareAutoOff = null;
        if (!_keyboardTimeoutAvailable)
        {
            firmwareAutoOff = AcerWmiManager.GetKeyboardAutoOff();
            _keyboardAutoOffViaWmi = firmwareAutoOff.HasValue;
        }

        var usable = _keyboardTimeoutAvailable || _keyboardAutoOffViaWmi;

        var offeredTimeouts = _keyboardAutoOffViaWmi
            ? KeyboardTimeouts.Take(2).ToArray()
            : KeyboardTimeouts;

        foreach (var (name, _) in offeredTimeouts)
            KeyboardTimeoutBox.Items.Add(name);

        // The controller is the truth. NitroSense, a firmware reset or another tool can change the
        // auto-off without Nitrous knowing, and showing the saved value would then claim a state the
        // keyboard is not in. An answer that is not one of the offered values is kept as the saved
        // preference and logged rather than silently rounded to "Never".
        var savedTimeout = SettingsManager.Get("KbBacklightTimeout", 0);
        if (controllerTimeout is { } reported &&
            Array.Exists(KeyboardTimeouts, t => t.Seconds == reported.TimeoutSeconds))
        {
            savedTimeout = reported.TimeoutSeconds;
            if (savedTimeout != SettingsManager.Get("KbBacklightTimeout", 0))
                SettingsManager.Save("KbBacklightTimeout", savedTimeout);
        }
        else if (controllerTimeout is { } unexpected)
        {
            Logger.Info($"Keyboard: controller reports an auto-off of {unexpected.TimeoutSeconds}s, " +
                        "which is not one of the offered values; keeping the saved preference.");
        }
        else if (firmwareAutoOff is { } on)
        {
            savedTimeout = on ? 30 : 0;
        }

        var timeoutIndex = Array.FindIndex(KeyboardTimeouts, t => t.Seconds == savedTimeout);
        KeyboardTimeoutBox.SelectedIndex = timeoutIndex >= 0 ? timeoutIndex : 0;

        KeyboardTimeoutBox.IsEnabled = usable;
        KeyboardTimeoutBox.ToolTip = usable
            ? null
            : timeoutRefused
                ? "This keyboard controller does not store an auto-off."
                : "Nitrous cannot reach this setting on this model; the OEM software may still offer it.";

        // Brightness on a model with no lighting controller. The EC HID keeps it on the newer units;
        // APGeAction keeps it on the WMI-only ones. ENEK models already have it on the LIGHT page.
        var wmiBrightness = _keyboardTimeoutAvailable ? null : AcerWmiManager.GetWmiKeyboardBacklight();
        _keyboardBrightnessViaWmi = wmiBrightness is not null;
        _keyboardBrightnessAvailable = !_keyboardAvailable &&
            (_keyboardTimeoutAvailable || _keyboardBrightnessViaWmi);

        // Learn which steps this keyboard keeps, once. henry's ANV15-41 takes off, 50 and 100 and
        // refuses 25 and 75, so the box and the beat effect were writing levels it discards.
        if (_keyboardBrightnessViaWmi)
            AcerWmiManager.ProbeKeyboardBrightnessLevels();

        var savedBrightness = SettingsManager.Get("KbBrightLevel", 100);
        if (_keyboardBrightnessViaWmi && wmiBrightness is { } wmiReported &&
            AcerWmiManager.KeyboardBrightnessLevels.Contains(wmiReported.Brightness))
        {
            // The firmware's own level is the truth where it reports one of the steps.
            savedBrightness = wmiReported.Brightness;
        }

        _keyboardBrightnessLevel = AcerWmiManager.NearestKeyboardBrightness(savedBrightness);

        var brightnessLevels = AcerWmiManager.KeyboardBrightnessLevels;
        foreach (var level in brightnessLevels)
            KeyboardBrightnessBox.Items.Add(level == 0 ? "Off" : $"{level}%");

        for (var i = 0; i < brightnessLevels.Count; i++)
        {
            if (brightnessLevels[i] != _keyboardBrightnessLevel) continue;

            KeyboardBrightnessBox.SelectedIndex = i;
            break;
        }

        KeyboardBrightnessCard.Visibility = _keyboardBrightnessAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        KeyboardBrightnessBox.IsEnabled = _keyboardBrightnessAvailable;

        var brightnessSyncOn = _keyboardBrightnessAvailable
            && SettingsManager.Get("KbSyncEnabled", 0) == 1
            && string.Equals(SettingsManager.Get("KbSyncSource", ""),
                LightingSync.SourceSound, StringComparison.OrdinalIgnoreCase);

        _brightnessSyncUpdating = true;
        try
        {
            TogBrightnessSync.IsChecked = brightnessSyncOn;

            var brightnessMode = SettingsManager.Get(
                "KbBrightSyncMode", LightingSync.SoundModeVibe);
            BrightnessSyncModeBox.SelectedIndex =
                string.Equals(brightnessMode, LightingSync.SoundModePeak, StringComparison.OrdinalIgnoreCase)
                    ? 2
                    : string.Equals(brightnessMode, LightingSync.SoundModeBeat, StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : 0;

            var brightnessSensitivity = Math.Clamp(SettingsManager.Get("KbSoundSensitivity", 4), 1, 10);
            BrightnessSyncSensitivity.Value = brightnessSensitivity;
            BrightnessSyncSensitivityText.Text = brightnessSensitivity.ToString();
        }
        finally
        {
            _brightnessSyncUpdating = false;
        }

        UpdateBrightnessSyncUi();

        // A one-zone keyboard has no effects or colour sync to cycle; sound sync through the
        // five-step backlight is the only lighting hotkey it can offer.
        var zonedLighting = _keyboardAvailable && _keyboardZoned;
        HkScreenSyncRow.Visibility = zonedLighting ? Visibility.Visible : Visibility.Collapsed;
        HkEffectRow.Visibility = zonedLighting ? Visibility.Visible : Visibility.Collapsed;
        HkSoundSyncRow.Visibility = zonedLighting || _keyboardBrightnessAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateZoneButtons();

        KeyboardLightingStatus.Text = !_keyboardAvailable
            ? "Colours not reachable"
            : !_keyboardZoned
                ? "Single-colour keyboard"
                : string.Empty;

        UpdateLightingInteractivity();
        UpdateLogoAvailability();

        // The tabs are a preset toggle: open the one that was last used, and apply it below.
        var source = SettingsManager.Get("KbLightingSource", "Area");
        if (source == "Area") LightingAreaTab.IsChecked = true;

        _keyboardLoading = false;

        // The tray applies the saved preset once at startup. Opening the dashboard must not
        // rewrite the hardware brightness: the user may have changed it with the Fn keys since,
        // and showing a window should not undo that. The controls apply on user changes below.

        // The tab/mode controls may have been initialised after the first pass; apply the final
        // sync gating once more so disabled colours and zone swatches match the loaded state.
        UpdateLightingInteractivity();
    }

    /// <summary>
    /// Selects a saved effect by its display name, tolerating the names older builds saved.
    /// </summary>
    private static KeyboardEffect LoadEffect(
        System.Windows.Controls.ComboBox box,
        IReadOnlyList<(string Name, KeyboardEffect Effect)> table,
        string settingKey)
    {
        var saved = MigrateEffectName(SettingsManager.Get(settingKey, "Static"));
        var index = 0;
        for (var i = 0; i < table.Count; i++)
        {
            if (table[i].Name != saved) continue;
            index = i;
            break;
        }

        box.SelectedIndex = index;
        return table[index].Effect;
    }

    private bool IgnoreLightingChanges => _keyboardLoading;

    /// <summary>
    /// Paints a colour chip so it shows the colour it selects. The text flips to black on light
    /// colours, because white on yellow is unreadable and the chip is the only feedback there is.
    /// </summary>
    private static void PaintColourButton(
        System.Windows.Controls.Button button, KeyboardColor color, string? text = null)
    {
        button.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        var luma = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        button.Foreground = luma > 0.55
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.White;
        button.Content = text ?? color.ToHex();
    }

    private void UpdateZoneButtons()
    {
        var buttons = new[] { Zone1Btn, Zone2Btn, Zone3Btn, Zone4Btn };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Background = new SolidColorBrush(
                Color.FromRgb(_zoneColors[i].R, _zoneColors[i].G, _zoneColors[i].B));
        }

        // The ALL tile shows the four zones side by side, so it reads as "the whole keyboard"
        // rather than as a fifth zone.
        ZoneAllBtn.Background = BuildZonePreview();

        // Single-colour keyboards get one chip instead of the four swatches.
        PaintColourButton(KbSingleColorBtn, _zoneColors[0]);
    }

    private System.Windows.Media.Brush BuildZonePreview()
    {
        var first = _zoneColors[0];
        var uniform = true;
        foreach (var color in _zoneColors)
        {
            if (color.R != first.R || color.G != first.G || color.B != first.B)
            {
                uniform = false;
                break;
            }
        }

        if (uniform)
            return new SolidColorBrush(Color.FromRgb(first.R, first.G, first.B));

        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0)
        };

        for (var i = 0; i < _zoneColors.Length; i++)
        {
            var color = _zoneColors[i];
            brush.GradientStops.Add(new GradientStop(
                Color.FromRgb(color.R, color.G, color.B),
                i / (double)(_zoneColors.Length - 1)));
        }

        return brush;
    }

    /// <summary>Zone colours only mean anything in the keyboard's Static panel.</summary>
    private void UpdateZoneInteractivity()
    {
        var screenSync = _syncEnabled && _syncSource == LightingSync.SourceScreen;
        var enabled = _keyboardAvailable && _keyboardZoned && KbStaticTab.IsChecked == true &&
                      !screenSync;

        Zone1Btn.IsEnabled = Zone2Btn.IsEnabled = Zone3Btn.IsEnabled = Zone4Btn.IsEnabled = enabled;
        ZoneAllBtn.IsEnabled = enabled;

        ZoneRow.Opacity = screenSync ? 0.4 : 1.0;
        SingleColourRow.Opacity = screenSync ? 0.4 : 1.0;

        // A single-colour keyboard still has a colour; offering four disabled swatches left no way
        // to set it at all.
        ZoneRow.Visibility = _keyboardZoned ? Visibility.Visible : Visibility.Collapsed;
        SingleColourRow.Visibility = _keyboardZoned ? Visibility.Collapsed : Visibility.Visible;
        KbSingleColorBtn.IsEnabled = _keyboardAvailable && !screenSync;

        var hint = screenSync
            ? "Screen sync uses the screen's colours."
            : null;
        Zone1Btn.ToolTip = hint ?? "Zone 1 colour";
        Zone2Btn.ToolTip = hint ?? "Zone 2 colour";
        Zone3Btn.ToolTip = hint ?? "Zone 3 colour";
        Zone4Btn.ToolTip = hint ?? "Zone 4 colour";
        ZoneAllBtn.ToolTip = hint ?? "Set all four zones to one colour.";
        KbSingleColorBtn.ToolTip = hint ?? "The keyboard's colour";

        ZoneHint.Text = "This keyboard has a single colour, so one colour applies to the whole keyboard.";
        ZoneHint.Visibility = !_keyboardZoned ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Enables only what can act. Screen sync owns the colours and disables the effect list;
    /// sound sync keeps it editable so the selected effect can run with audio-driven brightness.
    /// The speed row disappears for Static because the firmware ignores it there, and the logo's
    /// colour greys to "Auto" for the two modes that cycle their own palette rather than using the
    /// colour sent.
    /// </summary>
    private void UpdateLightingInteractivity()
    {
        var syncOn = _syncEnabled;
        var screenSync = syncOn && _syncSource == LightingSync.SourceScreen;
        var soundSync = syncOn && _syncSource == LightingSync.SourceSound;

        // Screen sync owns the colours and disables the list; sound sync leaves it editable so its
        // selected effect can run while the audio drives brightness.
        var globalAnimated = IsAnimated(_globalEffect);
        var globalHasColour = _globalEffect == KeyboardEffect.Static;

        // A WMI BIOS that declares the record's input as a number still takes zone colours, but
        // has no byte for the effect or the brightness. Offering either would look like it worked.
        GlobalEffectBox.IsEnabled = _keyboardAvailable && !screenSync && KeyboardLighting.EffectsWritable;
        GlobalBrightness.IsEnabled = _keyboardAvailable && KeyboardLighting.EffectsWritable;
        GlobalEffectBox.ToolTip = KeyboardLighting.EffectsWritable
            ? null
            : "This model's lighting takes zone colours only.";
        GlobalSpeed.IsEnabled = _keyboardAvailable && globalAnimated && (!syncOn || soundSync) &&
                                KeyboardLighting.EffectsWritable;
        GlobalSpeedRow.Visibility = globalAnimated ? Visibility.Visible : Visibility.Collapsed;

        var syncAvailable = _keyboardAvailable && _keyboardZoned;
        TogScreenSync.IsEnabled = syncAvailable;
        TogSoundSync.IsEnabled = syncAvailable;

        SoundModeRow.Visibility = syncOn && _syncSource == LightingSync.SourceSound
            ? Visibility.Visible
            : Visibility.Collapsed;
        SoundSensitivityRow.Visibility =
            (syncOn && _syncSource == LightingSync.SourceSound) ||
            (screenSync && _screenPulse)
                ? Visibility.Visible
                : Visibility.Collapsed;
        ScreenSaturationRow.Visibility = syncOn && _syncSource == LightingSync.SourceScreen
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScreenPulseRow.Visibility = screenSync ? Visibility.Visible : Visibility.Collapsed;
        ScreenFloorRow.Visibility = screenSync && _screenPulse
            ? Visibility.Visible
            : Visibility.Collapsed;
        TogScreenPulse.IsEnabled = screenSync;
        SyncTargetRow.Visibility = syncOn ? Visibility.Visible : Visibility.Collapsed;

        // A model without a lid cannot follow it; fall back to the keyboard rather than writing
        // logo packets that go nowhere.
        if (!LogoIsUsable && _syncTarget == LightingSync.TargetBoth)
        {
            _syncTarget = LightingSync.TargetKeyboard;
            SyncTargetBox.SelectedIndex = 0;
            SettingsManager.Save("KbSyncTarget", _syncTarget);
        }

        SoundModeBox.IsEnabled = syncOn;
        SyncTargetBox.IsEnabled = syncOn && LogoIsUsable;
        TogScreenSync.ToolTip = !_keyboardAvailable
            ? "This model has no lighting controller."
            : !_keyboardZoned
                ? "Screen sync needs the four-zone lighting controller."
                : null;
        TogSoundSync.ToolTip = !_keyboardAvailable
            ? "This model has no lighting controller."
            : !_keyboardZoned
                ? "Sound sync needs the four-zone lighting controller."
                : null;

        // The animated effects run their own palette, so a chosen colour does nothing for either
        // device. It is greyed to "Auto" rather than left looking usable. Screen sync owns the
        // colour entirely; Sound sync uses the preset colour as its base.
        var globalColourEditable = globalHasColour && !screenSync;
        GlobalColorBtn.IsEnabled = _keyboardAvailable && globalColourEditable;
        GlobalColorBtn.ToolTip = screenSync
            ? "Screen sync uses the screen's colours."
            : syncOn
                ? "Sound sync uses this as its base colour."
                : globalHasColour
                    ? "Keyboard and cover-logo colour"
                    : "Only Static uses a chosen colour; the animated effects run their own palette.";
        if (globalColourEditable) PaintColourButton(GlobalColorBtn, _globalColor);
        else PaintColourButton(GlobalColorBtn, new KeyboardColor(60, 60, 60), "Auto");

        KbStaticTab.IsEnabled = !screenSync;
        KbDynamicTab.IsEnabled = !screenSync;
        ScreenSyncKeyboardHint.Text = screenSync
            ? "Screen sync controls the colours; brightness is the cap."
            : "Sound sync follows the selected effect; the lid stays static.";
        ScreenSyncKeyboardHint.Visibility = screenSync || soundSync
            ? Visibility.Visible
            : Visibility.Collapsed;

        KeyboardEffectBox.IsEnabled = _keyboardAvailable && !screenSync && KeyboardLighting.EffectsWritable;
        KeyboardBrightness.IsEnabled = _keyboardAvailable && KeyboardLighting.EffectsWritable;
        KeyboardEffectBox.ToolTip = KeyboardLighting.EffectsWritable
            ? null
            : "This model's lighting takes zone colours only.";
        KbSpeed.IsEnabled = _keyboardAvailable && IsAnimated(_keyboardEffect) && !screenSync &&
                            KeyboardLighting.EffectsWritable;
        KbSpeedRow.Visibility = KbDynamicTab.IsChecked == true && IsAnimated(_keyboardEffect)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var logoHasColour = _logoEffect == KeyboardEffect.Static;
        var logoOwnedBySync = syncOn && _syncTarget == LightingSync.TargetBoth;
        LogoEffectBox.IsEnabled = _keyboardAvailable && !logoOwnedBySync;
        LogoBrightness.IsEnabled = _keyboardAvailable && !logoOwnedBySync;
        LogoSpeed.IsEnabled = _keyboardAvailable && IsAnimated(_logoEffect) && !logoOwnedBySync;
        LogoSpeedRow.Visibility = IsAnimated(_logoEffect) ? Visibility.Visible : Visibility.Collapsed;
        LogoColorBtn.IsEnabled = _keyboardAvailable && logoHasColour && !logoOwnedBySync;
        LogoColorBtn.ToolTip = logoOwnedBySync
            ? "Sync is controlling the lid."
            : logoHasColour
                ? "Cover-logo colour"
                : "Breathing and Neon cycle their own colours; the firmware ignores the one sent.";
        LogoCheckBtn.IsEnabled = _keyboardAvailable && !logoOwnedBySync;
        LogoCheckBtn.ToolTip = logoOwnedBySync ? "Sync is controlling the lid." : null;
        LogoSyncHint.Visibility = logoOwnedBySync ? Visibility.Visible : Visibility.Collapsed;

        if (logoHasColour) PaintColourButton(LogoColorBtn, _logoColor);
        else PaintColourButton(LogoColorBtn, new KeyboardColor(60, 60, 60), "Auto");

        UpdateZoneInteractivity();
    }

    /// <summary>
    /// Global and Area are two presets shown one at a time. Switching between them applies the one
    /// being opened, the way a preset toggle works: Global writes both devices, Area writes each
    /// device from its own saved settings.
    /// </summary>
    private void LightingTab_Checked(object sender, RoutedEventArgs e)
    {
        if (GlobalPanel == null || AreaPanel == null) return;

        var global = LightingGlobalTab.IsChecked == true;
        GlobalPanel.Visibility = global ? Visibility.Visible : Visibility.Collapsed;
        AreaPanel.Visibility = global ? Visibility.Collapsed : Visibility.Visible;

        // Not during load: the first apply is done deliberately at the end of initialisation.
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        // Lighting sync owns the LEDs while Global is open. Opening Area suspends it but keeps the
        // switches: the tray stops and the Area preset is applied. Returning to Global resumes it.
        // Sync owns the LEDs while it runs. Switching tabs only records which preset will return
        // when it is turned off; applying one now would be overwritten within a frame.
        if (_syncEnabled)
        {
            SettingsManager.Save("KbLightingSource", global ? "Global" : "Area");
            return;
        }

        if (global) ApplyGlobalLighting();
        else ApplyAreaLighting();
    }

    /// <summary>Applies both Area device configurations: keyboard and cover logo.</summary>
    private void ApplyAreaLighting(bool reportLogoFailure = true)
    {
        HideFnBrightnessHints();
        ApplyKeyboardLighting();
        ApplyLogoLighting(reportLogoFailure);
    }

    /// <summary>Area splits into the two devices the controller actually exposes.</summary>
    private void AreaTab_Checked(object sender, RoutedEventArgs e)
    {
        if (AreaKeyboardPanel == null || AreaLogoPanel == null) return;

        var keyboard = AreaKeyboardTab.IsChecked == true;
        AreaKeyboardPanel.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
        AreaLogoPanel.Visibility = keyboard ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Static and Dynamic are presets too: switching applies the selected one straight away, so it
    /// does not need an unrelated edit to take effect.
    /// </summary>
    private void KbMode_Checked(object sender, RoutedEventArgs e)
    {
        if (KbStaticPanel == null || KbDynamicPanel == null) return;

        var isStatic = KbStaticTab.IsChecked == true;
        KbStaticPanel.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
        KbDynamicPanel.Visibility = isStatic ? Visibility.Collapsed : Visibility.Visible;

        // Saved, because the next launch re-applies the lighting from this choice: without it a
        // Dynamic effect would come back as the Static zone colours.
        if (!_keyboardLoading) SettingsManager.Save("KbStyle", isStatic ? "Static" : "Dynamic");

        UpdateLightingInteractivity();

        if (!IgnoreLightingChanges && _keyboardAvailable && !_syncEnabled) ApplyKeyboardLighting();
    }

    private void ScreenSync_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IgnoreLightingChanges && !_syncToggleUpdating)
            SetSyncToggle(LightingSync.SourceScreen, TogScreenSync.IsChecked == true);
    }

    private void ScreenPulse_Toggled(object sender, RoutedEventArgs e)
    {
        if (IgnoreLightingChanges) return;

        _screenPulse = TogScreenPulse.IsChecked == true;
        SettingsManager.Save("KbScreenPulse", _screenPulse ? 1 : 0);
        UpdateLightingInteractivity();
    }

    private void SoundSync_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IgnoreLightingChanges && !_syncToggleUpdating)
            SetSyncToggle(LightingSync.SourceSound, TogSoundSync.IsChecked == true);
    }

    /// <summary>The two switches are one source: turning one on turns the other off.</summary>
    private void SetSyncToggle(string source, bool on)
    {
        _syncToggleUpdating = true;
        try
        {
            _syncEnabled = on;
            if (on) _syncSource = source;

            SettingsManager.Save("KbSyncEnabled", on ? 1 : 0);
            if (on)
            {
                SettingsManager.Save("KbSyncSource", source);
                SettingsManager.Save("KbLightingSource",
                    LightingGlobalTab.IsChecked == true ? "Global" : "Area");

                // A zero brightness would make either sync look broken; the active preset's slider
                // is the effect's cap.
                if (LightingAreaTab.IsChecked == true)
                {
                    if (_keyboardBrightness == 0)
                    {
                        _keyboardBrightness = 100;
                        KeyboardBrightness.Value = 100;
                        KeyboardBrightnessText.Text = "100%";
                        SettingsManager.Save("KbBrightness", 100);
                    }
                }
                else if (_globalBrightness == 0)
                {
                    _globalBrightness = 100;
                    GlobalBrightness.Value = 100;
                    GlobalBrightnessText.Text = "100%";
                    SettingsManager.Save("KbGlobal_Brightness", 100);
                }
            }

            TogScreenSync.IsChecked = on && source == LightingSync.SourceScreen;
            TogSoundSync.IsChecked = on && source == LightingSync.SourceSound;
        }
        finally
        {
            _syncToggleUpdating = false;
        }

        UpdateLightingInteractivity();

        KeyboardLightingStatus.Text = on
            ? source == LightingSync.SourceSound
                ? "Sound sync — the tray follows the audio"
                : "Screen sync — the tray follows the screen"
            : string.Empty;
    }

    private void SoundSensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)Math.Round(e.NewValue);
        if (SoundSensitivityText != null) SoundSensitivityText.Text = value.ToString();
        if (IgnoreLightingChanges) return;

        SettingsManager.Save("KbSoundSensitivity", value);
    }

    private void ScreenSaturation_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)Math.Round(e.NewValue);
        if (ScreenSaturationText != null) ScreenSaturationText.Text = $"{value}%";
        if (IgnoreLightingChanges) return;

        SettingsManager.Save("KbSyncSaturation", value);
    }

    private void ScreenFloor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)Math.Round(e.NewValue);
        if (ScreenFloorText != null) ScreenFloorText.Text = $"{value}%";
        if (IgnoreLightingChanges) return;

        SettingsManager.Save("KbSyncFloor", value);
    }

    private void SoundMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IgnoreLightingChanges || SoundModeBox.SelectedIndex < 0) return;

        SettingsManager.Save("KbSoundMode", SoundModeBox.SelectedIndex switch
        {
            0 => LightingSync.SoundModePeak,
            2 => LightingSync.SoundModeBeat,
            3 => LightingSync.SoundModeVibe,
            _ => LightingSync.SoundModeSpectrum
        });
    }

    private void SyncTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IgnoreLightingChanges || SyncTargetBox.SelectedIndex < 0) return;

        _syncTarget = SyncTargetBox.SelectedIndex == 0
            ? LightingSync.TargetKeyboard
            : LightingSync.TargetBoth;
        SettingsManager.Save("KbSyncTarget", _syncTarget);
        UpdateLightingInteractivity();
    }

    private void GlobalEffect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IgnoreLightingChanges || GlobalEffectBox.SelectedIndex < 0)
            return;

        _globalEffect = _keyboardEffects[GlobalEffectBox.SelectedIndex].Effect;
        SettingsManager.Save("KbGlobal_Effect", _keyboardEffects[GlobalEffectBox.SelectedIndex].Name);
        UpdateLightingInteractivity();
        if (!_syncEnabled) ApplyGlobalLighting();
    }

    private void GlobalBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _globalBrightness = (int)Math.Round(e.NewValue);
        if (GlobalBrightnessText != null) GlobalBrightnessText.Text = $"{_globalBrightness}%";
        if (!IgnoreLightingChanges) HideFnBrightnessHints();
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        SettingsManager.Save("KbGlobal_Brightness", _globalBrightness);
        if (_syncEnabled) return;

        ApplyGlobalLighting();
        PushFnBrightness(_globalBrightness);
    }

    private void GlobalSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _globalSpeed = (int)Math.Round(e.NewValue);
        if (GlobalSpeedText != null) GlobalSpeedText.Text = _globalSpeed.ToString();
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        SettingsManager.Save("KbGlobal_Speed", _globalSpeed);
        if (_syncEnabled) return;

        ApplyGlobalLighting();
    }

    private void GlobalColor_Click(object sender, RoutedEventArgs e)
    {
        if (_syncEnabled && _syncSource == LightingSync.SourceScreen) return;
        if (!_keyboardAvailable || (!_syncEnabled && _globalEffect != KeyboardEffect.Static)) return;
        if (PickColour(_globalColor) is not { } chosen) return;

        _globalColor = chosen;
        SettingsManager.Save("KbGlobal_Color", chosen.ToHex());
        PaintColourButton(GlobalColorBtn, chosen);

        // While sync runs it reads the colour live; a firmware write would be overwritten.
        if (!_syncEnabled) ApplyGlobalLighting();
    }

    private void LogoEffect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IgnoreLightingChanges || LogoEffectBox.SelectedIndex < 0) return;

        _logoEffect = LogoEffects[LogoEffectBox.SelectedIndex].Effect;
        SettingsManager.Save("KbLogo_Effect", LogoEffects[LogoEffectBox.SelectedIndex].Name);
        UpdateLightingInteractivity();
        ApplyLogoLighting();
    }

    private void LogoBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _logoBrightness = (int)Math.Round(e.NewValue);
        if (LogoBrightnessText != null) LogoBrightnessText.Text = $"{_logoBrightness}%";
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        SettingsManager.Save("KbLogo_Brightness", _logoBrightness);
        ApplyLogoLighting();
    }

    private void LogoSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _logoSpeed = (int)Math.Round(e.NewValue);
        if (LogoSpeedText != null) LogoSpeedText.Text = _logoSpeed.ToString();
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        SettingsManager.Save("KbLogo_Speed", _logoSpeed);
        ApplyLogoLighting();
    }

    private void LogoColor_Click(object sender, RoutedEventArgs e)
    {
        if (!_keyboardAvailable || _logoEffect != KeyboardEffect.Static) return;
        if (PickColour(_logoColor) is not { } chosen) return;

        _logoColor = chosen;
        SettingsManager.Save("KbLogo_Color", chosen.ToHex());
        PaintColourButton(LogoColorBtn, chosen);
        ApplyLogoLighting();
    }

    private void LogoCheck_Click(object sender, RoutedEventArgs e) => RunLogoCheck();

    /// <summary>
    /// The one question the protocol cannot answer: does this model have the lid logo? Nitrous
    /// writes a conspicuous colour to the logo device and asks. The answer is remembered, and a
    /// wrong answer can be revisited from the Cover logo panel at any time.
    /// </summary>
    private void RunLogoCheck()
    {
        if (!AcerKeyboard.IsAvailable() || _logoCheckBusy || _captureMode) return;

        _logoCheckBusy = true;
        try
        {
            AcerKeyboard.SetEffect(
                KeyboardEffect.Static, 100, 0, leftwards: false,
                new KeyboardColor(0, 255, 0), AcerKeyboard.LidDevice);

            var answer = MessageBox.Show(
                "Did the 'N' logo on the lid change to green (or another colour that is not the power " +
                "mode's colour)?\n\n" +
                "The power mode colours the logo white, orange, red or violet. If it stayed on the " +
                "power mode's colour, choose No.\n\n" +
                "Choose Cancel and it will ask again the next time you open Lighting.",
                "Lid logo",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes) SettingsManager.Save("KbLogoAvailable", 1);
            else if (answer == MessageBoxResult.No) SettingsManager.Save("KbLogoAvailable", 0);
            SettingsManager.Save("KbLogoCheckRevision", LogoCheckRevision);

            UpdateLogoAvailability();

            if (LogoIsUsable) ApplyLogoLighting();
        }
        finally
        {
            _logoCheckBusy = false;
        }
    }

    /// <summary>Shows the logo controls only when the lid is known to have one.</summary>
    private void UpdateLogoAvailability()
    {
        // A WMI lighting controller has no lid device at all, so there is nothing to ask about or
        // to re-test: the panel says so and the check button is gone.
        var usable = AcerKeyboard.IsAvailable() && LogoAvailability != 0;
        LogoControlsPanel.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        LogoUnavailablePanel.Visibility = usable ? Visibility.Collapsed : Visibility.Visible;
        LogoRecheckBtn.Visibility = AcerKeyboard.IsAvailable() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KbSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _keyboardSpeed = (int)Math.Round(e.NewValue);
        if (KbSpeedText != null) KbSpeedText.Text = _keyboardSpeed.ToString();
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        SettingsManager.Save("KbSpeed", _keyboardSpeed);
        if (!_syncEnabled) ApplyKeyboardLighting();
    }

    private void KbColor_Click(object sender, RoutedEventArgs e)
    {
        if (_syncEnabled && _syncSource == LightingSync.SourceScreen) return;
        if (!_keyboardAvailable || PickColour(_zoneColors[0]) is not { } chosen) return;

        // Dynamic sends one colour across all four zones, so the chip writes all four rather than
        // leaving the keyboard showing a gradient the chosen effect cannot express.
        for (var i = 0; i < _zoneColors.Length; i++)
        {
            _zoneColors[i] = chosen;
            SettingsManager.Save($"KbZone{i + 1}", chosen.ToHex());
        }

        UpdateZoneButtons();
        if (!_syncEnabled) ApplyKeyboardLighting();
    }

    private void BlinkOnMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (IgnoreLightingChanges) return;

        _blinkOnModeChange = TogBlinkOnMode.IsChecked == true;
        SettingsManager.Save("KbBlinkOnMode", _blinkOnModeChange);
    }

    private static KeyboardColor? PickColour(KeyboardColor current)
    {
        using var dialog = CreateColourDialog(
            System.Drawing.Color.FromArgb(current.R, current.G, current.B));

        try
        {
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? new KeyboardColor(dialog.Color.R, dialog.Color.G, dialog.Color.B)
                : null;
        }
        finally
        {
            RememberCustomColours(dialog);
        }
    }

    /// <summary>
    /// A colour dialog with the user's saved custom colours restored. Windows keeps the "Add to
    /// Custom Colors" slots only for the lifetime of the dialog, so they are stored here.
    /// </summary>
    private static System.Windows.Forms.ColorDialog CreateColourDialog(System.Drawing.Color initial)
    {
        var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = initial };

        var saved = LoadCustomColours();
        if (saved.Length > 0)
            dialog.CustomColors = saved;

        return dialog;
    }

    private const string CustomColoursKey = "ColorCustoms";
    private const int CustomColourSlots = 16;

    private static int[] LoadCustomColours()
    {
        var saved = SettingsManager.Get(CustomColoursKey, string.Empty);
        var values = new List<int>();

        foreach (var part in saved.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (values.Count >= CustomColourSlots) break;

            if (int.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    /// <summary>
    /// Stores the dialog's palette after it closes, whatever the result: a colour added and then
    /// cancelled is still a colour the user chose and would expect to find next time.
    /// </summary>
    private static void RememberCustomColours(System.Windows.Forms.ColorDialog dialog)
    {
        var colours = dialog.CustomColors ?? Array.Empty<int>();
        SettingsManager.Save(CustomColoursKey, string.Join(",",
            colours.Take(CustomColourSlots).Select(value => value.ToString("X8"))));
    }

    private void Zone_Click(object sender, RoutedEventArgs e)
    {
        if (_syncEnabled && _syncSource == LightingSync.SourceScreen) return;
        if (!_keyboardAvailable || !_keyboardZoned ||
            sender is not System.Windows.Controls.Button { Tag: string tag } ||
            !int.TryParse(tag, out var zone))
        {
            return;
        }

        // Tag 0 is the ALL tile: one colour for every zone, which is the common case.
        var allZones = zone == 0;
        var index = zone - 1;
        if (!allZones && (index < 0 || index >= _zoneColors.Length)) return;

        var current = _zoneColors[allZones ? 0 : index];
        if (PickColour(current) is not { } chosen) return;

        if (allZones)
        {
            for (var i = 0; i < _zoneColors.Length; i++)
            {
                _zoneColors[i] = chosen;
                SettingsManager.Save($"KbZone{i + 1}", chosen.ToHex());
            }
        }
        else
        {
            _zoneColors[index] = chosen;
            SettingsManager.Save($"KbZone{zone}", chosen.ToHex());
        }

        UpdateZoneButtons();

        // The zone chips only exist in the keyboard's Static panel, so the write is the static one.
        if (!_syncEnabled) ApplyKeyboardLighting();
    }

    private void KeyboardEffect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IgnoreLightingChanges || KeyboardEffectBox.SelectedIndex < 0) return;

        _keyboardEffect = _dynamicEffects[KeyboardEffectBox.SelectedIndex].Effect;
        SettingsManager.Save("KbEffect", _dynamicEffects[KeyboardEffectBox.SelectedIndex].Name);
        UpdateLightingInteractivity();
        if (!_syncEnabled) ApplyKeyboardLighting();
    }

    private void KeyboardBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _keyboardBrightness = (int)Math.Round(e.NewValue);
        if (KeyboardBrightnessText != null) KeyboardBrightnessText.Text = $"{_keyboardBrightness}%";
        if (!IgnoreLightingChanges) HideFnBrightnessHints();
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        SettingsManager.Save("KbBrightness", _keyboardBrightness);
        if (!_syncEnabled) ApplyKeyboardLighting();

        PushFnBrightness(_keyboardBrightness);
    }

    private void KeyboardBrightness_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_keyboardLoading || KeyboardBrightnessBox.SelectedIndex < 0) return;

        var levels = AcerWmiManager.KeyboardBrightnessLevels;
        var index = Math.Clamp(KeyboardBrightnessBox.SelectedIndex, 0, levels.Count - 1);
        var level = levels[index];

        _keyboardBrightnessLevel = level;
        SettingsManager.Save("KbBrightLevel", level);

        KeyboardBrightnessStatus.Text = AcerWmiManager.SetKeyboardBrightness(level)
            ? string.Empty
            : "Brightness refused";
    }

    /// <summary>
    /// Sound sync on a model whose only backlight control is the five-step brightness path. The
    /// tray worker owns the writes; this is the switch and its options.
    /// </summary>
    private void BrightnessSync_Toggled(object sender, RoutedEventArgs e)
    {
        if (_keyboardLoading || _brightnessSyncUpdating || !_keyboardBrightnessAvailable) return;

        var on = TogBrightnessSync.IsChecked == true;

        if (on && _keyboardBrightnessLevel == 0)
        {
            // A zero backlight would make the pulse invisible; 100 is also the level restored later.
            _keyboardBrightnessLevel = 100;
            SettingsManager.Save("KbBrightLevel", 100);
            KeyboardBrightnessBox.SelectedIndex = AcerWmiManager.KeyboardBrightnessLevels.Count - 1;
        }

        SettingsManager.Save("KbSyncEnabled", on ? 1 : 0);
        if (on) SettingsManager.Save("KbSyncSource", LightingSync.SourceSound);

        UpdateBrightnessSyncUi();
    }

    private void BrightnessSyncMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_keyboardLoading || _brightnessSyncUpdating) return;

        SettingsManager.Save("KbBrightSyncMode",
            BrightnessSyncModeBox.SelectedIndex switch
            {
                2 => LightingSync.SoundModePeak,
                1 => LightingSync.SoundModeBeat,
                _ => LightingSync.SoundModeVibe
            });
    }

    private void BrightnessSyncSensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)Math.Round(e.NewValue);
        if (BrightnessSyncSensitivityText != null) BrightnessSyncSensitivityText.Text = value.ToString();
        if (_keyboardLoading || _brightnessSyncUpdating) return;

        SettingsManager.Save("KbSoundSensitivity", value);
    }

    /// <summary>
    /// Whether the five-step backlight is driven by ACPI rather than the EC HID. Cached: the probe
    /// opens the HID device and asks the firmware, which is not something to repeat per repaint.
    /// </summary>
    private bool? _brightnessBacklightIsAcpi;

    private bool BrightnessBacklightIsAcpi() => _brightnessBacklightIsAcpi ??=
        AcerWmiManager.GetKeyboardBacklightPath() == AcerWmiManager.KeyboardBacklightPath.Wmi;

    private void UpdateBrightnessSyncUi()
    {
        var on = _keyboardBrightnessAvailable && TogBrightnessSync.IsChecked == true;

        BrightnessSyncOptions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        KeyboardBrightnessBox.IsEnabled = _keyboardBrightnessAvailable && !on;
        BrightnessSyncStatus.Text = on
            ? "The tray is pulsing the backlight with the system audio."
            : string.Empty;

        // Ace Knight, ANV15-51: ten of ten LatencyMon runs flagged audio latency with this effect
        // on, one or two of ten without it. The firmware traffic is the trigger there, so the
        // toggle says so instead of being a trap.
        BrightnessSyncLatencyHint.Visibility = BrightnessBacklightIsAcpi()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void KeyboardTimeout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_keyboardLoading || KeyboardTimeoutBox.SelectedIndex < 0) return;

        var seconds = KeyboardTimeouts[KeyboardTimeoutBox.SelectedIndex].Seconds;
        SettingsManager.Save("KbBacklightTimeout", seconds);

        if (!_keyboardTimeoutAvailable)
        {
            if (!_keyboardAutoOffViaWmi) return;

            // This model keeps the setting as a firmware timeout reachable over APGeAction, and it
            // only offers 30 seconds or never. SetKeyboardAutoOff reads the value back, so a write
            // the firmware ignores is reported rather than shown as applied.
            KeyboardTimeoutStatus.Text = AcerWmiManager.SetKeyboardAutoOff(
                seconds != 0, _keyboardBrightnessAvailable ? _keyboardBrightnessLevel : null)
                ? string.Empty
                : "Auto-off refused";

            return;
        }

        try
        {
            if (!AcerWmiManager.SetKeyboardBacklightTimeout(
                    seconds, _keyboardBrightnessAvailable ? _keyboardBrightnessLevel : null))
            {
                KeyboardTimeoutStatus.Text = "Auto-off refused";
                return;
            }

            // The controller answers "done" even where it has nowhere to store an auto-off, so
            // the value it reports afterwards is the only proof the change was applied.
            var applied = await ReadKeyboardTimeoutAfterWriteAsync(seconds);

            if (applied is { } state && state.TimeoutSeconds == seconds)
            {
                KeyboardTimeoutStatus.Text = string.Empty;
                return;
            }

            if (applied == null)
            {
                Logger.Warn("Keyboard: could not read the auto-off back.");
                return;
            }

            Logger.Warn($"Keyboard: wrote {seconds}s, controller reports " +
                        $"{applied.Value.TimeoutSeconds}s.");

            SettingsManager.Save("KbTimeoutRefused", 1);
            _keyboardTimeoutAvailable = false;
            KeyboardTimeoutBox.IsEnabled = false;
            KeyboardTimeoutBox.ToolTip = "This keyboard controller does not store an auto-off.";
            KeyboardTimeoutStatus.Text = "Auto-off not supported";
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: could not set the backlight auto-off.", ex);
            KeyboardTimeoutStatus.Text = "Auto-off change failed";
        }
    }

    /// <summary>
    /// Reads the auto-off back until it agrees with what was written, or the attempts run out.
    /// </summary>
    private static async Task<(int Brightness, int TimeoutSeconds)?> ReadKeyboardTimeoutAfterWriteAsync(
        int wantedSeconds)
    {
        (int Brightness, int TimeoutSeconds)? last = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var state = AcerWmiManager.GetKeyboardBacklightTimeout();
            if (state is { } value && value.TimeoutSeconds == wantedSeconds) return state;
            if (state != null) last = state;
            if (attempt < 2) await Task.Delay(80);
        }

        return last;
    }

    private bool _usbChargingLoading = true;
    private bool _usbChargingAvailable;
    private int _usbChargingFloor = 30;

    /// <summary>
    /// Power-off USB charging is an APGeAction setting, not a misc register, and some BIOSes only
    /// implement part of that class. When it does not answer the controls stay disabled.
    /// </summary>
    private void InitializeUsbCharging()
    {
        foreach (var floor in AcerWmiProtocol.UsbChargingFloors)
            UsbChargingFloorBox.Items.Add($"{floor}%");

        var state = AcerWmiManager.GetUsbCharging();
        _usbChargingAvailable = state.HasValue;

        TogUsbCharging.IsEnabled = _usbChargingAvailable;
        UsbChargingFloorBox.IsEnabled = _usbChargingAvailable;

        if (state is { } current)
        {
            _usbChargingFloor = current.Floor ?? 30;
            TogUsbCharging.IsChecked = current.On;
            UsbChargingFloorBox.SelectedIndex =
                Array.IndexOf(AcerWmiProtocol.UsbChargingFloors, _usbChargingFloor);
        }
        else
        {
            UsbChargingFloorBox.SelectedIndex =
                Array.IndexOf(AcerWmiProtocol.UsbChargingFloors, 30);
            TogUsbCharging.ToolTip =
                "Nitrous cannot reach this setting on this model; the OEM software may still offer it.";
        }

        _usbChargingLoading = false;
    }

    private void UsbCharging_Toggled(object sender, RoutedEventArgs e)
    {
        if (_usbChargingLoading || !_usbChargingAvailable) return;
        ApplyUsbCharging();
    }

    private void UsbChargingFloor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UsbChargingFloorBox.SelectedIndex >= 0)
            _usbChargingFloor = AcerWmiProtocol.UsbChargingFloors[UsbChargingFloorBox.SelectedIndex];

        if (_usbChargingLoading || !_usbChargingAvailable) return;
        ApplyUsbCharging();
    }

    private void ApplyUsbCharging()
    {
        try
        {
            var on = TogUsbCharging.IsChecked == true;
            if (!AcerWmiManager.SetUsbCharging(on, _usbChargingFloor))
                Logger.Warn($"USB charging: the firmware refused on={on} floor={_usbChargingFloor}.");
        }
        catch (Exception ex)
        {
            Logger.Warn("USB charging: could not apply the setting.", ex);
        }
    }

    private bool _lcdOverdriveLoading = true;
    private bool _lcdOverdriveBusy;
    private bool _lcdOverdriveAvailable;

    /// <summary>
    /// LCD overdrive is a panel property, not a model one: the firmware answers 0xFF for panels
    /// that do not have it, so the control is hidden rather than disabled. A control that can
    /// never do anything is clutter, and a disabled one still invites a click.
    ///
    /// A panel without overdrive can also answer 0, which is exactly what a panel that has it and
    /// has it switched off reports. The two cannot be told apart by reading, so the question is
    /// settled the first time a change is asked for: the firmware is written, then read back, and
    /// a panel that ignores the write is remembered as not supporting it.
    /// </summary>
    private void InitializeLcdOverdrive()
    {
        var state = AcerWmiManager.GetLcdOverdrive();

        // Reporting overdrive on is proof the panel has it, so a stale refusal is cleared.
        if (state is { Supported: true, On: true })
            SettingsManager.Save("LcdOverdriveRefused", 0);

        if (state is { } reported)
            Logger.Info($"LCD overdrive: firmware reports supported={reported.Supported}, " +
                        $"on={reported.On}.");
        else
            Logger.Warn("LCD overdrive: the firmware did not answer.");

        var refused = SettingsManager.Get("LcdOverdriveRefused", 0) == 1;
        _lcdOverdriveAvailable = state is { Supported: true } && !refused;

        if (state is { Supported: true, On: var on })
            TogLcdOverdrive.IsChecked = on;

        // The control exists only when it can actually work. A panel with no overdrive, a firmware
        // that does not answer, and a write the panel ignored all leave the row showing the
        // refresh list alone, rather than a control that cannot do anything.
        SetLcdOverdriveVisible(_lcdOverdriveAvailable);

        _lcdOverdriveLoading = false;
    }

    private void SetLcdOverdriveVisible(bool visible)
    {
        // The row is collapsed as a whole, so the card closes up rather than leaving a gap where
        // the control would have been. Nothing else has to move: each row owns its own line.
        LcdOverdriveRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The card exists only where the firmware advertises the feature (SMBIOS 0x0D or the misc 8
    /// switch). The probe touches WMI, so it runs off the UI thread.
    /// </summary>
    private void InitializeBootLogo()
    {
        _ = Task.Run(() =>
        {
            if (!BootLogo.IsSupported) return;

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    BootLogoCard.Visibility = Visibility.Visible;
                    RefreshBootLogo();
                }));
            }
            catch (InvalidOperationException) { }
        });
    }

    /// <summary>
    /// CoolBoost, the fan switch Acer's software offers on some models. It appears only where
    /// APGeAction function 7 says that function is CoolBoost, and hides in Max, which already runs
    /// the fans at full speed.
    /// </summary>
    private void InitializeCoolBoost()
    {
        _coolBoostAvailable = AcerWmiManager.CoolBoostSupported;
        if (!_coolBoostAvailable) return;

        RefreshCoolBoost();
        UpdateCoolBoostVisibility();
    }

    private void RefreshCoolBoost()
    {
        if (!_coolBoostAvailable) return;

        var state = AcerWmiManager.GetCoolBoost();
        if (state is null)
        {
            _coolBoostAvailable = false;
            UpdateCoolBoostVisibility();
            return;
        }

        _coolBoostLoading = true;
        try
        {
            TogCoolBoost.IsChecked = state;
        }
        finally
        {
            _coolBoostLoading = false;
        }
    }

    private void UpdateCoolBoostVisibility()
    {
        CoolBoostCard.Visibility = _coolBoostAvailable && BtnFanMax.IsChecked != true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CoolBoost_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_coolBoostAvailable || _coolBoostLoading) return;

        var on = TogCoolBoost.IsChecked == true;
        if (AcerWmiManager.SetCoolBoost(on)) return;

        // Show what the firmware actually kept rather than a state it refused.
        Logger.Warn($"CoolBoost: the firmware refused to turn it {(on ? "on" : "off")}.");
        RefreshCoolBoost();
    }

    private void RefreshBootLogo()
    {
        var current = BootLogo.Read();
        BootLogoPreview.Source = current?.Data is { } data ? LoadLogoImage(data) : null;
        BootLogoRemoveButton.IsEnabled = current is not null;
        BootLogoStatus.Text = current is null
            ? "Acer's splash is showing. A change appears at the next power-on."
            : $"Custom picture in use ({current.Value.Name}); shown at the next power-on.";
    }

    private static System.Windows.Media.Imaging.BitmapImage? LoadLogoImage(byte[] data)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.StreamSource = new System.IO.MemoryStream(data);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException)
        {
            return null;
        }
    }

    private void BootLogoChoose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a boot logo",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        BootLogoChooseButton.IsEnabled = false;
        BootLogoStatus.Text = "Writing the picture to the EFI partition...";

        _ = Task.Run(() =>
        {
            var written = BootLogo.Apply(path, out var error);
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    BootLogoChooseButton.IsEnabled = true;
                    if (!written)
                    {
                        BootLogoStatus.Text = error;
                        return;
                    }

                    RefreshBootLogo();
                    BootLogoStatus.Text = "Written. Restart to see it at power-on.";
                }));
            }
            catch (InvalidOperationException) { }
        });
    }

    private void BootLogoRemove_Click(object sender, RoutedEventArgs e)
    {
        BootLogoRemoveButton.IsEnabled = false;
        BootLogoStatus.Text = "Removing the picture...";

        _ = Task.Run(() =>
        {
            var restored = BootLogo.Restore(out var error);
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    BootLogoRemoveButton.IsEnabled = true;
                    if (!restored)
                    {
                        BootLogoStatus.Text = error;
                        return;
                    }

                    RefreshBootLogo();
                    BootLogoStatus.Text = "Acer's logo is back. Restart to see it.";
                }));
            }
            catch (InvalidOperationException) { }
        });
    }

    private void SetLcdOverdriveToggle(bool on)
    {
        _lcdOverdriveLoading = true;
        TogLcdOverdrive.IsChecked = on;
        _lcdOverdriveLoading = false;
    }

    /// <summary>
    /// Reads the setting back until it agrees with what was written, or the attempts run out. The
    /// firmware can lag a write, and one early read would look like a refusal.
    /// </summary>
    private static async Task<(bool Supported, bool On)?> ReadLcdOverdriveAfterWriteAsync(bool wanted)
    {
        (bool Supported, bool On)? last = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var state = AcerWmiManager.GetLcdOverdrive();
            if (state is { Supported: true, On: var on } && on == wanted) return state;
            if (state != null) last = state;
            if (attempt < 2) await Task.Delay(80);
        }

        return last;
    }

    private sealed record RefreshChoice(string Label, int? Hz);

    private bool _refreshControlsLoading;
    private bool _keepAwakeLoading;

    /// <summary>
    /// Fills the dropdown with Auto and the rates the display advertises, and marks the saved
    /// choice. The current rate is shown separately, so the dropdown says what was chosen and the
    /// label says what is actually running.
    /// </summary>
    private void SyncRefreshControls()
    {
        _refreshControlsLoading = true;

        var choices = new List<RefreshChoice> { new("Auto", null) };
        foreach (var hz in DisplayManager.GetSupportedRefreshRates())
            choices.Add(new RefreshChoice($"{hz} Hz", hz));

        RefreshModeBox.ItemsSource = choices;
        RefreshModeBox.DisplayMemberPath = nameof(RefreshChoice.Label);

        var mode = DisplayManager.GetMode();
        var chosen = DisplayManager.GetChosenRefreshRate();

        var selected = choices[0];
        if (mode != RefreshMode.Auto)
        {
            foreach (var choice in choices)
            {
                if (choice.Hz == chosen)
                {
                    selected = choice;
                    break;
                }
            }
        }

        RefreshModeBox.SelectedItem = selected;

        var actual = DisplayManager.GetCurrentRefreshRate();
        RefreshDisplaySummary();
        _lastSyncedRefreshHz = actual;

        _refreshControlsLoading = false;
    }

    private void RefreshMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshControlsLoading || RefreshModeBox.SelectedItem is not RefreshChoice choice) return;

        if (choice.Hz is { } hz)
            DisplayManager.SetFixedRate(hz);
        else
            DisplayManager.SetAuto();

        RefreshDisplaySummary();
    }

    /// <summary>
    /// The panel's own label: adapter, the rate it is running at, and the signal depth Windows reports -
    /// "AMD Radeon 860M @ 144 Hz · 10-bit". Parts the machine does not answer are left out rather than
    /// guessed.
    /// </summary>
    private void RefreshDisplaySummary()
    {
        var adapter = DisplayManager.GetAdapterName();
        var hz = DisplayManager.GetCurrentRefreshRate();
        var bits = DisplayManager.GetBitsPerColorChannel();

        var text = adapter ?? string.Empty;
        if (hz > 0) text = text.Length == 0 ? $"{hz} Hz" : $"{text} @ {hz} Hz";
        if (bits > 0) text = text.Length == 0 ? $"{bits}-bit" : $"{text} · {bits}-bit";

        DisplayCurrentText.Text = text;
        DisplayCurrentText.ToolTip = text.Length > 0 ? text : null;
    }

    private async void LcdOverdrive_Toggled(object sender, RoutedEventArgs e)
    {
        if (_lcdOverdriveLoading || _lcdOverdriveBusy || !_lcdOverdriveAvailable) return;

        var wanted = TogLcdOverdrive.IsChecked == true;
        _lcdOverdriveBusy = true;

        try
        {
            if (!AcerWmiManager.SetLcdOverdrive(wanted))
            {
                Logger.Warn("LCD overdrive: the firmware refused the change.");
                SetLcdOverdriveToggle(!wanted);
                return;
            }

            var applied = await ReadLcdOverdriveAfterWriteAsync(wanted);

            if (applied is { Supported: true, On: var on } && on == wanted)
                return;

            if (applied == null)
            {
                // The read failed, which says nothing about the panel. Leave the toggle usable.
                Logger.Warn("LCD overdrive: could not read the setting back.");
                return;
            }

            // Either the firmware now calls the panel unsupported, or it accepted the write and
            // did not apply it. Both mean this panel cannot do overdrive.
            Logger.Warn($"LCD overdrive: wrote {wanted}, firmware reports " +
                        $"supported={applied.Value.Supported}, on={applied.Value.On}.");

            SettingsManager.Save("LcdOverdriveRefused", 1);
            _lcdOverdriveAvailable = false;
            SetLcdOverdriveToggle(false);
            SetLcdOverdriveVisible(false);
        }
        catch (Exception ex)
        {
            Logger.Warn("LCD overdrive: could not apply the change.", ex);
            SetLcdOverdriveToggle(!wanted);
        }
        finally
        {
            _lcdOverdriveBusy = false;
        }
    }

    /// <summary>
    /// Area → Keyboard. The Static panel writes one colour per zone; the Dynamic panel writes one
    /// effect across all four, so only the first zone's colour is carried.
    /// </summary>
    private void ApplyKeyboardLighting()
    {
        if (!_keyboardAvailable) return;

        try
        {
            var applied = KbDynamicTab.IsChecked == true
                ? KeyboardLighting.SetEffect(
                    _keyboardEffect, _keyboardBrightness, _keyboardSpeed, leftwards: false, _zoneColors[0])
                : KeyboardLighting.SetZoneColors(_zoneColors, _keyboardBrightness);

            KeyboardLightingStatus.Text = applied ? string.Empty : "The keyboard refused the change";
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: lighting change failed.", ex);
            KeyboardLightingStatus.Text = "Lighting change failed";
        }

        SettingsManager.Save("KbLightingSource", "Area");
        ScheduleAutoOffRearm();
    }

    /// <summary>
    /// Area → Cover logo. Static honours the colour; Breathing and Neon cycle their own palette and
    /// ignore it, which is why the colour chip reads "Auto" for those two.
    /// </summary>
    private void ApplyLogoLighting(bool reportFailure = true)
    {
        if (!_keyboardAvailable || !LogoIsUsable) return;

        try
        {
            var applied = AcerKeyboard.SetEffect(
                _logoEffect, _logoBrightness, Math.Clamp(_logoSpeed, 1, LogoMaxSpeed),
                leftwards: false, _logoColor, AcerKeyboard.LidDevice);

            // A model without a lid simply refuses this; at launch that is not worth a message.
            if (reportFailure)
                KeyboardLightingStatus.Text = applied ? string.Empty : "The cover logo refused the change";
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: cover-logo change failed.", ex);
            if (reportFailure) KeyboardLightingStatus.Text = "Cover-logo change failed";
        }

        SettingsManager.Save("KbLightingSource", "Area");
    }

    /// <summary>
    /// Global. There is no "all devices" byte on this controller, so the same effect is written to
    /// both device ids. A mode the logo does not implement (Wave, Ripple, Zoom, Snake, Disco) is
    /// left out of the logo write entirely, which is what the OEM software does too.
    /// </summary>
    private void ApplyGlobalLighting()
    {
        if (!_keyboardAvailable) return;

        HideFnBrightnessHints();

        try
        {
            // The Global control is five steps, as the OEM software's is. Each step is a matched
            // pair: keyboard 1/3/5/7/9 with logo 1/2/3/4/5.
            var step = Math.Clamp(_globalSpeed, 1, LogoMaxSpeed);
            var keyboardSpeed = KeyboardSpeedFor(step);

            var keyboardApplied = _globalEffect == KeyboardEffect.Static
                ? KeyboardLighting.SetZoneColors(
                    Enumerable.Repeat(_globalColor, AcerKeyboard.ZoneCount).ToArray(), _globalBrightness)
                : KeyboardLighting.SetEffect(
                    _globalEffect, _globalBrightness, keyboardSpeed, leftwards: false, _globalColor);

            var logoApplied = true;
            var logoState = "not supported";
            if (LogoIsUsable && LogoEffectFor(_globalEffect) is { } logoEffect)
            {
                logoApplied = AcerKeyboard.SetEffect(
                    logoEffect, _globalBrightness, step,
                    leftwards: false, _globalColor, AcerKeyboard.LidDevice);
                logoState = logoApplied ? "applied" : "refused";
            }

            KeyboardLightingStatus.Text = keyboardApplied && logoApplied
                ? string.Empty
                : "A device refused the change";

            Logger.Info($"Keyboard: global {_globalEffect} at {_globalBrightness}%, step {step} " +
                        $"(keyboard {keyboardSpeed}, logo {step}) - " +
                        $"keyboard {(keyboardApplied ? "applied" : "refused")}, cover logo {logoState}.");
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: global lighting change failed.", ex);
            KeyboardLightingStatus.Text = "Lighting change failed";
        }

        SettingsManager.Save("KbLightingSource", "Global");
        ScheduleAutoOffRearm();
    }

    private static KeyboardEffect? LogoEffectFor(KeyboardEffect effect) =>
        effect is KeyboardEffect.Static or KeyboardEffect.Breathing or KeyboardEffect.Neon
            ? effect
            : null;

    /// <summary>
    /// Maps a Global speed step (1-5) onto the keyboard's nine speeds. The OEM software's Global
    /// slider has five positions and sends matched pairs: step 1 -> keyboard 1 / logo 1, step 5 ->
    /// keyboard 9 / logo 5 (captured). Sending the same number to both does not work, because the
    /// keyboard has nine steps and the logo five.
    /// </summary>
    private static int KeyboardSpeedFor(int step) =>
        AcerKeyboard.MinSpeed + (step - 1) * 2;

    /// <summary>
    /// Maps a keyboard speed (1-9) onto the cover logo's five steps, for the paths that only have a
    /// keyboard speed to start from.
    /// </summary>
    private static int LogoSpeedFor(int keyboardSpeed) =>
        Math.Clamp(1 + (keyboardSpeed - AcerKeyboard.MinSpeed) / 2, 1, LogoMaxSpeed);

    /// <summary>
    /// The lighting write switches the backlight back on, but the auto-off is a separate countdown
    /// on the embedded controller, armed only by a write that carries it. Without re-arming,
    /// opening the dashboard leaves the backlight on until the timeout is picked again.
    /// </summary>
    private void ScheduleAutoOffRearm()
    {
        if (!_keyboardTimeoutAvailable) return;

        _autoOffRearmTimer.Stop();
        _autoOffRearmTimer.Start();
    }

    private void RearmKeyboardAutoOff()
    {
        _autoOffRearmTimer.Stop();

        var seconds = SettingsManager.Get("KbBacklightTimeout", 0);
        if (seconds <= 0) return;

        try
        {
            // Brightness comes from the UI rather than a read: the write carries both fields, and
            // the level Nitrous just applied is the one that should be preserved.
            if (AcerWmiManager.SetKeyboardBacklightTimeout(seconds, _keyboardBrightness))
            {
                Logger.Info($"Keyboard: auto-off re-armed for {seconds}s at {_keyboardBrightness}% " +
                            "after a lighting change.");
            }
            else
            {
                Logger.Warn("Keyboard: could not re-arm the auto-off after a lighting change.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: could not re-arm the auto-off.", ex);
        }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_isDiagnosticsExporting) return;

        _isDiagnosticsExporting = true;
        ExportDiagnosticsButton.IsEnabled = false;
        ExportDiagnosticsStatus.Text = "Collecting diagnostics...";

        try
        {
            var progress = new Progress<string>(message => ExportDiagnosticsStatus.Text = message);
            var result = DataContext is DashboardViewModel vm
                ? await DiagnosticsCollector.ExportAsync(vm.GetGpuDiagnosticsAsync, progress)
                : await DiagnosticsCollector.ExportAsync(progress: progress);

            ExportDiagnosticsStatus.Text = result.ErrorCount == 0
                ? "Diagnostics exported."
                : $"Diagnostics exported with {result.ErrorCount} section error(s); see errors.txt.";

            DiagnosticsCollector.OpenInExplorer(result.ZipPath);
        }
        catch (Exception ex)
        {
            Logger.Warn("Diagnostics: export failed.", ex);
            ExportDiagnosticsStatus.Text = "Export failed. See the log for details.";
            MessageBox.Show(
                $"Could not export diagnostics:\n\n{ex.Message}",
                "Nitrous Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isDiagnosticsExporting = false;
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private async void EcProbe_Click(object sender, RoutedEventArgs e)
    {
        if (_isEcProbing) return;

        _isEcProbing = true;
        EcProbeButton.IsEnabled = false;
        ExportDiagnosticsStatus.Text = "Reading Acer firmware interface...";

        try
        {
            var result = await EcProbe.RunAsync();

            ExportDiagnosticsStatus.Text = result.ErrorCount == 0
                ? "EC probe written."
                : $"EC probe written with {result.ErrorCount} section error(s); see the report.";

            DiagnosticsCollector.OpenInExplorer(result.SummaryPath);
        }
        catch (Exception ex)
        {
            Logger.Warn("EC probe: failed.", ex);
            ExportDiagnosticsStatus.Text = "EC probe failed. See the log for details.";
            MessageBox.Show(
                $"Could not run the EC probe:\n\n{ex.Message}",
                "Nitrous Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isEcProbing = false;
            EcProbeButton.IsEnabled = true;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Logger.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Logger.LogDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Diagnostics: could not open the log folder.", ex);
            MessageBox.Show(
                $"Could not open the log folder:\n\n{ex.Message}",
                "Nitrous Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Hotkey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true; // Prevent standard typing

        if (sender is not TextBox txt || txt.Tag == null) return;

        // Ignore modifier keys pressed on their own.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.System)
            return;

        var settingKey = (string)txt.Tag;

        // ESC clears the hotkey.
        if (e.Key == Key.Escape)
        {
            txt.Text = "None";
            SettingsManager.Save(settingKey, "");
            HotkeyChangeSignal.Notify();
            Keyboard.ClearFocus();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        uint modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyBinding.ModAlt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyBinding.ModControl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyBinding.ModShift;

        // Require at least one modifier to prevent binding simple letters like "W".
        if (modifiers == 0) return;

        var savedValue = HotkeyBinding.Create(modifiers, virtualKey);
        SettingsManager.Save(settingKey, savedValue);
        HotkeyChangeSignal.Notify();

        txt.Text = HotkeyBinding.Format(savedValue);
        Keyboard.ClearFocus();
    }

    /// <summary>
    /// Normalizes a typed offset when the box loses focus. A valid entry is re-rendered in the
    /// signed form ("+50"); an entry that never reached the view model (letters, empty text) is
    /// replaced with the last valid value. Typing a negative number clamps to 0 in the setter.
    /// </summary>
    private void GpuOffsetInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;

        // The text boxes commit on LostFocus so typing does not fight with the "+" formatting.
        // Push the typed value through the setter first, then render the canonical value back.
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();
        binding?.UpdateTarget();

        if (DataContext is DashboardViewModel viewModel)
            box.Text = box == GpuMemoryInput ? viewModel.GpuMemoryOffsetText : viewModel.GpuCoreOffsetText;
    }
}
