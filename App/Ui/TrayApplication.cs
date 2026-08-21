using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Nitrous.Managers;
using Nitrous.Enums;

namespace Nitrous.Ui;

public class TrayApplication : ApplicationContext
{
    private NotifyIcon trayIcon;
    private PowerLineStatus lastPowerStatus = PowerLineStatus.Unknown;

    // Toggles
    private ToolStripMenuItem startupItem = null!;
    private ToolStripMenuItem chargeLimitItem = null!;
    private ToolStripMenuItem autoSwitchItem = null!;
    private ToolStripMenuItem refreshAutoSwitchItem = null!;

    // Groups for exclusive checking (DRY UI)
    private readonly List<ToolStripMenuItem> powerItems = [];
    private readonly List<ToolStripMenuItem> fanItems = [];

    public TrayApplication()
    {
        trayIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield,
            Visible = true,
            Text = $"Nitrous {UpdateManager.CurrentVersion}"
        };

        BuildContextMenu();
        LoadSettings();

        _ = Task.Run(() => UpdateManager.CheckForUpdatesAsync(true, () => Exit(null, EventArgs.Empty)));
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        // Header
        menu.Items.Add(new ToolStripMenuItem($"Nitrous {UpdateManager.CurrentVersion}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Check for Updates...", null, async (s, e) => await UpdateManager.CheckForUpdatesAsync(false, () => Exit(null, EventArgs.Empty))));
        menu.Items.Add(new ToolStripSeparator());

        // Toggles
        startupItem = CreateToggle("Run on Windows Startup", (s, e) => startupItem.Checked = SystemOSManager.ToggleStartupTask(startupItem.Checked, Application.ExecutablePath));
        chargeLimitItem = CreateToggle("Enable 80% Charge Limit", (s, e) => { SettingsManager.Save("ChargeLimit", chargeLimitItem.Checked ? 1 : 0); _ = AcerWmiManager.SetChargeLimitAsync(chargeLimitItem.Checked); });
        autoSwitchItem = CreateToggle("Auto-Switch Power on AC/Battery", (s, e) => ToggleAutoSwitch());
        refreshAutoSwitchItem = CreateToggle("Auto-Switch Refresh Rate", (s, e) => { SettingsManager.Save("RefreshAutoSwitch", refreshAutoSwitchItem.Checked ? 1 : 0); ApplyPowerModeForCurrentState(); });

        menu.Items.AddRange(new ToolStripItem[] { startupItem, new ToolStripSeparator(), chargeLimitItem, new ToolStripSeparator(), autoSwitchItem, refreshAutoSwitchItem, new ToolStripSeparator() });

        // Power Modes
        powerItems.Add(CreatePowerMenu("Power: Turbo", PowerProfile.Turbo, 100, FanProfile.Max));
        powerItems.Add(CreatePowerMenu("Power: Performance", PowerProfile.Performance, 100, FanProfile.Auto));
        powerItems.Add(CreatePowerMenu("Power: Balanced", PowerProfile.Balanced, 100, FanProfile.Auto));
        powerItems.Add(CreatePowerMenu("Power: Quiet", PowerProfile.Quiet, 99, FanProfile.Auto));
        powerItems.ForEach(item => menu.Items.Add(item));
        menu.Items.Add(new ToolStripSeparator());

        // Fan Modes
        fanItems.Add(CreateFanMenu("Fans: Auto", FanProfile.Auto));
        fanItems.Add(CreateFanMenu("Fans: Max (100%)", FanProfile.Max));
        fanItems.Add(CreateFanMenu("Fans: Medium (50%)", FanProfile.Medium));
        fanItems.Add(CreateFanMenu("Fans: Quiet (25%)", FanProfile.Quiet));
        fanItems.ForEach(item => menu.Items.Add(item));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit", null, Exit);
        trayIcon.ContextMenuStrip = menu;
    }

    private ToolStripMenuItem CreateToggle(string text, EventHandler onClick)
    {
        return new ToolStripMenuItem(text, null, onClick) { CheckOnClick = true };
    }

    private ToolStripMenuItem CreatePowerMenu(string text, PowerProfile mode, int maxCpu, FanProfile linkedFan)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (s, e) =>
        {
            ApplyPowerState(mode, maxCpu, linkedFan);
            UpdateCheckmarks(powerItems, item);
            UpdateCheckmarks(fanItems, fanItems.Find(f => f.Tag?.ToString() == linkedFan.ToString()));
        };
        item.Tag = mode.ToString();
        return item;
    }

    private ToolStripMenuItem CreateFanMenu(string text, FanProfile mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (s, e) =>
        {
            _ = AcerWmiManager.SetFansAsync(mode);
            if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online)
                SettingsManager.Save("LastAcFanMode", mode.ToString());
            else
                SettingsManager.Save("LastDcFanMode", mode.ToString());

            UpdateCheckmarks(fanItems, item);
        };
        item.Tag = mode.ToString();
        return item;
    }

    private void UpdateCheckmarks(List<ToolStripMenuItem> group, ToolStripMenuItem? activeItem)
    {
        if (trayIcon.ContextMenuStrip!.InvokeRequired) { trayIcon.ContextMenuStrip.Invoke(new Action(() => UpdateCheckmarks(group, activeItem))); return; }
        group.ForEach(i => i.Checked = false);
        if (activeItem != null) activeItem.Checked = true;
    }

    private void LoadSettings()
    {
        startupItem.Checked = SystemOSManager.CheckStartupTask();
        chargeLimitItem.Checked = SettingsManager.Get("ChargeLimit", 0) == 1;
        refreshAutoSwitchItem.Checked = SettingsManager.Get("RefreshAutoSwitch", 0) == 1;
        autoSwitchItem.Checked = SettingsManager.Get("AutoSwitch", 0) == 1;

        _ = AcerWmiManager.SetChargeLimitAsync(chargeLimitItem.Checked);

        if (autoSwitchItem.Checked)
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            ApplyPowerModeForCurrentState();
        }
        else
        {
            bool isOnline = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            string keyMode = isOnline ? "LastAcPowerMode" : "LastDcPowerMode";
            string keyFan = isOnline ? "LastAcFanMode" : "LastDcFanMode";
            int defaultMode = isOnline ? (int)PowerProfile.Balanced : (int)PowerProfile.Quiet;

            var savedMode = (PowerProfile)SettingsManager.Get(keyMode, defaultMode);
            var savedFan = Enum.TryParse(SettingsManager.Get(keyFan, "Auto"), out FanProfile f) ? f : FanProfile.Auto;

            int cpuMax = savedMode == PowerProfile.Quiet ? 99 : 100;
            ApplyPowerState(savedMode, cpuMax, savedFan);

            UpdateCheckmarks(powerItems, powerItems.Find(i => i.Tag?.ToString() == savedMode.ToString()));
            UpdateCheckmarks(fanItems, fanItems.Find(i => i.Tag?.ToString() == savedFan.ToString()));
        }
    }

    private void ToggleAutoSwitch()
    {
        SettingsManager.Save("AutoSwitch", autoSwitchItem.Checked ? 1 : 0);
        if (autoSwitchItem.Checked)
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            ApplyPowerModeForCurrentState();
        }
        else SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) ApplyPowerModeForCurrentState();
    }

    private void ApplyPowerModeForCurrentState()
    {
        var currentStatus = SystemInformation.PowerStatus.PowerLineStatus;
        if (currentStatus == lastPowerStatus) return; // Debounce Micro-Drops
        lastPowerStatus = currentStatus;

        if (currentStatus == PowerLineStatus.Online)
        {
            var savedMode = (PowerProfile)SettingsManager.Get("LastAcPowerMode", (int)PowerProfile.Performance);
            var savedFan = Enum.TryParse(SettingsManager.Get("LastAcFanMode", "Auto"), out FanProfile f) ? f : FanProfile.Auto;

            int cpuMax = savedMode == PowerProfile.Quiet ? 99 : 100;
            ApplyPowerState(savedMode, cpuMax, savedFan);

            UpdateCheckmarks(powerItems, powerItems.Find(i => i.Tag?.ToString() == savedMode.ToString()));
            UpdateCheckmarks(fanItems, fanItems.Find(i => i.Tag?.ToString() == savedFan.ToString()));

            if (refreshAutoSwitchItem.Checked)
            {
                SystemOSManager.SetRefreshRate(SystemOSManager.GetMaxRefreshRate());
            }
        }
        else if (currentStatus == PowerLineStatus.Offline)
        {
            var savedMode = (PowerProfile)SettingsManager.Get("LastDcPowerMode", (int)PowerProfile.Quiet);
            var savedFan = Enum.TryParse(SettingsManager.Get("LastDcFanMode", "Auto"), out FanProfile f) ? f : FanProfile.Auto;

            int cpuMax = savedMode == PowerProfile.Quiet ? 99 : 100;
            ApplyPowerState(savedMode, cpuMax, savedFan);

            UpdateCheckmarks(powerItems, powerItems.Find(i => i.Tag?.ToString() == savedMode.ToString()));
            UpdateCheckmarks(fanItems, fanItems.Find(i => i.Tag?.ToString() == savedFan.ToString()));

            if (refreshAutoSwitchItem.Checked)
            {
                SystemOSManager.SetRefreshRate(60);
            }
        }
    }

    private void ApplyPowerState(PowerProfile power, int maxCpu, FanProfile fan)
    {
        _ = AcerWmiManager.SetPowerModeAsync(power);
        _ = SystemOSManager.SetWindowsCpuLimitsAsync(5, maxCpu);
        _ = AcerWmiManager.SetFansAsync(fan);

        if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online)
        {
            SettingsManager.Save("LastAcPowerMode", (int)power);
            SettingsManager.Save("LastAcFanMode", fan.ToString());
        }
        else
        {
            SettingsManager.Save("LastDcPowerMode", (int)power);
            SettingsManager.Save("LastDcFanMode", fan.ToString());
        }
    }

    private void Exit(object? sender, EventArgs e)
    {
        if (autoSwitchItem.Checked) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        trayIcon.Visible = false;
        trayIcon.Dispose();
        Application.Exit();
    }
}
