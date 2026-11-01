using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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

using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
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

    public NitrousDashboard()
    {
        InitializeComponent();

        DataContext = new DashboardViewModel();

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

        InitializeKeyboardLighting();
        InitializeStickyKeys();
        InitializeUsbCharging();
        InitializeLcdOverdrive();

        System.Threading.Tasks.Task.Run(() =>
        {
            var modelName = SystemInfoManager.GetSystemModel();
            Dispatcher.InvokeAsync(() => SystemModelText.Text = $"{modelName}");
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

        System.Threading.Tasks.Task.Delay(5500).ContinueWith(_ =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.InvokeAsync(RefreshDashboardState);
        });
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

        var activeMode = (PowerProfile)SettingsManager.Get("LastPowerMode", (int)PowerProfile.Performance);
        var activeFan = Enum.TryParse(SettingsManager.Get("LastFanMode", "Auto"), out FanProfile fan)
            ? fan
            : FanProfile.Auto;
        var activeRefresh = DisplayManager.GetMode();

        if (activeMode == _lastSyncedPowerMode &&
            activeFan == _lastSyncedFanMode &&
            activeRefresh == _lastSyncedRefreshMode &&
            DisplayManager.GetCurrentRefreshRate() == _lastSyncedRefreshHz)
            return;

        RefreshDashboardState();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private int _activeNavIndex;

    private void SetActiveNav(int index)
    {
        _activeNavIndex = index;

        var pages = new UIElement[] { DashPage, GpuPage, KeyboardPage, SettingsPage };
        var icons = new Shape[] { NavDashIcon, NavGpuIcon, NavKeyboardIcon, NavSetIcon };
        var labels = new TextBlock[] { NavDashText, NavGpuText, NavKeyboardText, NavSetText };

        var activeBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B388FF"));
        var inactiveBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888890"));

        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            icons[i].Fill = i == index ? activeBrush : inactiveBrush;
            labels[i].Foreground = i == index ? activeBrush : inactiveBrush;
        }

        UpdateGpuTelemetryScope();

        // Entering the GPU page should show live values immediately rather than the previous
        // page's last snapshot.
        if (index == 1 && DataContext is DashboardViewModel vm)
            vm.RefreshGpuOffsets();
    }

    /// <summary>
    /// NVML polling is only allowed while the GPU page is the visible page. Reading NVML wakes
    /// the dGPU, and no other page displays its values.
    /// </summary>
    private void UpdateGpuTelemetryScope()
    {
        if (DataContext is not DashboardViewModel vm) return;

        var visible = Visibility == Visibility.Visible && WindowState != WindowState.Minimized;
        vm.SetGpuTelemetryActive(visible && _activeNavIndex == 1);
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

        if (Visibility == Visibility.Visible) RefreshDashboardState();
    }

    public void RefreshDashboardState()
    {
        var isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                       System.Windows.Forms.PowerLineStatus.Online;
        var powerColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isOnline ? "#FF453A" : "#34C759"));
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

            // The tray applies the new profile's offsets on a hotkey or AC/DC change.
            // Reflect the live values so the sliders match what is actually applied.
            if (vm.ManageGpuClocks)
                vm.RefreshGpuOffsets();
        }

        if (TxtHkCycle != null)
        {
            TxtHkCycle.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_CyclePower", ""));
            TxtHkDash.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_Dashboard", ""));
        }

        _lastSyncedPowerMode = activeMode;
        _lastSyncedFanMode = activeFan;
        _lastSyncedRefreshMode = activeRefresh;
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

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (Topmost || _isDialogOpen) return;

        WindowState = WindowState.Minimized;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _profileSyncTimer.Stop();
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
    private KeyboardEffect _keyboardEffect = KeyboardEffect.Static;
    private bool _keyboardAvailable;
    private bool _keyboardZoned = true;
    private bool _keyboardLoading = true;

    // The lighting write switches the backlight back on, but the auto-off is a separate countdown
    // on the EC that only a write carrying it arms. Debounced so dragging the brightness slider
    // costs one EC exchange at the end rather than one per tick.
    private readonly DispatcherTimer _autoOffRearmTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };

    private static readonly (string Name, KeyboardEffect Effect)[] KeyboardEffects =
    {
        ("Static", KeyboardEffect.Static),
        ("Breathing", KeyboardEffect.Breathing),
        ("Neon", KeyboardEffect.Neon),
        ("Wave", KeyboardEffect.Wave),
        ("Shifting", KeyboardEffect.Shifting),
        ("Zoom", KeyboardEffect.Zoom),
        ("Meteor", KeyboardEffect.Meteor),
        ("Twinkling", KeyboardEffect.Twinkling)
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

    /// <summary>
    /// Loads the saved lighting state and shows it. The keyboard's lighting interface is a
    /// separate HID device; models without it keep the card visible but disabled.
    /// </summary>
    private void InitializeKeyboardLighting()
    {
        _keyboardAvailable = AcerKeyboard.IsAvailable();

        // SMBIOS says whether the keyboard has one colour or RGB. A single-colour keyboard can
        // still have a backlight, so only the zones are withheld: offering four swatches on a
        // keyboard that cannot light them is what made this look broken on an unlisted model.
        _keyboardZoned = AcerCapabilitiesProvider.Current.Smbios.KeyboardColor is not 1;

        for (var i = 0; i < AcerKeyboard.ZoneCount; i++)
            _zoneColors[i] = KeyboardColor.FromHex(SettingsManager.Get($"KbZone{i + 1}", _zoneColors[i].ToHex()));

        _keyboardBrightness = Math.Clamp(SettingsManager.Get("KbBrightness", 100), 0, 100);

        foreach (var (name, _) in KeyboardEffects)
            KeyboardEffectBox.Items.Add(name);

        var saved = SettingsManager.Get("KbEffect", "Static");
        var selected = Array.FindIndex(KeyboardEffects, e => e.Name == saved);
        KeyboardEffectBox.SelectedIndex = selected >= 0 ? selected : 0;
        _keyboardEffect = KeyboardEffects[KeyboardEffectBox.SelectedIndex].Effect;

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

        UpdateZoneButtons();

        KeyboardLightingStatus.Text = !_keyboardAvailable
            ? "Colours not reachable"
            : !_keyboardZoned
                ? "Single-colour keyboard"
                : string.Empty;

        KeyboardEffectBox.IsEnabled = _keyboardAvailable;
        KeyboardBrightness.IsEnabled = _keyboardAvailable;
        UpdateZoneInteractivity();

        _keyboardLoading = false;

        if (_keyboardAvailable) ApplyKeyboardLighting();
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

    /// <summary>Zone colours only mean anything in Static; the controller ignores them otherwise.</summary>
    private void UpdateZoneInteractivity()
    {
        var staticMode = _keyboardEffect == KeyboardEffect.Static;
        var enabled = _keyboardAvailable && _keyboardZoned && staticMode;

        Zone1Btn.IsEnabled = Zone2Btn.IsEnabled = Zone3Btn.IsEnabled = Zone4Btn.IsEnabled = enabled;
        ZoneAllBtn.IsEnabled = enabled;

        ZoneHint.Text = !_keyboardZoned
            ? "This keyboard has a single colour, not four zones."
            : "Zone colours apply to Static. Pick Static to use them.";
        ZoneHint.Visibility = !_keyboardZoned || !staticMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Zone_Click(object sender, RoutedEventArgs e)
    {
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

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var chosen = new KeyboardColor(dialog.Color.R, dialog.Color.G, dialog.Color.B);

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

        // Picking a zone colour means Static; the change handler applies it.
        if (_keyboardEffect != KeyboardEffect.Static)
        {
            KeyboardEffectBox.SelectedIndex = 0;
            return;
        }

        ApplyKeyboardLighting();
    }

    private void KeyboardEffect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_keyboardLoading || KeyboardEffectBox.SelectedIndex < 0) return;

        _keyboardEffect = KeyboardEffects[KeyboardEffectBox.SelectedIndex].Effect;
        SettingsManager.Save("KbEffect", KeyboardEffects[KeyboardEffectBox.SelectedIndex].Name);
        UpdateZoneInteractivity();
        ApplyKeyboardLighting();
    }

    private void KeyboardBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _keyboardBrightness = (int)Math.Round(e.NewValue);
        if (KeyboardBrightnessText != null) KeyboardBrightnessText.Text = $"{_keyboardBrightness}%";
        if (_keyboardLoading || !_keyboardAvailable) return;

        SettingsManager.Save("KbBrightness", _keyboardBrightness);
        ApplyKeyboardLighting();
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
            KeyboardLightingStatus.Text = AcerWmiManager.SetKeyboardAutoOff(seconds != 0)
                ? string.Empty
                : "Auto-off refused";

            return;
        }

        try
        {
            if (!AcerWmiManager.SetKeyboardBacklightTimeout(seconds))
            {
                KeyboardLightingStatus.Text = "Auto-off refused";
                return;
            }

            // The controller answers "done" even where it has nowhere to store an auto-off, so
            // the value it reports afterwards is the only proof the change was applied.
            var applied = await ReadKeyboardTimeoutAfterWriteAsync(seconds);

            if (applied is { } state && state.TimeoutSeconds == seconds)
            {
                KeyboardLightingStatus.Text = string.Empty;
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
            KeyboardLightingStatus.Text = "Auto-off not supported";
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: could not set the backlight auto-off.", ex);
            KeyboardLightingStatus.Text = "Auto-off change failed";
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
        DisplayCurrentText.Text = actual > 0 ? $"{actual} Hz" : string.Empty;
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

        var actual = DisplayManager.GetCurrentRefreshRate();
        DisplayCurrentText.Text = actual > 0 ? $"{actual} Hz" : string.Empty;
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

    private void ApplyKeyboardLighting()
    {
        if (!_keyboardAvailable) return;

        try
        {
            var applied = _keyboardEffect == KeyboardEffect.Static
                ? AcerKeyboard.SetZoneColors(_zoneColors, _keyboardBrightness)
                : AcerKeyboard.SetEffect(_keyboardEffect, _keyboardBrightness, 4, leftwards: false, _zoneColors[0]);

            KeyboardLightingStatus.Text = applied ? string.Empty : "The keyboard refused the change";
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: lighting change failed.", ex);
            KeyboardLightingStatus.Text = "Lighting change failed";
        }

        ScheduleAutoOffRearm();
    }

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
