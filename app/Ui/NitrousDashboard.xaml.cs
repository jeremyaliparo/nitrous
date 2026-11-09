using System;
using System.Linq;
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
using Nitrous.Ui.Theme;

using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
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

        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        BuildAppearanceSwatches();

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

        // The tray, a hotkey or another process may have asked for (or released) WhisperMode
        // without changing the mode, fan or refresh. That request is a registry flag, so follow
        // it every tick rather than only when one of the other values moves.
        var whisperActive = SettingsManager.Get("WhisperActive", 0) == 1;
        if (DataContext is DashboardViewModel whisperVm && whisperVm.WhisperModeActive != whisperActive)
            whisperVm.WhisperModeActive = whisperActive;

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

        var key = page?.ToLowerInvariant() ?? string.Empty;

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

        var index = key switch
        {
            "gpu" => 1,
            "kb" or "keyboard" => 2,
            "setup" or "settings" or "setup-hotkeys" => 3,
            _ => 0
        };

        // "lighting-area", "lighting-logo", "lighting-area-dynamic" all open the same page.
        if (key.StartsWith("lighting")) index = 2;

        SetActiveNav(index);

        if (key == "setup-hotkeys")
        {
            UpdateLayout();
            TxtHkCycle.BringIntoView();
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
                3 => SettingsPage,
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
            vm.RefreshGpuOffsets();

        // First visit to Lighting on a model whose lid logo has never been confirmed: settle it
        // before any more writes are sent to device 0x83.
        if (index == 2 && !_keyboardLoading && !_captureMode && _keyboardAvailable && LogoAvailability < 0)
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
            CornerRadius = new CornerRadius(6),
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
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Child = inner
        };

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
            CornerRadius = new CornerRadius(6),
            Background = colour is { } c ? new SolidColorBrush(c) : null
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
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Child = inner
        };

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
    }

    private void OnThemeChanged()
    {
        ApplyNavColours();
        RefreshAppearanceSelection();

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
    private string _syncTarget = LightingSync.TargetBoth;
    private bool _syncToggleUpdating;
    private bool _screenPulse;
    private bool _globalEffectsFiltered;
    private bool _updatingGlobalEffects;

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
    private static bool LogoIsUsable => LogoAvailability == 1;

    // The lighting write switches the backlight back on, but the auto-off is a separate countdown
    // on the EC that only a write carrying it arms. Debounced so dragging the brightness slider
    // costs one EC exchange at the end rather than one per tick.
    private readonly DispatcherTimer _autoOffRearmTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };

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

        _keyboardAvailable = AcerKeyboard.IsAvailable();

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

        foreach (var (name, _) in DynamicEffects)
            KeyboardEffectBox.Items.Add(name);

        foreach (var (name, _) in KeyboardEffects)
            GlobalEffectBox.Items.Add(name);

        foreach (var (name, _) in LogoEffects)
            LogoEffectBox.Items.Add(name);

        _keyboardEffect = LoadEffect(KeyboardEffectBox, DynamicEffects, "KbEffect");
        _globalEffect = LoadEffect(GlobalEffectBox, KeyboardEffects, "KbGlobal_Effect");
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
            _ => LightingSync.SoundModeSpectrum
        };

        SoundModeBox.Items.Add("Peak");
        SoundModeBox.Items.Add("Spectrum");
        SoundModeBox.Items.Add("Beat");
        SoundModeBox.SelectedIndex = soundMode == LightingSync.SoundModePeak ? 0
            : soundMode == LightingSync.SoundModeBeat ? 2
            : 1;

        SyncTargetBox.Items.Add("Keyboard");
        SyncTargetBox.Items.Add("Keyboard + lid logo");
        SyncTargetBox.SelectedIndex = _syncTarget == LightingSync.TargetKeyboard ? 0 : 1;

        TogScreenSync.IsChecked = _syncEnabled && _syncSource == LightingSync.SourceScreen;
        TogSoundSync.IsChecked = _syncEnabled && _syncSource == LightingSync.SourceSound;

        if (_syncEnabled)
            SettingsManager.Save("KbLightingSource",
                LightingGlobalTab.IsChecked == true ? "Global" : "Area");

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

            var beat = !string.Equals(
                SettingsManager.Get("KbBrightSyncMode", LightingSync.SoundModeBeat),
                LightingSync.SoundModePeak, StringComparison.OrdinalIgnoreCase);
            BrightnessSyncModeBox.SelectedIndex = beat ? 0 : 1;

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

        if (_keyboardAvailable)
        {
            if (source == "Global") ApplyGlobalLighting();
            else ApplyAreaLighting(reportLogoFailure: false);
        }

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
    /// Sound sync needs a static base: its Effect list is reduced to Static while it runs, and the
    /// full list returns afterwards. Screen sync leaves the list full but disabled.
    /// </summary>
    private void UpdateGlobalEffectOptions(bool soundSync)
    {
        if (_globalEffectsFiltered == soundSync) return;

        _globalEffectsFiltered = soundSync;
        _updatingGlobalEffects = true;
        try
        {
            GlobalEffectBox.Items.Clear();

            if (soundSync)
            {
                GlobalEffectBox.Items.Add("Static");
                _globalEffect = KeyboardEffect.Static;
                SettingsManager.Save("KbGlobal_Effect", "Static");
                GlobalEffectBox.SelectedIndex = 0;
                return;
            }

            foreach (var (name, _) in KeyboardEffects)
                GlobalEffectBox.Items.Add(name);

            var index = Array.FindIndex(KeyboardEffects, item => item.Effect == _globalEffect);
            GlobalEffectBox.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _updatingGlobalEffects = false;
        }
    }

    /// <summary>
    /// Enables only what can act. The speed row disappears for Static because the firmware ignores
    /// it there, and the logo's colour greys to "Auto" for the two modes that cycle their own
    /// palette rather than using the colour sent.
    /// </summary>
    private void UpdateLightingInteractivity()
    {
        var syncOn = _syncEnabled;
        var screenSync = syncOn && _syncSource == LightingSync.SourceScreen;
        var soundSync = syncOn && _syncSource == LightingSync.SourceSound;

        // Sound sync needs a static base, so its Effect list is just Static; Screen sync owns the
        // colours and disables the list.
        UpdateGlobalEffectOptions(soundSync);

        var globalAnimated = IsAnimated(_globalEffect);
        var globalHasColour = _globalEffect == KeyboardEffect.Static;

        GlobalEffectBox.IsEnabled = _keyboardAvailable && !screenSync;
        GlobalBrightness.IsEnabled = _keyboardAvailable;
        GlobalSpeed.IsEnabled = _keyboardAvailable && globalAnimated && !syncOn;
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

        if (soundSync && KbStaticTab.IsChecked != true)
            KbStaticTab.IsChecked = true;
        if (soundSync) SettingsManager.Save("KbStyle", "Static");

        KbStaticTab.IsEnabled = !screenSync;
        KbDynamicTab.IsEnabled = !screenSync && !soundSync;
        ScreenSyncKeyboardHint.Text = screenSync
            ? "Screen sync controls the colours; brightness is the cap."
            : "Sound sync uses the static zone colours; brightness is the cap.";
        ScreenSyncKeyboardHint.Visibility = screenSync || soundSync
            ? Visibility.Visible
            : Visibility.Collapsed;

        KeyboardEffectBox.IsEnabled = _keyboardAvailable && !screenSync && !soundSync;
        KeyboardBrightness.IsEnabled = _keyboardAvailable;
        KbSpeed.IsEnabled = _keyboardAvailable && IsAnimated(_keyboardEffect) && !screenSync &&
                            !soundSync;
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
        if (IgnoreLightingChanges || _updatingGlobalEffects || GlobalEffectBox.SelectedIndex < 0 ||
            _syncEnabled)
            return;

        _globalEffect = KeyboardEffects[GlobalEffectBox.SelectedIndex].Effect;
        SettingsManager.Save("KbGlobal_Effect", KeyboardEffects[GlobalEffectBox.SelectedIndex].Name);
        UpdateLightingInteractivity();
        ApplyGlobalLighting();
    }

    private void GlobalBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _globalBrightness = (int)Math.Round(e.NewValue);
        if (GlobalBrightnessText != null) GlobalBrightnessText.Text = $"{_globalBrightness}%";
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        SettingsManager.Save("KbGlobal_Brightness", _globalBrightness);
        if (_syncEnabled) return;

        ApplyGlobalLighting();
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
        if (!_keyboardAvailable || _logoCheckBusy || _captureMode) return;

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
        var usable = LogoAvailability != 0;
        LogoControlsPanel.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        LogoUnavailablePanel.Visibility = usable ? Visibility.Collapsed : Visibility.Visible;
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

        _keyboardEffect = DynamicEffects[KeyboardEffectBox.SelectedIndex].Effect;
        SettingsManager.Save("KbEffect", DynamicEffects[KeyboardEffectBox.SelectedIndex].Name);
        UpdateLightingInteractivity();
        if (!_syncEnabled) ApplyKeyboardLighting();
    }

    private void KeyboardBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _keyboardBrightness = (int)Math.Round(e.NewValue);
        if (KeyboardBrightnessText != null) KeyboardBrightnessText.Text = $"{_keyboardBrightness}%";
        if (IgnoreLightingChanges || !_keyboardAvailable) return;

        SettingsManager.Save("KbBrightness", _keyboardBrightness);
        if (!_syncEnabled) ApplyKeyboardLighting();
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
            BrightnessSyncModeBox.SelectedIndex == 1
                ? LightingSync.SoundModePeak
                : LightingSync.SoundModeBeat);
    }

    private void BrightnessSyncSensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)Math.Round(e.NewValue);
        if (BrightnessSyncSensitivityText != null) BrightnessSyncSensitivityText.Text = value.ToString();
        if (_keyboardLoading || _brightnessSyncUpdating) return;

        SettingsManager.Save("KbSoundSensitivity", value);
    }

    private void UpdateBrightnessSyncUi()
    {
        var on = _keyboardBrightnessAvailable && TogBrightnessSync.IsChecked == true;

        BrightnessSyncOptions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        KeyboardBrightnessBox.IsEnabled = _keyboardBrightnessAvailable && !on;
        BrightnessSyncStatus.Text = on
            ? "The tray is pulsing the backlight with the system audio."
            : string.Empty;
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
                ? AcerKeyboard.SetEffect(
                    _keyboardEffect, _keyboardBrightness, _keyboardSpeed, leftwards: false, _zoneColors[0])
                : AcerKeyboard.SetZoneColors(_zoneColors, _keyboardBrightness);

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

        try
        {
            // The Global control is five steps, as the OEM software's is. Each step is a matched
            // pair: keyboard 1/3/5/7/9 with logo 1/2/3/4/5.
            var step = Math.Clamp(_globalSpeed, 1, LogoMaxSpeed);
            var keyboardSpeed = KeyboardSpeedFor(step);

            var keyboardApplied = _globalEffect == KeyboardEffect.Static
                ? AcerKeyboard.SetZoneColors(
                    Enumerable.Repeat(_globalColor, AcerKeyboard.ZoneCount).ToArray(), _globalBrightness)
                : AcerKeyboard.SetEffect(
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
