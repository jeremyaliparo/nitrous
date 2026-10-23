using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;
using Nitrous.Managers;

using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using TextBox = System.Windows.Controls.TextBox;

namespace Nitrous.Ui;

public partial class NitrousDashboard : Window
{
    private bool _isDialogOpen;

    public NitrousDashboard()
    {
        InitializeComponent();

        DataContext = new DashboardViewModel();

        DashVersionText.Text = GpuVersionText.Text = KeyboardVersionText.Text = SettingsVersionText.Text =
            $"Nitrous {UpdateManager.CurrentVersion}";

        System.Threading.Tasks.Task.Run(() =>
        {
            var modelName = SystemInfoManager.GetSystemModel();
            Dispatcher.InvokeAsync(() => SystemModelText.Text = $"{modelName}");
        });

        SystemEvents.PowerModeChanged += OnPowerStateChanged;
        StateChanged += Window_StateChanged;
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
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void SetActiveNav(int index)
    {
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
    }

    private void NavDashBtn_Click(object sender, RoutedEventArgs e) => SetActiveNav(0);
    private void NavGpuBtn_Click(object sender, RoutedEventArgs e) => SetActiveNav(1);
    private void NavKeyboardBtn_Click(object sender, RoutedEventArgs e) => SetActiveNav(2);
    private void NavSetBtn_Click(object sender, RoutedEventArgs e) => SetActiveNav(3);

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
            vm.SetTelemetryEnabled(Visibility == Visibility.Visible && WindowState != WindowState.Minimized);

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
        var activeRefresh = (RefreshProfile)SettingsManager.Get("RefreshMode", (int)RefreshProfile.Auto);

        BtnPowerQuiet.IsChecked = activeMode == PowerProfile.Quiet;
        BtnPowerBal.IsChecked = activeMode == PowerProfile.Balanced;
        BtnPowerPerf.IsChecked = activeMode == PowerProfile.Performance;
        BtnPowerTurbo.IsChecked = activeMode == PowerProfile.Turbo;

        BtnFanAuto.IsChecked = activeFan == FanProfile.Auto;
        BtnFanMax.IsChecked = activeFan == FanProfile.Max;
        BtnFanCustom.IsChecked = activeFan == FanProfile.Medium;

        BtnRefreshAuto.IsChecked = activeRefresh == RefreshProfile.Auto;
        BtnRefresh60.IsChecked = activeRefresh == RefreshProfile.Hz60;
        BtnRefreshMax.IsChecked = activeRefresh == RefreshProfile.MaxHz;

        if (DataContext is DashboardViewModel vm)
        {
            vm.ActivePowerProfile = activeMode;
            vm.IsCustomFanEnabled = activeFan == FanProfile.Medium;
        }

        if (TxtHkCycle != null)
        {
            TxtHkCycle.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_CyclePower", ""));
            TxtHkDash.Text = HotkeyBinding.Format(SettingsManager.Get("Hotkey_Dashboard", ""));
        }
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
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (Topmost || _isDialogOpen) return;

        WindowState = WindowState.Minimized;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        SystemEvents.PowerModeChanged -= OnPowerStateChanged;
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

        txt.Text = HotkeyBinding.Format(savedValue);
        Keyboard.ClearFocus();
    }
}
