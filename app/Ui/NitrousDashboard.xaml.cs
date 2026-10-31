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

        InitializeKeyboardLighting();
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
    private bool _keyboardLoading = true;

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

    /// <summary>
    /// Loads the saved lighting state and shows it. The keyboard's lighting interface is a
    /// separate HID device; models without it keep the card visible but disabled.
    /// </summary>
    private void InitializeKeyboardLighting()
    {
        _keyboardAvailable = AcerKeyboard.IsAvailable();

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

        foreach (var (name, _) in KeyboardTimeouts)
            KeyboardTimeoutBox.Items.Add(name);

        var savedTimeout = SettingsManager.Get("KbBacklightTimeout", 0);
        var timeoutIndex = Array.FindIndex(KeyboardTimeouts, t => t.Seconds == savedTimeout);
        KeyboardTimeoutBox.SelectedIndex = timeoutIndex >= 0 ? timeoutIndex : 0;

        // The auto-off lives on the embedded controller, not the lighting controller, so it can be
        // missing even where the zones work.
        _keyboardTimeoutAvailable = AcerWmiManager.GetKeyboardBacklightTimeout() is not null;
        KeyboardTimeoutBox.IsEnabled = _keyboardTimeoutAvailable;
        if (!_keyboardTimeoutAvailable)
            KeyboardTimeoutBox.ToolTip = "This model does not expose the backlight controller.";

        UpdateZoneButtons();

        KeyboardLightingStatus.Text = _keyboardAvailable ? string.Empty : "No lighting interface on this model";
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
    }

    /// <summary>Zone colours only mean anything in Static; the controller ignores them otherwise.</summary>
    private void UpdateZoneInteractivity()
    {
        var staticMode = _keyboardEffect == KeyboardEffect.Static;
        var enabled = _keyboardAvailable && staticMode;

        Zone1Btn.IsEnabled = Zone2Btn.IsEnabled = Zone3Btn.IsEnabled = Zone4Btn.IsEnabled = enabled;
        ZoneHint.Visibility = staticMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Zone_Click(object sender, RoutedEventArgs e)
    {
        if (!_keyboardAvailable ||
            sender is not System.Windows.Controls.Button { Tag: string tag } ||
            !int.TryParse(tag, out var zone))
        {
            return;
        }

        var index = zone - 1;
        if (index < 0 || index >= _zoneColors.Length) return;

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(_zoneColors[index].R, _zoneColors[index].G, _zoneColors[index].B)
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _zoneColors[index] = new KeyboardColor(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        SettingsManager.Save($"KbZone{zone}", _zoneColors[index].ToHex());
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

    private void KeyboardTimeout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_keyboardLoading || KeyboardTimeoutBox.SelectedIndex < 0) return;

        var seconds = KeyboardTimeouts[KeyboardTimeoutBox.SelectedIndex].Seconds;
        SettingsManager.Save("KbBacklightTimeout", seconds);

        if (!_keyboardTimeoutAvailable) return;

        try
        {
            KeyboardLightingStatus.Text = AcerWmiManager.SetKeyboardBacklightTimeout(seconds)
                ? string.Empty
                : "The controller refused the auto-off change";
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: could not set the backlight auto-off.", ex);
            KeyboardLightingStatus.Text = "Auto-off change failed";
        }
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
            TogUsbCharging.ToolTip = "This model does not expose power-off USB charging.";
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
    private bool _lcdOverdriveAvailable;

    /// <summary>
    /// LCD overdrive is a panel property, not a model one: the firmware answers 0xFF for panels
    /// that do not have it, so the toggle stays disabled rather than pretending to work.
    /// </summary>
    private void InitializeLcdOverdrive()
    {
        var state = AcerWmiManager.GetLcdOverdrive();
        _lcdOverdriveAvailable = state is { Supported: true };

        TogLcdOverdrive.IsEnabled = _lcdOverdriveAvailable;

        if (state is { Supported: true, On: var on })
            TogLcdOverdrive.IsChecked = on;
        else
            TogLcdOverdrive.ToolTip = "This panel does not support LCD overdrive.";

        _lcdOverdriveLoading = false;
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

    private void LcdOverdrive_Toggled(object sender, RoutedEventArgs e)
    {
        if (_lcdOverdriveLoading || !_lcdOverdriveAvailable) return;

        try
        {
            if (!AcerWmiManager.SetLcdOverdrive(TogLcdOverdrive.IsChecked == true))
                Logger.Warn("LCD overdrive: the firmware refused the change.");
        }
        catch (Exception ex)
        {
            Logger.Warn("LCD overdrive: could not apply the change.", ex);
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
