using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace Nitrous.Managers;

public static class SystemOSManager
{
    private static string? _cachedModel;

    public static async Task SetWindowsCpuLimitsAsync(int minPercent, int maxPercent)
    {
        await Task.Run(() =>
        {
            RunPowerCfg($"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {minPercent}");
            RunPowerCfg($"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX {maxPercent}");
            RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {minPercent}");
            RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX {maxPercent}");
            RunPowerCfg("/setactive SCHEME_CURRENT");
        });
    }

    private static void RunPowerCfg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args) { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit();
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion; public short dmDriverVersion; public short dmSize;
        public short dmDriverExtra; public int dmFields; public int dmPositionX; public int dmPositionY;
        public int dmDisplayOrientation; public int dmDisplayFixedOutput; public short dmColor;
        public short dmDuplex; public short dmYResolution; public short dmTTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
        public int dmDisplayFlags; public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    public static void ApplyAutoRefreshRate(bool isAcPower)
    {
        try
        {
            DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };

            // Passing 'null' natively targets the primary display, bypassing MUX switch issues
            if (!EnumDisplaySettings(null, -1, ref mode)) return;

            int currentWidth = mode.dmPelsWidth;
            int currentHeight = mode.dmPelsHeight;
            int maxHz = 60;
            int i = 0;

            DEVMODE testMode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };

            // Map all supported refresh rates for the exact current resolution
            while (EnumDisplaySettings(null, i++, ref testMode))
            {
                if (testMode.dmPelsWidth == currentWidth && testMode.dmPelsHeight == currentHeight)
                {
                    if (testMode.dmDisplayFrequency > maxHz)
                        maxHz = testMode.dmDisplayFrequency;
                }
            }

            int targetHz = isAcPower ? maxHz : 60;

            if (mode.dmDisplayFrequency != targetHz)
            {
                mode.dmDisplayFrequency = targetHz;
                _ = ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, 0x01, IntPtr.Zero);
            }
        }
        catch { }
    }

    public static bool CheckStartupTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/query /tn \"Nitrous\"") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit();
            return p != null && p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool ToggleStartupTask(bool enable, string exePath)
    {
        try
        {
            if (enable)
            {
                string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?><Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task""><Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT5S</Delay></LogonTrigger></Triggers><Principals><Principal id=""Author""><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><RunOnlyIfIdle>false</RunOnlyIfIdle></Settings><Actions Context=""Author""><Exec><Command>{exePath}</Command></Exec></Actions></Task>";
                string tempFile = Path.GetTempFileName();
                File.WriteAllText(tempFile, xml);

                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/create /tn \"Nitrous\" /xml \"{tempFile}\" /f") { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit();
                try { File.Delete(tempFile); } catch { }
                return p?.ExitCode == 0;
            }
            else
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/delete /tn \"Nitrous\" /f") { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit();
                return p?.ExitCode != 0;
            }
        }
        catch { return !enable; }
    }

    public static string GetSystemModel()
    {
        if (_cachedModel != null) return _cachedModel;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            using var collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    _cachedModel = item["Model"]?.ToString() ?? "Unknown System";
                    return _cachedModel;
                }
            }
        }
        catch { }
        return "Unknown System";
    }
}
