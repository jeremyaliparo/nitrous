using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Read-only access to model-specific registers through PawnIO's signed driver
/// (<see href="https://pawnio.eu"/> by namazso).
///
/// Nitrous talks to the driver only through its device IOCTL interface, which PawnIO's licence
/// exception explicitly allows for independent programs; it never links PawnIO code. The module it
/// loads - IntelMSR, from PawnIO.Modules - is LGPL-2.1 and shipped unmodified, with its licence in
/// <c>assets/pawnio/COPYING</c>.
///
/// This exists for one register: MSR 0x610, the package power limit, which Windows exposes no API
/// for. Reading it is harmless; writing is deliberately not implemented here - see
/// <see cref="CpuPowerLimits"/>.
/// </summary>
internal static class PawnIo
{
    private const uint DeviceType = 41394;
    private const uint IoctlLoadBinary = (DeviceType << 16) | (0x821 << 2);
    private const uint IoctlExecute = (DeviceType << 16) | (0x841 << 2);

    /// <summary>PawnIO takes the function name as a zero-padded 32-byte field before the arguments.</summary>
    private const int FunctionNameLength = 32;

    private const string ModuleResource = "Nitrous.assets.pawnio.IntelMSR.bin";
    private const string DevicePath = @"\Device\PawnIO";

    private static readonly object Gate = new();
    private static SafeFileHandle? _device;
    private static bool _ready;
    private static bool _failed;
    private static bool? _installed;

    /// <summary>Whether the PawnIO service is registered. Says nothing about whether it answers.</summary>
    public static bool IsInstalled
    {
        get
        {
            if (_installed is { } known) return known;

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO");
                _installed = key is not null;
            }
            catch
            {
                _installed = false;
            }

            return _installed.Value;
        }
    }

    /// <summary>The register's value, or null when PawnIO is absent or the read was refused.</summary>
    public static ulong? ReadMsr(uint register)
    {
        lock (Gate)
        {
            if (!EnsureReady()) return null;

            var input = new byte[FunctionNameLength + sizeof(ulong)];
            Encoding.ASCII.GetBytes("ioctl_read_msr").CopyTo(input, 0);
            BitConverter.GetBytes((ulong)register).CopyTo(input, FunctionNameLength);

            var output = new byte[sizeof(ulong)];
            if (!DeviceIoControl(_device!, IoctlExecute, input, input.Length,
                    output, output.Length, out var written, IntPtr.Zero))
            {
                Logger.Warn($"PawnIO: reading 0x{register:X} failed (error {Marshal.GetLastWin32Error()}).");
                return null;
            }

            return written >= sizeof(ulong) ? BitConverter.ToUInt64(output, 0) : null;
        }
    }

    /// <summary>
    /// The IntelMSR module refuses to load on anything but an Intel package, and most Nitro models
    /// are AMD. Checking first keeps an installed PawnIO from logging a failed load on those.
    /// </summary>
    private static bool IsIntelCpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("VendorIdentifier") as string == "GenuineIntel";
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureReady()
    {
        if (_ready) return true;
        if (_failed) return false;

        if (!IsInstalled || !IsIntelCpu())
        {
            _failed = true;
            return false;
        }

        try
        {
            var handle = OpenDevice();
            if (handle is null || handle.IsInvalid)
            {
                handle?.Dispose();
                _failed = true;
                Logger.Info("PawnIO: the driver is installed but its device does not answer; CPU registers are unavailable.");
                return false;
            }

            if (!LoadModule(handle))
            {
                handle.Dispose();
                _failed = true;
                return false;
            }

            _device = handle;
            _ready = true;
            Logger.Info("PawnIO: IntelMSR module loaded.");
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            Logger.Warn("PawnIO: could not be opened.", ex);
            return false;
        }
    }

    /// <summary>
    /// Opens <c>\Device\PawnIO</c> the way PawnIOLib does, through NtOpenFile. The Win32
    /// <c>\\?\GLOBALROOT</c> form is not equivalent for a driver that has not registered a DOS name.
    /// </summary>
    private static SafeFileHandle? OpenDevice()
    {
        var name = Native.InitUnicode(DevicePath);
        try
        {
            var attributes = new Native.OBJECT_ATTRIBUTES
            {
                Length = Marshal.SizeOf<Native.OBJECT_ATTRIBUTES>(),
                ObjectName = name
            };

            // GENERIC_READ | GENERIC_WRITE, share read/write/delete - PawnIOLib's own call.
            const uint access = 0xC0000000;
            const uint share = 0x7;
            var status = Native.NtOpenFile(out var rawHandle, access, ref attributes, out _, share, 0);
            if (status < 0 || rawHandle == IntPtr.Zero)
            {
                Logger.Info($"PawnIO: NtOpenFile returned 0x{status:X8}.");
                return null;
            }

            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            Native.FreeUnicode(name);
        }
    }

    private static bool LoadModule(SafeFileHandle device)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ModuleResource);
        if (stream is null)
        {
            Logger.Warn($"PawnIO: the {ModuleResource} resource is missing from the build.");
            return false;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var blob = buffer.ToArray();

        if (!DeviceIoControl(device, IoctlLoadBinary, blob, blob.Length, null, 0, out _, IntPtr.Zero))
        {
            Logger.Warn($"PawnIO: the IntelMSR module would not load (error {Marshal.GetLastWin32Error()}). " +
                        "Intel CPUs only, and the driver must be running.");
            return false;
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[]? input, int inputSize, byte[]? output, int outputSize, out int written, IntPtr overlapped);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_STATUS_BLOCK
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [DllImport("ntdll.dll")]
        public static extern int NtOpenFile(out IntPtr handle, uint access, ref OBJECT_ATTRIBUTES attributes,
            out IO_STATUS_BLOCK status, uint share, uint options);

        public static IntPtr InitUnicode(string text)
        {
            var bytes = Encoding.Unicode.GetBytes(text);
            var buffer = Marshal.AllocHGlobal(bytes.Length + sizeof(char));
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.WriteInt16(buffer, bytes.Length, 0);

            var header = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>());
            Marshal.StructureToPtr(new UNICODE_STRING
            {
                Length = (ushort)bytes.Length,
                MaximumLength = (ushort)(bytes.Length + sizeof(char)),
                Buffer = buffer
            }, header, false);

            return header;
        }

        public static void FreeUnicode(IntPtr header)
        {
            if (header == IntPtr.Zero) return;

            var value = Marshal.PtrToStructure<UNICODE_STRING>(header);
            Marshal.FreeHGlobal(value.Buffer);
            Marshal.FreeHGlobal(header);
        }
    }
}
