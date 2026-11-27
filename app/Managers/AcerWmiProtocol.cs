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

    // GetGamingMiscSetting(0x09) is the GPU MUX mask (1 hybrid, 2 discrete, 4 automatic) and
    // (0x02) the stored mode (1/2/3). SetGamingMiscSetting(0x02 | mode << 8) stores it for the
    // next boot; status 0 or 8 both mean stored. Same contract as OpenSense and Nitro-Hub.
    public const uint GpuMuxSetting = 0x02;
    public const uint GpuMuxSupportSetting = 0x09;

    public static ulong GpuMuxPayload(byte mode) => GpuMuxSetting | ((ulong)mode << 8);

    // CoolBoost, where APGeAction function 7 is it (marker 1): sub-function 2 reads the state from
    // byte 1, and a set carries it in byte 2.
    public const uint CoolBoostRead = 0x0207;

    public static ulong CoolBoostWrite(bool on) => 0x07UL | ((on ? 1UL : 0UL) << 16);
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

    // Keyboard lighting on the models whose embedded controller owns the LEDs and exposes them
    // through WMI alone - the AN515/AN517 family, with no ENEK HID interface (§23.196). The record
    // is the 16-byte NitroSense layout; a zone colour is a u64 with the zone's bit below R, G, B.
    public const string GetGamingKBBacklight = "GetGamingKBBacklight";
    public const string SetGamingKBBacklight = "SetGamingKBBacklight";
    public const string GetGamingRgbKb = "GetGamingRgbKb";
    public const string SetGamingRgbKb = "SetGamingRgbKb";
    public const string SetGamingLED = "SetGamingLED";

    /// <summary>Zones the WMI interface holds; the SMBIOS keyboard type names the same four.</summary>
    public const int KeyboardZones = 4;

    /// <summary><c>SetGamingRgbKb</c>: zone 1-4 in bits 0-3, then R, G and B in bytes 1-3.</summary>
    public static ulong KeyboardZoneColor(int zone, byte r, byte g, byte b) =>
        (1UL << (zone - 1)) | ((ulong)r << 8) | ((ulong)g << 16) | ((ulong)b << 24);

    /// <summary><c>GetGamingRgbKb</c> takes the zone's bit, as the setter does.</summary>
    public static uint KeyboardZoneQuery(int zone) => 1u << (zone - 1);

    /// <summary>
    /// <c>SetGamingLED</c>: the keyboard's group (<c>0x08</c>) with every zone lit. Bits 40-43 are
    /// the four zone switches - the answer for all four is <c>0xF0000000000</c>.
    /// </summary>
    public const ulong KeyboardAllZonesOn = 0x08 | (0x0FUL << 40);

    /// <summary>The colour in a <c>GetGamingRgbKb</c> answer: R, G and B in bytes 1-3.</summary>
    public static (byte R, byte G, byte B) KeyboardZoneAnswer(ulong output) =>
        ((byte)(output >> 8), (byte)(output >> 16), (byte)(output >> 24));

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
