using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Nitrous.Managers;
using Nitrous.Enums;

namespace Nitrous.Ui;

public partial class NitrousDashboard : Window
{
    public NitrousDashboard()
    {
        InitializeComponent();

        SystemModelText.Text = $"Nitrous on {SystemOSManager.GetSystemModel()}";
        DashVersionText.Text = $"Nitrous {UpdateManager.CurrentVersion}";
        SettingsVersionText.Text = $"Nitrous {UpdateManager.CurrentVersion}";
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => this.Hide();

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

        // Geometry strings instead of Text Icons!
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

        BtnPowerQuiet.IsChecked = activeMode == PowerProfile.Quiet;
        BtnPowerBal.IsChecked = activeMode == PowerProfile.Balanced;
        BtnPowerPerf.IsChecked = activeMode == PowerProfile.Performance;
        BtnPowerTurbo.IsChecked = activeMode == PowerProfile.Turbo;

        BtnFanAuto.IsChecked = activeFan == FanProfile.Auto;
        BtnFanMax.IsChecked = activeFan == FanProfile.Max;
        BtnFanCustom.IsChecked = activeFan == FanProfile.Medium;

        UpdateFanSliderState(activeFan);

        TogCharge.IsChecked = SettingsManager.Get("ChargeLimit", 0) == 1;
        TogAutoSwitch.IsChecked = SettingsManager.Get("AutoSwitch", 0) == 1;
        TogRefreshSwitch.IsChecked = SettingsManager.Get("RefreshAutoSwitch", 0) == 1;
        TogStartup.IsChecked = SystemOSManager.CheckStartupTask();
    }

    private void UpdateFanSliderState(FanProfile activeFan)
    {
        if (activeFan == FanProfile.Auto)
        {
            CustomFanSlider.IsEnabled = false;
            CustomFanSlider.Value = 0;
            CustomFanLabel.Text = "Fan Speed: Auto (0%)";
            CustomFanSection.Opacity = 0.4;
        }
        else if (activeFan == FanProfile.Max)
        {
            CustomFanSlider.IsEnabled = false;
            CustomFanSlider.Value = 100;
            CustomFanLabel.Text = "Fan Speed: Max (100%)";
            CustomFanSection.Opacity = 0.4;
        }
        else
        {
            CustomFanSlider.IsEnabled = true;
            CustomFanSlider.Value = SettingsManager.Get("CustomFanSpeed", 50);
            CustomFanLabel.Text = $"Custom Fan Speed: {CustomFanSlider.Value}%";
            CustomFanSection.Opacity = 1.0;
        }
    }

    private void PowerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton btn && Enum.TryParse(btn.Uid, out PowerProfile mode))
        {
            _ = AcerWmiManager.SetPowerModeAsync(mode);
            SettingsManager.Save("LastPowerMode", (int)mode);
        }
    }

    private void FanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton btn && Enum.TryParse(btn.Uid, out FanProfile mode))
        {
            UpdateFanSliderState(mode);

            int speedToSet = (mode == FanProfile.Medium) ? (int)CustomFanSlider.Value : (int)mode;
            _ = AcerWmiManager.SetFansAsync((FanProfile)speedToSet);
            SettingsManager.Save("LastFanMode", mode.ToString());
        }
    }

    private void CustomFanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CustomFanLabel != null && CustomFanSlider.IsEnabled)
        {
            CustomFanLabel.Text = $"Custom Fan Speed: {(int)e.NewValue}%";
        }
    }

    private void CustomFanSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (CustomFanSlider.IsEnabled)
        {
            int val = (int)CustomFanSlider.Value;
            SettingsManager.Save("CustomFanSpeed", val);
            _ = AcerWmiManager.SetFansAsync((FanProfile)val);
        }
    }

    private void TogCharge_Click(object sender, RoutedEventArgs e)
    {
        bool chk = TogCharge.IsChecked == true;
        SettingsManager.Save("ChargeLimit", chk ? 1 : 0);
        _ = AcerWmiManager.SetChargeLimitAsync(chk);
    }

    private void TogAutoSwitch_Click(object sender, RoutedEventArgs e) => SettingsManager.Save("AutoSwitch", TogAutoSwitch.IsChecked == true ? 1 : 0);

    private void TogRefreshSwitch_Click(object sender, RoutedEventArgs e) => SettingsManager.Save("RefreshAutoSwitch", TogRefreshSwitch.IsChecked == true ? 1 : 0);

    private void TogStartup_Click(object sender, RoutedEventArgs e) => SystemOSManager.ToggleStartupTask(TogStartup.IsChecked == true, System.Windows.Forms.Application.ExecutablePath);
}
