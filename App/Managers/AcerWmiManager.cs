using System;
using System.Management;
using System.Threading.Tasks;
using Nitrous.Enums;

namespace Nitrous.Managers;

public static class AcerWmiManager
{
    public static bool IsHardwareSupported()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AcerGamingFunction");
            return searcher.Get().Count > 0;
        }
        catch { return false; }
    }

    public static async Task SetPowerModeAsync(PowerProfile profile)
    {
        ulong payload = ((ulong)profile << 8) | 0x0B;
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingMiscSetting", payload.ToString());
    }

    public static async Task SetFansAsync(FanProfile profile)
    {
        if (profile == FanProfile.Auto)
        {
            // 1. Wipe the manual PWM overrides to 0% for the CORRECT Fan IDs (1 and 4)
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (0ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (0ul << 8)).ToString());

            // 2. Hand control back to the EC using Dual Auto Behavior (0x410005)
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0x410005ul.ToString());
        }
        else if (profile == FanProfile.Max)
        {
            // Dual Max Behavior
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0x820005ul.ToString());

            // Force 100% speed on Fan 1 (CPU) and Fan 4 (GPU) to guarantee both fans hit 100%
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (100ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (100ul << 8)).ToString());
        }
        else
        {
            // Dual Custom Behavior: 0xC30005 (CPU Custom + GPU Custom + Dual Fan Mask 5)
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0xC30005ul.ToString());

            ulong speedPercent = (ulong)profile;
            // Fan 1 (CPU): (Speed << 8) | 0x01
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (speedPercent << 8)).ToString());
            // Fan 4 (GPU): (Speed << 8) | 0x04
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (speedPercent << 8)).ToString());
        }
    }

    public static async Task SetChargeLimitAsync(bool enable)
    {
        byte param = enable ? (byte)1 : (byte)0;
        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryControl");
                foreach (ManagementObject instance in searcher.Get())
                {
                    using var inParams = instance.GetMethodParameters("SetBatteryHealthControl");
                    inParams["uBatteryNo"] = (byte)1;
                    inParams["uFunctionMask"] = (byte)1;
                    inParams["uFunctionStatus"] = param;
                    inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };
                    instance.InvokeMethod("SetBatteryHealthControl", inParams, null);
                }
            }
            catch { }
        });
    }

    private static async Task InvokeWmiAsync(string className, string methodName, string gmInputStr)
    {
        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", $"SELECT * FROM {className}");
                foreach (ManagementObject instance in searcher.Get())
                {
                    using var inParams = instance.GetMethodParameters(methodName);
                    inParams["gmInput"] = gmInputStr;
                    instance.InvokeMethod(methodName, inParams, null);
                }
            }
            catch { }
        });
    }
}
