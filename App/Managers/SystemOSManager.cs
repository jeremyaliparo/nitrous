using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Nitrous.Managers;

public static class SystemOSManager
{
    private const string InternalDisplayDevice = @"\\.\DISPLAY1";

    // OS CPU Throttling
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
            Process.Start(new ProcessStartInfo("powercfg.exe", args) { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
        }
        catch { }
    }

    // Native Display API (Targets Built-in Display Only)
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

    public static int GetMaxRefreshRate()
    {
        int maxHz = 60;
        try
        {
            DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
            if (EnumDisplaySettings(InternalDisplayDevice, -1, ref mode))
            {
                int w = mode.dmPelsWidth, h = mode.dmPelsHeight, i = 0;
                while (EnumDisplaySettings(InternalDisplayDevice, i++, ref mode))
                {
                    if (mode.dmPelsWidth == w && mode.dmPelsHeight == h && mode.dmDisplayFrequency > maxHz)
                        maxHz = mode.dmDisplayFrequency;
                }
            }
        }
        catch { }
        return maxHz;
    }

    public static void SetRefreshRate(int targetHz)
    {
        try
        {
            DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
            // If the laptop display is off/disabled, EnumDisplaySettings returns false and aborts safely
            if (EnumDisplaySettings(InternalDisplayDevice, -1, ref mode) && mode.dmDisplayFrequency != targetHz)
            {
                mode.dmDisplayFrequency = targetHz;
                ChangeDisplaySettingsEx(InternalDisplayDevice, ref mode, IntPtr.Zero, 0x01, IntPtr.Zero);
            }
        }
        catch { }
    }

    // Elevated Startup Control
    public static bool CheckStartupTask()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/query /tn \"Nitrous\"") { CreateNoWindow = true, UseShellExecute = false });
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
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/create /tn \"Nitrous\" /xml \"{tempFile}\" /f") { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit();
                try { File.Delete(tempFile); } catch { }
                return p?.ExitCode == 0;
            }
            else
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/delete /tn \"Nitrous\" /f") { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit();
                return p?.ExitCode != 0;
            }
        }
        catch { return !enable; }
    }
}
