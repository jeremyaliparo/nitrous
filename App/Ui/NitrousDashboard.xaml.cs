using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Nitrous.Managers;
using Nitrous.Enums;

namespace Nitrous.Ui;

public partial class NitrousDashboard : Window
{
    private bool _isSyncingFans = false;

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
        bool isCustom = activeFan == FanProfile.Medium;
        CustomFanSection.Opacity = isCustom ? 1.0 : 0.4;

        CpuFanSlider.IsEnabled = isCustom;
        GpuFanSlider.IsEnabled = isCustom;
        TogUnifiedFans.IsEnabled = isCustom;

        if (!isCustom)
        {
            int val = activeFan == FanProfile.Max ? 100 : 0;
            CpuFanSlider.Value = val;
            GpuFanSlider.Value = val;
        }
        else
        {
            TogUnifiedFans.IsChecked = SettingsManager.Get("UnifiedFans", 1) == 1;
            CpuFanSlider.Value = SettingsManager.Get("CustomFanSpeedCpu", 50);
            GpuFanSlider.Value = SettingsManager.Get("CustomFanSpeedGpu", 50);
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

            if (mode == FanProfile.Medium)
            {
                _ = AcerWmiManager.SetCustomFansAsync((int)CpuFanSlider.Value, (int)GpuFanSlider.Value);
            }
            else
            {
                _ = AcerWmiManager.SetFansAsync(mode);
            }
            SettingsManager.Save("LastFanMode", mode.ToString());
        }
    }

    private void TogUnifiedFans_Click(object sender, RoutedEventArgs e)
    {
        bool isUnified = TogUnifiedFans.IsChecked == true;
        SettingsManager.Save("UnifiedFans", isUnified ? 1 : 0);

        if (isUnified)
        {
            GpuFanSlider.Value = CpuFanSlider.Value;
            FanSlider_DragCompleted(null, null!);
        }
    }

    private void CpuFanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuFanInput != null && !CpuFanInput.IsFocused) CpuFanInput.Text = ((int)e.NewValue).ToString();

        if (TogUnifiedFans?.IsChecked == true && !_isSyncingFans)
        {
            _isSyncingFans = true;
            GpuFanSlider?.Value = e.NewValue;
            _isSyncingFans = false;
        }
    }

    private void GpuFanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GpuFanInput != null && !GpuFanInput.IsFocused) GpuFanInput.Text = ((int)e.NewValue).ToString();

        if (TogUnifiedFans?.IsChecked == true && !_isSyncingFans)
        {
            _isSyncingFans = true;
            CpuFanSlider?.Value = e.NewValue;
            _isSyncingFans = false;
        }
    }

    private void FanSlider_DragCompleted(object? sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (CpuFanSlider.IsEnabled)
        {
            int cpuVal = (int)CpuFanSlider.Value;
            int gpuVal = (int)GpuFanSlider.Value;

            SettingsManager.Save("CustomFanSpeedCpu", cpuVal);
            SettingsManager.Save("CustomFanSpeedGpu", gpuVal);
            _ = AcerWmiManager.SetCustomFansAsync(cpuVal, gpuVal);
        }
    }

    private void FanInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            System.Windows.Input.TraversalRequest request = new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next);
            (sender as UIElement)?.MoveFocus(request);
            e.Handled = true;
        }
    }

    private void FanInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            if (int.TryParse(tb.Text, out int val))
            {
                val = Math.Clamp(val, 0, 100);
                tb.Text = val.ToString();

                if (tb.Name == "CpuFanInput") CpuFanSlider.Value = val;
                if (tb.Name == "GpuFanInput") GpuFanSlider.Value = val;

                FanSlider_DragCompleted(null, null!);
            }
            else
            {
                if (tb.Name == "CpuFanInput") tb.Text = ((int)CpuFanSlider.Value).ToString();
                if (tb.Name == "GpuFanInput") tb.Text = ((int)GpuFanSlider.Value).ToString();
            }
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
        SettingsManager.Save("WindowTop", (int)this.Top);
        SettingsManager.Save("WindowLeft", (int)this.Left);
    }
}
