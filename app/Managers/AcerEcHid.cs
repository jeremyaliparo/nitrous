using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nitrous.Enums;

namespace Nitrous.Managers;

/// <summary>What the controller's HID interface offers, as probed once and cached.</summary>
internal sealed record EcHidInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int FeatureLength,
    string? Version,
    ushort? Capability,
    IReadOnlyList<PowerProfile> Modes);

/// <summary>
/// The embedded controller's HID feature-report interface: how Acer's own software, and OpenSense,
/// actually change the operating mode on 2024+ Nitro/Predator models.
///
/// This is deliberately not the Acer gaming WMI class. Writing <c>SetGamingMiscSetting(0x0B)</c>
/// stores a value the firmware echoes back, but on the AN16S-61 it does not move the enforced GPU
/// power limit (measured: the register followed every write while the limit stayed put). The mode
/// only takes effect through this interface.
///
/// The device is ACPI-enumerated (<c>ACPI\1025174B</c>), so it never appears as
/// <c>VID_1025&amp;PID_174B</c>; it is matched on its HID attributes instead.
/// </summary>
internal sealed class AcerEcHid : IDisposable
{
    private const ushort ExpectedVendorId = 0x1025;
    private const ushort ExpectedProductId = 0x174B;
    private const ushort ExpectedUsagePage = 0xFF05;
    private const ushort ExpectedUsage = 0x0001;

    private const byte ReportId = 0xA0;
    private const ushort RequestHeader = 0xA000;
    private const ushort StatusDone = 0xE000;
    private const ushort StatusUnsupported = 0xE001;
    private const ushort CommandStatus = 0x0000;
    private const ushort CommandMode = 0x0001;

    /// <summary>Device settings; device 2 is the keyboard backlight (brightness and auto-off).</summary>
    private const ushort CommandDevice = 0x000A;
    private const byte KeyboardBacklight = 2;
    private const byte FunctionSet = 1;
    private const byte FunctionGet = 2;
    private const int MinimumReportLength = 65;
    private const int Attempts = 5;

    /// <summary>Status query for the operating-mode capability.</summary>
    private const byte StatusModeCapability = 5;

    /// <summary>The mutex Acer's services and OpenSense take around each exchange.</summary>
    private const string MachineLockName = @"Global\ECHIDFeatureVID_1025&PID_174B";

    private static readonly TimeSpan AnswerDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Acer's mode order; a mode's value counts down from the end of the supported list.</summary>
    private static readonly PowerProfile[] ModeOrder =
        { PowerProfile.Quiet, PowerProfile.Balanced, PowerProfile.Performance, PowerProfile.Turbo };

    private readonly SafeFileHandle _handle;
    private readonly Mutex? _lock;
    private readonly byte[] _reply;

    private AcerEcHid(
        SafeFileHandle handle,
        Mutex? machineLock,
        EcHidInterface info,
        IReadOnlyList<PowerProfile>? knownModes)
    {
        _handle = handle;
        _lock = machineLock;
        _reply = new byte[Math.Max(info.FeatureLength, MinimumReportLength)];

        DevicePath = info.Path;
        VendorId = info.VendorId;
        ProductId = info.ProductId;
        UsagePage = info.UsagePage;
        Usage = info.Usage;
        FeatureLength = info.FeatureLength;

        if (knownModes is { Count: > 0 })
        {
            // The capability probe already read the mode list; do not pay for it on every write.
            Modes = knownModes;
        }
        else
        {
            Version = ReadVersion();
            Capability = ReadCapability();
            Modes = ModesFor(Capability);
        }
    }

    public string DevicePath { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int FeatureLength { get; }

    /// <summary>E.g. "0.6"; null when the controller did not answer.</summary>
    public string? Version { get; }

    /// <summary>The firmware's mode-capability value; null when it did not answer.</summary>
    public ushort? Capability { get; }

    /// <summary>The operating modes this firmware supports, in Acer's order.</summary>
    public IReadOnlyList<PowerProfile> Modes { get; }

    /// <summary>Opens the controller's HID interface; null when this model has none.</summary>
    public static AcerEcHid? Open() => OpenInternal(null);

    /// <summary>
    /// Opens without re-reading the mode list, for callers that already probed it. Saves two
    /// exchanges on every mode change.
    /// </summary>
    public static AcerEcHid? OpenWith(IReadOnlyList<PowerProfile> modes) => OpenInternal(modes);

    /// <summary>Reads what the interface offers and closes it; null when this model has none.</summary>
    public static EcHidInfo? Probe()
    {
        using var hid = OpenInternal(null);
        return hid is null
            ? null
            : new EcHidInfo(
                hid.DevicePath, hid.VendorId, hid.ProductId, hid.UsagePage, hid.Usage,
                hid.FeatureLength, hid.Version, hid.Capability, hid.Modes);
    }

    private static AcerEcHid? OpenInternal(IReadOnlyList<PowerProfile>? knownModes)
    {
        foreach (var path in HidInterop.DevicePaths())
        {
            var handle = HidInterop.Open(path);
            if (handle == null) continue;

            var info = HidInterop.Describe(handle, path);
            if (info == null)
            {
                handle.Dispose();
                continue;
            }

            if (info.VendorId != ExpectedVendorId || info.ProductId != ExpectedProductId ||
                info.UsagePage != ExpectedUsagePage || info.Usage != ExpectedUsage)
            {
                handle.Dispose();
                continue;
            }

            return new AcerEcHid(handle, OpenMachineLock(), info, knownModes);
        }

        return null;
    }

    /// <summary>The mode the controller reports; null when it did not answer.</summary>
    public PowerProfile? ReadMode()
    {
        if (Exchange(CommandMode, FunctionGet) is not { } reply) return null;
        return ModeFromValue(Byte(reply.Data, 4));
    }

    /// <summary>
    /// Sets the mode. The value is derived from what the firmware says it supports, so a mode the
    /// laptop does not have is never sent. Returns false when the controller refuses.
    /// </summary>
    public bool WriteMode(PowerProfile mode)
    {
        var value = ValueFor(mode);
        if (value == null) return false;

        return Exchange(CommandMode, FunctionSet, value.Value) is { } reply
               && reply.Status == StatusDone;
    }

    /// <summary>
    /// The keyboard backlight's brightness and auto-off, in seconds (0 = never off).
    /// Null when the controller did not answer.
    /// </summary>
    public (int Brightness, int TimeoutSeconds)? ReadBacklightTimeout()
    {
        if (Exchange(CommandDevice, FunctionGet, KeyboardBacklight) is not { } reply) return null;

        // The reply repeats the device id at data[5], then 1, brightness and timeout.
        if (Byte(reply.Data, 5) != KeyboardBacklight) return null;
        return (Word(reply.Data, 8), Word(reply.Data, 10));
    }

    /// <summary>Sets both, as one write; the controller takes them together.</summary>
    public bool WriteBacklightTimeout(int brightness, int timeoutSeconds)
    {
        var data = new byte[7];
        data[0] = KeyboardBacklight;
        data[1] = 1; // constant, as Acer's software sends it
        data[2] = 0;
        data[3] = (byte)(brightness & 0xFF);
        data[4] = (byte)((brightness >> 8) & 0xFF);
        data[5] = (byte)(timeoutSeconds & 0xFF);
        data[6] = (byte)((timeoutSeconds >> 8) & 0xFF);

        return Exchange(CommandDevice, FunctionSet, data) is { } reply
               && reply.Status == StatusDone;
    }

    /// <summary>
    /// Changes only the auto-off, keeping the brightness the controller reports. Reading first
    /// matters: the write carries both, so guessing a brightness would change the backlight level.
    /// </summary>
    public bool SetBacklightTimeout(int timeoutSeconds) =>
        ReadBacklightTimeout() is { } current &&
        WriteBacklightTimeout(current.Brightness, timeoutSeconds);

    private string? ReadVersion()
    {
        if (Exchange(CommandStatus, 0) is not { } reply) return null;

        // Older firmware answers at 4 and 5 and leaves FFFF at 6; newer repeats the type at 4.
        var data = reply.Data;
        var word6 = Word(data, 6);
        if (word6 == 0xFFFF) return $"{Byte(data, 4)}.{Byte(data, 5)}";
        return Byte(data, 4) == 0 ? $"{Byte(data, 6)}.{Byte(data, 7)}" : null;
    }

    private ushort? ReadCapability()
    {
        if (Exchange(CommandStatus, StatusModeCapability) is not { } reply) return null;

        // From version 0.6 the answer repeats the type at 4 and carries the value two bytes later.
        var echoesType = Version != null && IsVersionAtLeast(Version, 0, 6);
        var data = reply.Data;
        if (!echoesType) return Word(data, 4);
        return Byte(data, 4) == StatusModeCapability ? Word(data, 6) : null;
    }

    private static bool IsVersionAtLeast(string version, int first, int second)
    {
        var parts = version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var f) || !int.TryParse(parts[1], out var s))
            return false;
        return f > first || (f == first && s >= second);
    }

    private static IReadOnlyList<PowerProfile> ModesFor(ushort? capability) => capability switch
    {
        // Acer's capability value is how many modes there are; Eco only exists in the five-mode list.
        1 or 2 => new[] { PowerProfile.Balanced },
        3 => new[] { PowerProfile.Quiet, PowerProfile.Balanced, PowerProfile.Performance },
        4 => new[] { PowerProfile.Quiet, PowerProfile.Balanced, PowerProfile.Performance, PowerProfile.Turbo },
        5 => new[] { PowerProfile.Quiet, PowerProfile.Balanced, PowerProfile.Performance, PowerProfile.Turbo },
        _ => Array.Empty<PowerProfile>()
    };

    private byte? ValueFor(PowerProfile mode)
    {
        var index = Array.IndexOf(Modes.ToArray(), mode);
        return index < 0 ? null : (byte)(Modes.Count - 1 - index);
    }

    private PowerProfile? ModeFromValue(byte value) =>
        value < Modes.Count ? Modes[Modes.Count - 1 - value] : null;

    /// <summary>
    /// One request/answer exchange. The controller answers a feature report a few milliseconds
    /// after it is written, so the read is retried while the answer is neither done nor refused.
    /// </summary>
    private Reply? Exchange(ushort command, byte function, params byte[] data)
    {
        var request = BuildRequest(command, function, data);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(RetryDelay);

            var locked = false;
            try
            {
                locked = _lock?.WaitOne(LockTimeout) ?? false;
                if (!HidInterop.SetFeature(_handle, request)) continue;

                Thread.Sleep(AnswerDelay);
                Array.Clear(_reply);
                _reply[0] = ReportId;
                if (!HidInterop.GetFeature(_handle, _reply)) continue;

                if (Decode(_reply) is not { } reply) continue;
                if (reply.Command != command) continue;
                if (reply.Status != StatusDone && reply.Status != StatusUnsupported) continue;
                return reply;
            }
            catch (AbandonedMutexException)
            {
                locked = true; // its owner died mid-exchange; the lock is ours now
            }
            finally
            {
                if (locked) _lock!.ReleaseMutex();
            }
        }

        return null;
    }

    private static byte[] BuildRequest(ushort command, byte function, byte[] data)
    {
        var report = new byte[MinimumReportLength];
        report[0] = ReportId;
        report[1] = (byte)(RequestHeader & 0xFF);
        report[2] = (byte)(RequestHeader >> 8);
        report[3] = (byte)(command & 0xFF);
        report[4] = (byte)(command >> 8);
        report[5] = function;
        if (data.Length > 0)
            Array.Copy(data, 0, report, 6, Math.Min(data.Length, MinimumReportLength - 6));
        return report;
    }

    private static Reply? Decode(byte[] report)
    {
        if (report.Length < 6 || report[0] != ReportId) return null;

        var data = new byte[report.Length - 1];
        Array.Copy(report, 1, data, 0, data.Length);
        return new Reply(Word(data, 0), Word(data, 2), data);
    }

    private static byte Byte(byte[] data, int offset) => offset < data.Length ? data[offset] : (byte)0;

    private static ushort Word(byte[] data, int offset) =>
        offset + 1 < data.Length ? (ushort)(data[offset] | (data[offset + 1] << 8)) : (ushort)0;

    private static Mutex? OpenMachineLock()
    {
        try
        {
            return Mutex.TryOpenExisting(MachineLockName, out var existing)
                ? existing
                : new Mutex(false, MachineLockName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        _lock?.Dispose();
    }

    private readonly record struct Reply(ushort Status, ushort Command, byte[] Data);

    private sealed record EcHidInterface(
        string Path, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage, int FeatureLength);

    /// <summary>Windows HID access: configuration manager enumeration and the HID class driver.</summary>
    private static class HidInterop
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareReadWrite = 3;
        private const uint OpenExisting = 3;
        private const int CrSuccess = 0;
        private const int CrBufferSmall = 26;

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

        [DllImport("hid.dll")]
        private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, int length);

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

        public static IReadOnlyList<string> DevicePaths()
        {
            var guid = HidGuid();
            var length = 0;
            if (CM_Get_Device_Interface_List_SizeW(out length, ref guid, null, 0) != CrSuccess || length <= 0)
                return Array.Empty<string>();

            var buffer = new char[length];
            var result = CM_Get_Device_Interface_ListW(ref guid, null, buffer, length, 0);
            if (result == CrBufferSmall)
                return Array.Empty<string>(); // a device arrived mid-call; the next attempt will see it
            if (result != CrSuccess)
                return Array.Empty<string>();

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private static Guid HidGuid()
        {
            HidD_GetHidGuid(out var guid);
            return guid;
        }

        public static SafeFileHandle? Open(string path)
        {
            var handle = CreateFileW(
                path, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return handle;
        }

        public static EcHidInterface? Describe(SafeFileHandle handle, string path)
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

            return new EcHidInterface(
                path, attributes.VendorId, attributes.ProductId,
                caps.UsagePage, caps.Usage, caps.FeatureReportByteLength);
        }

        public static bool SetFeature(SafeFileHandle handle, byte[] report) =>
            HidD_SetFeature(handle, report, report.Length);

        public static bool GetFeature(SafeFileHandle handle, byte[] report) =>
            HidD_GetFeature(handle, report, report.Length);
    }
}
