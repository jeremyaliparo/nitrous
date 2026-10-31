using Nitrous.Enums;

namespace Nitrous.Managers;

/// <summary>
/// The undocumented Acer EC/WMI contract. Keeping the magic addresses and payloads in one
/// place makes it possible to add per-model support without hunting through call sites.
/// </summary>
internal static class AcerWmiProtocol
{
    public const string WmiNamespace = @"root\wmi";
    public const string GamingClass = "AcerGamingFunction";
    public const string BatteryClass = "BatteryControl";

    public const string GetGamingMiscSetting = "GetGamingMiscSetting";
    public const string SetGamingMiscSetting = "SetGamingMiscSetting";
    public const string SetGamingFanBehavior = "SetGamingFanBehavior";
    public const string SetGamingFanSpeed = "SetGamingFanSpeed";
    public const string GetGamingSysInfo = "GetGamingSysInfo";
    public const string SetBatteryHealthControl = "SetBatteryHealthControl";

    // GetGamingSysInfo addresses. The GPU temperature addresses differ between BIOS versions.
    public const uint CpuTemperature = 0x0101;
    public const uint CpuFanRpm = 0x0201;
    public const uint GpuFanRpm = 0x0601;
    public const uint GpuTemperaturePrimary = 0x0A01;
    public const uint GpuTemperatureFallbackA = 0x0901;
    public const uint GpuTemperatureFallbackB = 0x0B01;

    public const ulong ByteMask = 0xFF;
    public const ulong WordMask = 0xFFFF;

    // GetGamingMiscSetting(0x0A) returns the supported-profiles bitmap.
    public const uint SupportedProfilesSetting = 0x0A;
    public const int TurboProfileBit = 5;

    // SetGamingFanBehavior payloads.
    public const ulong FanBehaviorAuto = 0x41000F;
    public const ulong FanBehaviorMax = 0x82000F;
    public const ulong FanBehaviorCustom = 0xC3000F;

    // SetGamingFanSpeed device masks.
    public const ulong CpuFanDevice = 1;
    public const ulong GpuFanDeviceA = 2;
    public const ulong GpuFanDeviceB = 4;

    public const ulong PowerModeOpcode = 0x0B;

    // BatteryControl payload.
    public const byte BatteryNumber = 1;
    public const byte BatteryFunctionMask = 1;

    // SetGamingProfile: the gaming profile settings (Windows key lock, LCD overdrive).
    public const string SetGamingProfile = "SetGamingProfile";
    public const string GetGamingProfile = "GetGamingProfile";
    public const uint ProfileQuery = 0;

    public static ulong LcdOverdrivePayload(bool on) => 0x10UL | ((on ? 1UL : 0UL) << 48);

    /// <summary>1 = on; anything else = off.</summary>
    public static bool LcdOverdriveOn(ulong output) => ((output >> 48) & 0xFF) == 1;

    /// <summary>
    /// Overdrive depends on the panel, not the model: the firmware answers 0 or 1 for panels that
    /// have it and 0xFF for the others.
    /// </summary>
    public static bool LcdOverdriveSupported(ulong output) => ((output >> 48) & 0xFF) is 0 or 1;

    // APGeAction: power-off USB charging (function 4).
    public const string ActionClass = "APGeAction";
    public const uint UsbChargingQuery = 0x04;
    public const byte UsbChargingOn = 0x0F;
    public const byte UsbChargingOff = 0x1F;

    /// <summary>The battery levels the firmware offers for stopping USB charging.</summary>
    public static readonly int[] UsbChargingFloors = { 10, 20, 30 };

    public static ulong UsbChargingPayload(bool on, int floor) =>
        UsbChargingQuery |
        ((ulong)(on ? UsbChargingOn : UsbChargingOff) << 8) |
        ((ulong)floor << 16);

    public static ulong PowerModePayload(PowerProfile profile) => ((ulong)profile << 8) | PowerModeOpcode;

    public static ulong FanSpeedPayload(ulong deviceMask, int percent) => deviceMask | ((ulong)percent << 8);
}
