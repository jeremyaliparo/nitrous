using System;
using System.IO;
using System.Runtime.InteropServices;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Read-only NVIDIA Advanced Optimus (Dynamic Display Switching) probe.
///
/// Loads nvcpl.dll and asks for the DDS status (info 0x17). It never reads or writes a display
/// setting, so it cannot change the panel. Where this answers supported the display mode is switched
/// live by the NVIDIA app, and Nitrous hides the reboot-based BIOS MUX: two controls for one panel
/// would disagree, and the BIOS one needs a restart for a change the app makes immediately.
///
/// The same surface is used by PreySense-Nitro (§TGP notes); the exports and the 16-byte status
/// record are its published findings.
/// </summary>
internal static class NvidiaDds
{
    private const uint InfoTypeDdsStatus = 0x17;

    private static readonly object Gate = new();
    private static bool _probed;
    private static bool? _supported;

    /// <summary>
    /// true = DDS confirmed; false = nvcpl answered but has no DDS; null = it could not be asked
    /// (no nvcpl.dll, no exports, or the call failed). Only <c>true</c> hides the BIOS MUX.
    /// </summary>
    public static bool? IsSupported()
    {
        lock (Gate)
        {
            if (_probed) return _supported;
            _probed = true;

            try
            {
                _supported = Probe();
            }
            catch (Exception ex)
            {
                Logger.Warn("NVIDIA DDS: the probe failed.", ex);
                _supported = null;
            }

            Logger.Info($"NVIDIA DDS: {(_supported switch { true => "supported", false => "not supported", _ => "not detected" })}.");
            return _supported;
        }
    }

    private static bool? Probe()
    {
        var path = ResolveNvCplPath();
        if (path is null)
        {
            Logger.Info("NVIDIA DDS: nvcpl.dll was not found.");
            return null;
        }

        var module = LoadLibraryA(path);
        if (module == IntPtr.Zero)
        {
            Logger.Info($"NVIDIA DDS: nvcpl.dll could not be loaded from {path}.");
            return null;
        }

        try
        {
            var initPtr = GetProcAddress(module, "NvCplApiInit");
            var getInfoPtr = GetProcAddress(module, "NvCplApiGetInfo");
            if (initPtr == IntPtr.Zero || getInfoPtr == IntPtr.Zero)
            {
                Logger.Info("NVIDIA DDS: the NvCplApi exports are not present.");
                return null;
            }

            var init = Marshal.GetDelegateForFunctionPointer<NvCplApiInitDelegate>(initPtr);
            var closePtr = GetProcAddress(module, "NvCplApiClose");
            var close = closePtr == IntPtr.Zero
                ? null
                : Marshal.GetDelegateForFunctionPointer<NvCplApiCloseDelegate>(closePtr);
            var getInfo = Marshal.GetDelegateForFunctionPointer<NvCplApiGetInfoDelegate>(getInfoPtr);

            if (init() != 0)
            {
                Logger.Info("NVIDIA DDS: NvCplApiInit was refused.");
                return null;
            }

            try
            {
                ulong size = 16;
                var data = Marshal.AllocHGlobal(16);
                try
                {
                    for (var i = 0; i < 16; i++) Marshal.WriteByte(data, i, 0);

                    if (getInfo(InfoTypeDdsStatus, 0, data, ref size) != 0)
                    {
                        Logger.Info("NVIDIA DDS: NvCplApiGetInfo(0x17) did not answer.");
                        return null;
                    }

                    var status = Marshal.PtrToStructure<NativeDdsStatusInfo>(data);
                    Logger.Info($"NVIDIA DDS: supported={status.IsSupported != 0}, automatic={status.IsAutomatic != 0}, mux={status.MuxState}, kmd={status.KmdResponse}.");
                    return status.IsSupported != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                }
            }
            finally
            {
                try { close?.Invoke(); } catch { }
            }
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private static string? ResolveNvCplPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] paths =
        [
            Path.Combine(programFiles, "NVIDIA Corporation", "NVIDIA App", "NvCpl", "nvcpl.dll"),
            Path.Combine(programFiles, "NVIDIA Corporation", "Control Panel Client", "nvcpl.dll"),
            Path.Combine(Environment.SystemDirectory, "nvcpl.dll"),
        ];

        foreach (var path in paths)
            if (File.Exists(path))
                return path;

        return null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeDdsStatusInfo
    {
        public byte IsSupported;
        public byte IsAutomatic;
        public byte Reserved1;
        public byte Reserved2;
        public uint MuxState;
        public uint KmdResponse;
        public uint SrType;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvCplApiInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvCplApiCloseDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvCplApiGetInfoDelegate(uint infoType, uint param, IntPtr data, ref ulong size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibraryA(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
