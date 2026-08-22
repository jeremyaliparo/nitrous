using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Nitrous.Enums;
using Nitrous.Hooks;
using Nitrous.Managers;

namespace Nitrous.Ui;

public class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly NitrousDashboard dashboard;
    private readonly NitroKeyHook _nitroHook;

    public TrayApplication()
    {
        if (System.Windows.Application.Current == null) _ = new System.Windows.Application();

        dashboard = new NitrousDashboard();

        Icon appIcon = SystemIcons.Shield;
        try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield; } catch { }

        trayIcon = new NotifyIcon { Icon = appIcon, Visible = true, Text = "Nitrous" };
        trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(); };

        BuildContextMenu();
        SystemEvents.PowerModeChanged += OnPowerStateChanged;

        _ = Task.Run(() => UpdateManager.CheckForUpdatesAsync(true, () => Exit(null, EventArgs.Empty)));

        _nitroHook = new NitroKeyHook();
        _nitroHook.NitroKeyPressed += (s, e) => ShowDashboard();

        ShowDashboard();
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false
        };

        menu.Items.Add("Open Nitrous", null, (s, e) => ShowDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check for Updates...", null, async (s, e) => await UpdateManager.CheckForUpdatesAsync(false, () => Exit(null, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, Exit);

        trayIcon.ContextMenuStrip = menu;
    }

    private void ShowDashboard()
    {
        dashboard.Show();
        dashboard.Activate();
    }

    private void OnPowerStateChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange && SettingsManager.Get("AutoSwitch", 0) == 1)
        {
            bool isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

            string keyMode = isOnline ? "LastAcPowerMode" : "LastDcPowerMode";
            var activeMode = (PowerProfile)SettingsManager.Get(keyMode, (int)(isOnline ? PowerProfile.Performance : PowerProfile.Quiet));
            _ = AcerWmiManager.SetPowerModeAsync(activeMode);
            SettingsManager.Save("LastPowerMode", (int)activeMode);

            string keyFan = isOnline ? "LastAcFanMode" : "LastDcFanMode";
            var activeFan = Enum.TryParse(SettingsManager.Get(keyFan, "Auto"), out FanProfile f) ? f : FanProfile.Auto;

            if (activeFan == FanProfile.Medium)
            {
                int cpu = SettingsManager.Get("CustomFanSpeedCpu", 50);
                int gpu = SettingsManager.Get("CustomFanSpeedGpu", 50);
                _ = AcerWmiManager.SetCustomFansAsync(cpu, gpu);
            }
            else
            {
                _ = AcerWmiManager.SetFansAsync(activeFan);
            }
            SettingsManager.Save("LastFanMode", activeFan.ToString());

            if (dashboard.IsVisible) dashboard.Dispatcher.Invoke(() => dashboard.RefreshDashboardState());
        }
    }

    private void Exit(object? sender, EventArgs e)
    {
        _nitroHook.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerStateChanged;
        trayIcon.Visible = false;
        trayIcon.Dispose();
        System.Windows.Application.Current?.Shutdown();
        Application.Exit();
    }
}
