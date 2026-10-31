using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Nitrous.Managers;

/// <summary>A 3-byte record in Acer's gaming SMBIOS structure (type 0xAC).</summary>
public readonly record struct AcerSmbiosRecord(byte Id, ushort Value);

/// <summary>A 4-byte record in Acer's hotkey structure (type 0xAA).</summary>
public readonly record struct AcerHotkeyRecord(byte Function, byte Flag, ushort Value);

/// <summary>Records of the gaming structure that describe hardware.</summary>
public enum AcerGamingRecord : byte
{
    /// <summary>1, 3 or 4: a keyboard driven by the embedded controller.</summary>
    KeyboardType = 0x08,

    /// <summary>1 single colour, 2 RGB.</summary>
    KeyboardColor = 0x0A,

    /// <summary>1: the firmware shows a custom boot logo from the EFI system partition.</summary>
    CustomBootLogo = 0x0D,

    /// <summary>1: operating-mode controls are supported.</summary>
    OperatingModes = 0x0F,

    /// <summary>1: light bars on the embedded controller, 2: on USB.</summary>
    LightBar = 0x17
}

/// <summary>
/// Acer's OEM SMBIOS structures, readable without elevation.
///
/// Type 0xAC carries the gaming interface version, and that version selects payload encodings
/// (the LED array length changes at 2.86 and 2.91). Type 0xAA lists the hotkey functions the
/// firmware implements, which is how a keyboard feature is detected instead of assumed.
///
/// This is the first thing Nitrous asks a new model, because it answers before any device is
/// opened and it cannot lie about hardware it does not have.
/// </summary>
public sealed record AcerSmbios(
    byte? GamingMajor,
    byte? GamingMinor,
    IReadOnlyList<AcerSmbiosRecord> GamingRecords,
    IReadOnlyList<AcerHotkeyRecord> HotkeyFunctions)
{
    private const uint RawSmbios = 0x52534D42; // 'RSMB'

    public static AcerSmbios Empty { get; } = new(null, null, Array.Empty<AcerSmbiosRecord>(), Array.Empty<AcerHotkeyRecord>());

    /// <summary>E.g. 2.83 on an AN515-57 (bytes 02 53).</summary>
    public double? GamingVersion =>
        GamingMajor is { } major && GamingMinor is { } minor ? major + minor / 100.0 : null;

    /// <summary>
    /// The array <c>SetGamingLED</c> takes: 8 bytes before interface 2.86, 12 up to 2.90,
    /// 16 from 2.91 (the minor byte is decimal, so 2.86 is 0x56).
    /// </summary>
    public int LedArrayLength => (GamingMajor, GamingMinor) switch
    {
        (> 2, not null) or (2, >= 0x5B) => 16,
        (2, >= 0x56) => 12,
        _ => 8
    };

    /// <summary>1: light bars on the embedded controller (2 would be USB ones).</summary>
    public bool HasEcLightBars => Gaming(AcerGamingRecord.LightBar) == 1;

    /// <summary>True when the firmware says it has operating-mode controls at all.</summary>
    public bool OperatingModesListed => Gaming(AcerGamingRecord.OperatingModes) == 1;

    /// <summary>1 single colour, 2 RGB; null when the record is absent.</summary>
    public ushort? KeyboardColor => Gaming(AcerGamingRecord.KeyboardColor);

    /// <summary>A record's value; null when it is missing or reads 0xFF ("not present").</summary>
    public ushort? Gaming(AcerGamingRecord record) =>
        GamingRecords.Where(r => r.Id == (byte)record)
            .Select(r => (ushort?)r.Value)
            .FirstOrDefault() is { } value and not 0xFF
            ? value
            : null;

    /// <summary>
    /// The backlight auto-off hotkey: the last function in 0x81..0x9F whose value is 8, which is
    /// the firmware's slot 3. Null when the firmware has no such function.
    /// </summary>
    public byte? BacklightHotkey => HotkeyFunctions
        .Where(r => r.Function is >= 0x81 and <= 0x9F && r.Value == 0x0008)
        .Select(r => (byte?)r.Function)
        .LastOrDefault();

    public bool HasHotkeyFunction(byte function) =>
        HotkeyFunctions.Any(r => r.Function == function);

    public string Describe()
    {
        var version = GamingVersion is { } v ? v.ToString("0.00") : "unknown";
        return $"gaming interface {version}; " +
               $"operating modes {(OperatingModesListed ? "listed" : "not listed")}; " +
               $"keyboard colour {KeyboardColor?.ToString() ?? "unknown"}; " +
               $"ec light bars {(HasEcLightBars ? "yes" : "no")}; " +
               $"hotkey functions {HotkeyFunctions.Count}";
    }

    /// <summary>Reads the tables Windows was given at boot; empty when they are unavailable.</summary>
    public static AcerSmbios Read()
    {
        try
        {
            var size = GetSystemFirmwareTable(RawSmbios, 0, null, 0);
            if (size == 0 || size > 1024 * 1024) return Empty;

            var buffer = new byte[size];
            if (GetSystemFirmwareTable(RawSmbios, 0, buffer, size) != size) return Empty;

            // RawSMBIOSData: 4 bytes of version, then the table length, then the table.
            if (buffer.Length < 8) return Empty;
            var length = (int)Math.Min(BitConverter.ToUInt32(buffer, 4), (uint)(buffer.Length - 8));
            return Parse(buffer.AsSpan(8, length));
        }
        catch
        {
            return Empty;
        }
    }

    internal static AcerSmbios Parse(ReadOnlySpan<byte> table)
    {
        byte? major = null, minor = null;
        var gaming = new List<AcerSmbiosRecord>();
        var hotkeys = new List<AcerHotkeyRecord>();

        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var type = table[offset];
            var formattedLength = table[offset + 1];
            if (formattedLength < 4 || offset + formattedLength > table.Length) break;

            var formatted = table.Slice(offset, formattedLength);

            if (type == 0xAC && formatted.Length >= 6)
            {
                major = formatted[4];
                minor = formatted[5];
                for (var i = 6; i + 3 <= formatted.Length; i += 3)
                    gaming.Add(new AcerSmbiosRecord(formatted[i], (ushort)(formatted[i + 1] | (formatted[i + 2] << 8))));
            }
            else if (type == 0xAA && formatted.Length >= 18)
            {
                // After a 14-byte header come 4-byte records: function, flag, u16 value.
                for (var i = 14; i + 4 <= formatted.Length; i += 4)
                    hotkeys.Add(new AcerHotkeyRecord(
                        formatted[i], formatted[i + 1], (ushort)(formatted[i + 2] | (formatted[i + 3] << 8))));
            }

            if (type == 0x7F) break; // end-of-table

            // Skip the formatted area, then the double-NUL terminated string set.
            var next = offset + formattedLength;
            while (next + 1 < table.Length && !(table[next] == 0 && table[next + 1] == 0)) next++;
            offset = next + 2;
        }

        return new AcerSmbios(major, minor, gaming, hotkeys);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);
}
