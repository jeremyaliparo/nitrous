using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Read-only access to Intel registers through PawnIO's signed driver
/// (<see href="https://pawnio.eu"/> by namazso).
///
/// Nitrous talks to the driver only through its device IOCTL interface, which PawnIO's licence
/// exception explicitly allows for independent programs; it never links PawnIO code. The modules it
/// loads - IntelMSR and IntelMCHBAR, from PawnIO.Modules - are LGPL-2.1 and shipped unmodified, with
/// their licence in <c>assets/pawnio/COPYING</c>.
///
/// One device handle holds one loaded module, so each module gets its own handle and they are opened
/// only when something actually reads through them.
/// </summary>
internal static class PawnIo
{
    private const uint DeviceType = 41394;
    private const uint IoctlLoadBinary = (DeviceType << 16) | (0x821 << 2);
    private const uint IoctlExecute = (DeviceType << 16) | (0x841 << 2);

    /// <summary>PawnIO takes the function name as a zero-padded 32-byte field before the arguments.</summary>
    private const int FunctionNameLength = 32;

    private const string DevicePath = @"\Device\PawnIO";

    private static readonly object Gate = new();
    private static readonly Channel Msr = new("Nitrous.assets.pawnio.IntelMSR.bin", "IntelMSR");
    private static readonly Channel Mchbar = new("Nitrous.assets.pawnio.IntelMCHBAR.bin", "IntelMCHBAR");

    private static bool? _installed;
    private static bool? _isIntel;

    /// <summary>
    /// Whether this is an Intel package. The IntelMSR and IntelMCHBAR modules only load there, so
    /// anything gated on them is gated on this first.
    /// </summary>
    public static bool HasIntelCpu => IsIntelCpu();

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

    /// <summary>MSR_PKG_POWER_LIMIT and anything else the IntelMSR module allows.</summary>
    public static ulong? ReadMsr(uint register) => Execute(Msr, "ioctl_read_msr", 1, register);

    /// <summary>Writes an MSR. Only <see cref="CpuPowerCap"/> calls this.</summary>
    public static bool WriteMsr(uint register, ulong value) =>
        Execute(Msr, "ioctl_write_msr", 0, register, value) is not null;

    /// <summary>A 64-bit MCHBAR word, such as the firmware's copy of the power limit at 0x59A0.</summary>
    public static ulong? ReadMchbar(ulong offset) => Execute(Mchbar, "ioctl_read_qword", 1, offset);

    private static ulong? Execute(Channel channel, string function, int outputCells, params ulong[] arguments)
    {
        lock (Gate)
        {
            if (!IsInstalled || !IsIntelCpu()) return null;
            if (!channel.EnsureOpen()) return null;

            var input = new byte[FunctionNameLength + sizeof(ulong) * arguments.Length];
            Encoding.ASCII.GetBytes(function).CopyTo(input, 0);
            for (var i = 0; i < arguments.Length; i++)
                BitConverter.GetBytes(arguments[i]).CopyTo(input, FunctionNameLength + sizeof(ulong) * i);

            var output = new byte[sizeof(ulong) * outputCells];
            if (!DeviceIoControl(channel.Device!, IoctlExecute, input, input.Length,
                    output.Length == 0 ? null : output, output.Length, out var written, IntPtr.Zero))
            {
                // A stopped or restarted driver leaves this handle stale. Drop it so the next read
                // opens a fresh one instead of failing for the rest of the session.
                var error = Marshal.GetLastWin32Error();
                channel.Close();
                Logger.Warn($"PawnIO: {function} failed (error {error}).");
                return null;
            }

            if (outputCells == 0) return 0;
            return written >= output.Length ? BitConverter.ToUInt64(output, 0) : null;
        }
    }

    /// <summary>
    /// The IntelMSR and IntelMCHBAR modules refuse to load on anything but an Intel package, and most
    /// Nitro models are AMD. Checking first keeps an installed PawnIO from logging a failed load.
    /// </summary>
    private static bool IsIntelCpu()
    {
        if (_isIntel is { } known) return known;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _isIntel = key?.GetValue("VendorIdentifier") as string == "GenuineIntel";
        }
        catch
        {
            _isIntel = false;
        }

        return _isIntel.Value;
    }

    /// <summary>
    /// Opens <c>\Device\PawnIO</c> the way PawnIOLib does, through NtOpenFile. The Win32
    /// <c>\\?\GLOBALROOT</c> form also works against a healthy install; NtOpenFile is used because it
    /// does not depend on the driver registering a DOS name.
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

    private static bool LoadModule(SafeFileHandle device, string resource, string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null)
        {
            Logger.Warn($"PawnIO: the {resource} resource is missing from the build.");
            return false;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var blob = buffer.ToArray();

        if (!DeviceIoControl(device, IoctlLoadBinary, blob, blob.Length, null, 0, out _, IntPtr.Zero))
        {
            Logger.Warn($"PawnIO: the {name} module would not load (error {Marshal.GetLastWin32Error()}). " +
                        "Intel CPUs only, the driver must be running, and the chipset must expose MCHBAR.");
            return false;
        }

        return true;
    }

    /// <summary>One module on one device handle, opened the first time something reads through it.</summary>
    private sealed class Channel(string resource, string name)
    {
        public SafeFileHandle? Device { get; private set; }

        public bool EnsureOpen()
        {
            if (Device is not null) return true;

            var handle = OpenDevice();
            if (handle is null) return false;

            if (!LoadModule(handle, resource, name))
            {
                handle.Dispose();
                return false;
            }

            Device = handle;
            Logger.Info($"PawnIO: {name} module loaded.");
            return true;
        }

        public void Close()
        {
            Device?.Dispose();
            Device = null;
        }
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
