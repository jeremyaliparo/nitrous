using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>Effects the keyboard controller understands, in its own encoding.</summary>
public enum KeyboardEffect : byte
{
    Direct = 1,
    Static = 2,
    Breathing = 4,
    Neon = 5,
    Wave = 7,
    Shifting = 8,
    Zoom = 9,
    Meteor = 10,
    Twinkling = 11
}

/// <summary>A colour as the keyboard takes it.</summary>
public readonly record struct KeyboardColor(byte R, byte G, byte B)
{
    public static KeyboardColor White { get; } = new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary><c>#RRGGBB</c> (the # is optional); white for anything unparseable.</summary>
    public static KeyboardColor FromHex(string? hex)
    {
        var s = (hex ?? string.Empty).TrimStart('#');
        return s.Length == 6 &&
               uint.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier,
                   System.Globalization.CultureInfo.InvariantCulture, out var rgb)
            ? new KeyboardColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : White;
    }
}

/// <summary>
/// The keyboard's lighting controller: an ENEK HID interface with its own 11-byte feature
/// report. This is not the Acer WMI lighting methods, which store values the firmware never
/// applies, and not the embedded controller's <c>0xA0</c> interface that carries the power mode.
///
/// Protocol taken from OpenRGB's AcerNitroHidKeyboardController, then verified on an AN16S-61:
/// four zones, a static colour per zone, or one effect across all of them.
/// </summary>
internal static class AcerKeyboard
{
    public const ushort VendorId = 0x0CF2;
    public const ushort ProductId = 0x5130;
    public const ushort UsagePage = 0xFF5A;
    public const ushort Usage = 0x0001;

    private const byte ReportId = 0xA4;
    private const byte KeyboardDevice = 0x21;
    private const byte AllZones = 0x0F;
    private const int PacketLength = 11;

    public const int ZoneCount = 4;
    public const int MinSpeed = 1;
    public const int MaxSpeed = 9;

    /// <summary>True when this machine exposes the keyboard's lighting interface.</summary>
    public static bool IsAvailable()
    {
        using var device = HidDevice.Open(VendorId, ProductId, UsagePage, Usage);
        return device != null;
    }

    /// <summary>
    /// Sets one colour per zone in Static mode. <paramref name="colors"/> is padded or trimmed to
    /// four zones, so a caller cannot send a mask the firmware does not expect.
    /// </summary>
    public static bool SetZoneColors(IReadOnlyList<KeyboardColor> colors, int brightness)
    {
        using var device = HidDevice.Open(VendorId, ProductId, UsagePage, Usage);
        if (device == null) return false;

        var level = (byte)Math.Clamp(brightness, 0, 100);
        var sent = false;

        for (var zone = 0; zone < ZoneCount; zone++)
        {
            var color = zone < colors.Count ? colors[zone] : KeyboardColor.White;
            var packet = new byte[PacketLength];
            packet[0] = ReportId;
            packet[1] = KeyboardDevice;
            packet[2] = (byte)KeyboardEffect.Static;
            packet[3] = level;
            packet[4] = 0; // speed: unused by Static
            packet[5] = 1; // direction: right
            packet[6] = color.R;
            packet[7] = color.G;
            packet[8] = color.B;
            packet[9] = (byte)(1 << zone);
            packet[10] = 0;

            sent |= device.SetFeature(packet);
        }

        return sent;
    }

    /// <summary>Sets one effect across all four zones.</summary>
    public static bool SetEffect(
        KeyboardEffect effect, int brightness, int speed, bool leftwards, KeyboardColor color)
    {
        using var device = HidDevice.Open(VendorId, ProductId, UsagePage, Usage);
        if (device == null) return false;

        var packet = new byte[PacketLength];
        packet[0] = ReportId;
        packet[1] = KeyboardDevice;
        packet[2] = (byte)effect;
        packet[3] = (byte)Math.Clamp(brightness, 0, 100);
        packet[4] = (byte)Math.Clamp(speed, MinSpeed, MaxSpeed);
        packet[5] = (byte)(leftwards ? 2 : 1);
        packet[6] = color.R;
        packet[7] = color.G;
        packet[8] = color.B;
        packet[9] = AllZones;
        packet[10] = 0;

        return device.SetFeature(packet);
    }

    /// <summary>A matched HID interface opened for feature reports.</summary>
    private sealed class HidDevice : IDisposable
    {
        private readonly SafeFileHandle _handle;

        private HidDevice(SafeFileHandle handle) => _handle = handle;

        public static HidDevice? Open(ushort vendorId, ushort productId, ushort usagePage, ushort usage)
        {
            try
            {
                foreach (var path in Paths())
                {
                    var handle = CreateFileW(
                        path, 0x80000000u | 0x40000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    if (Describe(handle) is not { } info ||
                        info.VendorId != vendorId || info.ProductId != productId ||
                        info.UsagePage != usagePage || info.Usage != usage)
                    {
                        handle.Dispose();
                        continue;
                    }

                    return new HidDevice(handle);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Keyboard: could not open the lighting interface.", ex);
            }

            return null;
        }

        public bool SetFeature(byte[] packet) => HidD_SetFeature(_handle, packet, packet.Length);

        public void Dispose() => _handle.Dispose();

        private static IReadOnlyList<string> Paths()
        {
            HidD_GetHidGuid(out var guid);

            if (CM_Get_Device_Interface_List_SizeW(out var length, ref guid, null, 0) != 0 || length <= 0)
                return Array.Empty<string>();

            var buffer = new char[length];
            if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, length, 0) != 0)
                return Array.Empty<string>();

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private static DeviceInfo? Describe(SafeFileHandle handle)
        {
            var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
            if (!HidD_GetAttributes(handle, ref attributes)) return null;

            if (!HidD_GetPreparsedData(handle, out var preparsed)) return null;

            var caps = new HidCaps();
            try
            {
                if (HidP_GetCaps(preparsed, ref caps) < 0) return null;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }

            return new DeviceInfo(attributes.VendorId, attributes.ProductId, caps.UsagePage, caps.Usage);
        }

        private readonly record struct DeviceInfo(
            ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage);

        [StructLayout(LayoutKind.Sequential)]
        private struct HidAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_SizeW(
            out int length, ref Guid guid, string? deviceId, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_ListW(
            ref Guid guid, string? deviceId, char[] buffer, int length, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr data, ref HidCaps caps);

        [DllImport("hid.dll")]
        private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, int length);
    }
}
