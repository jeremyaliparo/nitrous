using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Nitrous.Managers;
using Nitrous.Enums;

namespace Nitrous.Ui;

public partial class NitrousDashboard : Window
{
    public NitrousDashboard()
    {
        InitializeComponent();

        DataContext = new DashboardViewModel();

        DashVersionText.Text = $"Nitrous {UpdateManager.CurrentVersion}";
        SettingsVersionText.Text = $"Nitrous {UpdateManager.CurrentVersion}";

        System.Threading.Tasks.Task.Run(() =>
        {
            string modelName = SystemInfoManager.GetSystemModel();
            Dispatcher.Invoke(() => SystemModelText.Text = $"Nitrous on {modelName}");
        });

        SystemEvents.PowerModeChanged += OnPowerStateChanged;
    }

    private void OnPowerStateChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            System.Threading.Tasks.Task.Delay(4000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() => RefreshDashboardState());
            });
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => this.Close();

    private void NavDashBtn_Click(object sender, RoutedEventArgs e)
    {
        DashPage.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        NavDashIcon.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B388FF"));
        NavSetIcon.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#888890"));
    }

    private void NavSetBtn_Click(object sender, RoutedEventArgs e)
    {
        DashPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        NavDashIcon.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#888890"));
        NavSetIcon.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B388FF"));
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (this.Visibility == Visibility.Visible) RefreshDashboardState();
    }

    public void RefreshDashboardState()
    {
        bool isOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        var powerColor = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isOnline ? "#FF453A" : "#34C759"));
        string powerText = isOnline ? "AC POWER" : "BATTERY";

        var acGeom = Geometry.Parse("M7,2V13H10V22L17,10H13L17,2H7Z");
        var battGeom = Geometry.Parse("M16.67,4H15V2H9V4H7.33A1.33,1.33 0 0,0 6,5.33V20.67C6,21.4 6.6,22 7.33,22H16.67A1.33,1.33 0 0,0 18,20.67V5.33C18,4.6 17.4,4 16.67,4Z");

        DashPowerPillBorder.BorderBrush = powerColor;
        DashPowerPillIcon.Fill = powerColor;
        DashPowerPillText.Foreground = powerColor;
        DashPowerPillText.Text = powerText;
        DashPowerPillIcon.Data = isOnline ? acGeom : battGeom;

        SettingsPowerPillBorder.BorderBrush = powerColor;
        SettingsPowerPillIcon.Fill = powerColor;
        SettingsPowerPillText.Foreground = powerColor;
        SettingsPowerPillText.Text = powerText;
        SettingsPowerPillIcon.Data = isOnline ? acGeom : battGeom;

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
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int top = SettingsManager.Get("WindowTop", -9999);
        int left = SettingsManager.Get("WindowLeft", -9999);
        if (top != -9999 && left != -9999)
        {
            this.Top = top;
            this.Left = left;
        }
        else
        {
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        SystemEvents.PowerModeChanged -= OnPowerStateChanged;
        SettingsManager.Save("WindowTop", (int)this.Top);
        SettingsManager.Save("WindowLeft", (int)this.Left);
    }
}
