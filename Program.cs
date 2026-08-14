using System;
using System.Drawing;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Nitrous
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!IsAcerHardwareSupported())
            {
                MessageBox.Show("Acer WMI instances not found. This app only works on supported Acer hardware.",
                                "Hardware Not Supported", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.Run(new TrayApplication());
        }

        private static bool IsAcerHardwareSupported()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AcerGamingFunction"))
                {
                    return searcher.Get().Count > 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public class TrayApplication : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private bool isChargeLimitEnabled = false;
        private bool isAutoSwitchEnabled = false;

        // Menu Items
        private ToolStripMenuItem startupItem;
        private ToolStripMenuItem chargeLimitItem;
        private ToolStripMenuItem autoSwitchItem;
        private ToolStripMenuItem perfModeItem;
        private ToolStripMenuItem balModeItem;
        private ToolStripMenuItem quietModeItem;
        private ToolStripMenuItem fanAutoItem;
        private ToolStripMenuItem fanMaxItem;
        private ToolStripMenuItem fanMedItem;

        // Registry Path for saving settings
        private const string RegPath = @"Software\Nitrous";

        public TrayApplication()
        {
            // Extract the custom .ico we embedded in the .csproj!
            Icon? appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            trayIcon = new NotifyIcon()
            {
                Icon = appIcon ?? SystemIcons.Shield,
                Visible = true,
                Text = "Nitrous"
            };

            var contextMenu = new ContextMenuStrip();

            // --- App Settings ---
            startupItem = new ToolStripMenuItem("Run on Windows Startup", null, (s, e) => ToggleStartup())
            {
                CheckOnClick = true
            };
            contextMenu.Items.Add(startupItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            // --- Battery Control ---
            chargeLimitItem = new ToolStripMenuItem("Enable 80% Charge Limit", null, (s, e) => ToggleChargeLimit())
            {
                CheckOnClick = true
            };
            contextMenu.Items.Add(chargeLimitItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            // --- Power Modes ---
            autoSwitchItem = new ToolStripMenuItem("Auto-Switch Power on AC/Battery", null, (s, e) => ToggleAutoSwitch())
            {
                CheckOnClick = true
            };
            contextMenu.Items.Add(autoSwitchItem);

            perfModeItem = new ToolStripMenuItem("Power: Performance", null, (s, e) => { SetPowerModeAsync(0x04); UpdatePowerModeCheck(s as ToolStripMenuItem); SaveSetting("PowerMode", 4); });
            balModeItem = new ToolStripMenuItem("Power: Balanced", null, (s, e) => { SetPowerModeAsync(0x01); UpdatePowerModeCheck(s as ToolStripMenuItem); SaveSetting("PowerMode", 1); });
            quietModeItem = new ToolStripMenuItem("Power: Quiet", null, (s, e) => { SetPowerModeAsync(0x00); UpdatePowerModeCheck(s as ToolStripMenuItem); SaveSetting("PowerMode", 0); });

            contextMenu.Items.Add(perfModeItem);
            contextMenu.Items.Add(balModeItem);
            contextMenu.Items.Add(quietModeItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            // --- Fan Controls ---
            fanAutoItem = new ToolStripMenuItem("Fans: Auto", null, (s, e) => { SetFansAsync("Auto", 0); UpdateFanModeCheck(s as ToolStripMenuItem); SaveSetting("FanMode", "Auto"); });
            fanMaxItem = new ToolStripMenuItem("Fans: Max (100%)", null, (s, e) => { SetFansAsync("Custom", 100); UpdateFanModeCheck(s as ToolStripMenuItem); SaveSetting("FanMode", "Max"); });
            fanMedItem = new ToolStripMenuItem("Fans: Medium (50%)", null, (s, e) => { SetFansAsync("Custom", 50); UpdateFanModeCheck(s as ToolStripMenuItem); SaveSetting("FanMode", "Medium"); });

            contextMenu.Items.Add(fanAutoItem);
            contextMenu.Items.Add(fanMaxItem);
            contextMenu.Items.Add(fanMedItem);

            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Exit", null, Exit);

            trayIcon.ContextMenuStrip = contextMenu;

            LoadSettings();
        }

        // --- REGISTRY SAVE / LOAD HELPERS ---
        private void SaveSetting(string key, object value)
        {
            using (RegistryKey regKey = Registry.CurrentUser.CreateSubKey(RegPath))
            {
                regKey.SetValue(key, value);
            }
        }

        private T GetSetting<T>(string key, T defaultValue)
        {
            try
            {
                using (RegistryKey? regKey = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    object? val = regKey?.GetValue(key);
                    if (val != null) return (T)Convert.ChangeType(val, typeof(T))!;
                }
            }
            catch { }
            return defaultValue;
        }

        private void LoadSettings()
        {
            CheckStartupStatus();

            isChargeLimitEnabled = GetSetting("ChargeLimit", 0) == 1;
            chargeLimitItem.Checked = isChargeLimitEnabled;
            ApplyChargeLimitAsync();

            isAutoSwitchEnabled = GetSetting("AutoSwitch", 0) == 1;
            autoSwitchItem.Checked = isAutoSwitchEnabled;
            if (isAutoSwitchEnabled)
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                ApplyPowerModeForCurrentState();
            }
            else
            {
                int savedPowerMode = GetSetting("PowerMode", 1);
                SetPowerModeAsync((ulong)savedPowerMode);
                if (savedPowerMode == 4) UpdatePowerModeCheck(perfModeItem);
                else if (savedPowerMode == 0) UpdatePowerModeCheck(quietModeItem);
                else UpdatePowerModeCheck(balModeItem);
            }

            string savedFanMode = GetSetting("FanMode", "Auto");
            if (savedFanMode == "Max")
            {
                SetFansAsync("Custom", 100);
                UpdateFanModeCheck(fanMaxItem);
            }
            else if (savedFanMode == "Medium")
            {
                SetFansAsync("Custom", 50);
                UpdateFanModeCheck(fanMedItem);
            }
            else
            {
                SetFansAsync("Auto", 0);
                UpdateFanModeCheck(fanAutoItem);
            }
        }

        // --- ELEVATED STARTUP HELPER ---
        private void ToggleStartup()
        {
            string exePath = Application.ExecutablePath;
            // Because CheckOnClick = true, startupItem.Checked represents the new desired state
            bool enableStartup = startupItem.Checked;

            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    Arguments = enableStartup
                        ? $"/create /tn \"Nitrous\" /tr \"\\\"{exePath}\\\"\" /sc onlogon /rl highest /f"
                        : $"/delete /tn \"Nitrous\" /f"
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    process?.WaitForExit();

                    // If Windows Task Scheduler returned an error code, revert the checkmark
                    if (process == null || process.ExitCode != 0)
                    {
                        startupItem.Checked = !enableStartup;
                    }
                }
            }
            catch
            {
                // Revert checkmark state on exception
                startupItem.Checked = !enableStartup;
            }
        }

        private void CheckStartupStatus()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/query /tn \"Nitrous\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    process?.WaitForExit();
                    // Checkmark is checked ONLY if the task exists in Task Scheduler (ExitCode == 0)
                    startupItem.Checked = (process != null && process.ExitCode == 0);
                }
            }
            catch
            {
                startupItem.Checked = false;
            }
        }

        // --- UI CHECKMARK HELPERS ---
        private void UpdatePowerModeCheck(ToolStripMenuItem? activeItem)
        {
            if (trayIcon.ContextMenuStrip == null) return;
            Action updateAction = () =>
            {
                if (perfModeItem != null) perfModeItem.Checked = false;
                if (balModeItem != null) balModeItem.Checked = false;
                if (quietModeItem != null) quietModeItem.Checked = false;
                if (activeItem != null) activeItem.Checked = true;
            };
            if (!trayIcon.ContextMenuStrip.IsHandleCreated || !trayIcon.ContextMenuStrip.InvokeRequired) updateAction();
            else trayIcon.ContextMenuStrip.Invoke(updateAction);
        }

        private void UpdateFanModeCheck(ToolStripMenuItem? activeItem)
        {
            if (trayIcon.ContextMenuStrip == null) return;
            Action updateAction = () =>
            {
                if (fanAutoItem != null) fanAutoItem.Checked = false;
                if (fanMaxItem != null) fanMaxItem.Checked = false;
                if (fanMedItem != null) fanMedItem.Checked = false;
                if (activeItem != null) activeItem.Checked = true;
            };
            if (!trayIcon.ContextMenuStrip.IsHandleCreated || !trayIcon.ContextMenuStrip.InvokeRequired) updateAction();
            else trayIcon.ContextMenuStrip.Invoke(updateAction);
        }

        // --- WMI INSTANCE INVOKER ---
        private async Task<bool> InvokeWmiInstanceMethodAsync(string className, string methodName, Action<ManagementBaseObject> setParams)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher($@"root\wmi", $"SELECT * FROM {className}"))
                    {
                        foreach (ManagementObject instance in searcher.Get())
                        {
                            using (ManagementBaseObject inParams = instance.GetMethodParameters(methodName))
                            {
                                setParams(inParams);
                                using (ManagementBaseObject outParams = instance.InvokeMethod(methodName, inParams, null)) { }
                            }
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to execute {methodName}.\n\nError: {ex.Message}", "WMI Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            });
        }

        // --- HARDWARE CONTROLS ---
        private void ToggleChargeLimit()
        {
            isChargeLimitEnabled = chargeLimitItem.Checked;
            SaveSetting("ChargeLimit", isChargeLimitEnabled ? 1 : 0);
            ApplyChargeLimitAsync();
        }

        private async void ApplyChargeLimitAsync()
        {
            byte param = isChargeLimitEnabled ? (byte)1 : (byte)0;
            bool success = await InvokeWmiInstanceMethodAsync("BatteryControl", "SetBatteryHealthControl", inParams =>
            {
                inParams["uBatteryNo"] = (byte)1;
                inParams["uFunctionMask"] = (byte)1;
                inParams["uFunctionStatus"] = param;
                inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };
            });

            if (!success && trayIcon.ContextMenuStrip != null)
            {
                trayIcon.ContextMenuStrip.Invoke(new Action(() =>
                {
                    isChargeLimitEnabled = !isChargeLimitEnabled;
                    chargeLimitItem.Checked = isChargeLimitEnabled;
                    SaveSetting("ChargeLimit", isChargeLimitEnabled ? 1 : 0);
                }));
            }
        }

        private void ToggleAutoSwitch()
        {
            isAutoSwitchEnabled = autoSwitchItem.Checked;
            SaveSetting("AutoSwitch", isAutoSwitchEnabled ? 1 : 0);

            if (isAutoSwitchEnabled)
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                ApplyPowerModeForCurrentState();
            }
            else
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.StatusChange) ApplyPowerModeForCurrentState();
        }

        private void ApplyPowerModeForCurrentState()
        {
            if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online)
            {
                SetPowerModeAsync(0x04); // Performance
                UpdatePowerModeCheck(perfModeItem);
                SaveSetting("PowerMode", 4);
            }
            else if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
            {
                SetPowerModeAsync(0x00); // Quiet
                UpdatePowerModeCheck(quietModeItem);
                SaveSetting("PowerMode", 0);
            }
        }

        private async void SetPowerModeAsync(ulong profile)
        {
            ulong payload = (profile << 8) | 0x0B;
            await InvokeWmiInstanceMethodAsync("AcerGamingFunction", "SetGamingMiscSetting", inParams =>
            {
                inParams["gmInput"] = payload.ToString();
            });
        }

        private async void SetFansAsync(string mode, ulong speedPercent)
        {
            if (mode == "Auto")
            {
                await InvokeWmiInstanceMethodAsync("AcerGamingFunction", "SetGamingFanBehavior", inParams => { inParams["gmInput"] = 0x000009ul.ToString(); });
            }
            else if (mode == "Custom")
            {
                await InvokeWmiInstanceMethodAsync("AcerGamingFunction", "SetGamingFanBehavior", inParams => { inParams["gmInput"] = 0x820009ul.ToString(); });
                await InvokeWmiInstanceMethodAsync("AcerGamingFunction", "SetGamingFanSpeed", inParams => { inParams["gmInput"] = (0ul | (speedPercent << 8)).ToString(); });
                await InvokeWmiInstanceMethodAsync("AcerGamingFunction", "SetGamingFanSpeed", inParams => { inParams["gmInput"] = (1ul | (speedPercent << 8)).ToString(); });
            }
        }

        private void Exit(object? sender, EventArgs e)
        {
            if (isAutoSwitchEnabled) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Application.Exit();
        }
    }
}
