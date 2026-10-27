using System;
using System.Runtime.InteropServices;
using System.Text;
using Nitrous.Diagnostics;

namespace Nitrous.Helpers;

/// <summary>
/// Reads the dGPU's Windows PnP power state without touching the GPU.
///
/// <para>
/// NVML and NVAPI are not usable as sleep detectors: a query against a powered-down
/// device fails, but re-initializing either one powers the device back up. The
/// power manager already tracks the device state, and
/// <c>DEVPKEY_Device_PowerData</c> exposes <c>CM_POWER_DATA.PD_MostRecentPowerState</c>
/// as a cached value. Reading it does not move the device out of D3, so it is safe to
/// poll at any rate.
/// </para>
/// </summary>
public static class GpuPowerState
{
    public enum DevicePower
    {
        /// <summary>The state could not be read. Callers should not assume the GPU is asleep.</summary>
        Unknown = 0,
        D0 = 1,
        D1 = 2,
        D2 = 3,
        D3 = 4
    }

    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly Guid PowerDataGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint PowerDataPropertyId = 32; // DEVPKEY_Device_PowerData
    private const uint DigcfPresent = 0x02;

    private static readonly object Gate = new();
    private static uint _devInst;
    private static bool _resolved;
    private static bool _failureLogged;

    /// <summary>
    /// True only when Windows reports D3 (rail off). Unknown is treated as awake so a
    /// machine that does not expose the property keeps the previous behaviour.
    /// </summary>
    public static bool IsAsleep
    {
        get
        {
            lock (Gate)
                return QueryLocked() == DevicePower.D3;
        }
    }

    /// <summary>True when the device is known to be powered (D0).</summary>
    public static bool IsAwake
    {
        get
        {
            lock (Gate)
                return QueryLocked() == DevicePower.D0;
        }
    }

    public static DevicePower Query()
    {
        lock (Gate)
            return QueryLocked();
    }

    /// <summary>Drops the cached device node, e.g. after a driver restart.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _devInst = 0;
            _resolved = false;
        }
    }

    private static DevicePower QueryLocked()
    {
        if (!_resolved && !ResolveLocked())
            return DevicePower.Unknown;

        if (!TryReadPowerState(_devInst, out var state))
        {
            // The node may have been re-enumerated (driver restart or device reset).
            // Resolve once more before giving up.
            _resolved = false;
            if (!ResolveLocked() || !TryReadPowerState(_devInst, out state))
                return DevicePower.Unknown;
        }

        return state;
    }

    private static bool ResolveLocked()
    {
        _devInst = 0;

        var instanceId = FindNvidiaDisplayInstanceId();
        if (instanceId == null)
        {
            LogOnce("the NVIDIA display device was not found");
            return false;
        }

        if (CmLocateDevNode(out var devInst, instanceId, 0) != CrSuccess)
        {
            LogOnce("the NVIDIA display device node could not be located");
            return false;
        }

        _devInst = devInst;
        _resolved = true;
        _failureLogged = false;
        return true;
    }

    private static void LogOnce(string reason)
    {
        if (_failureLogged) return;

        _failureLogged = true;
        Logger.Warn($"GPU power state: {reason}; assuming the dGPU is awake.");
    }

    private static string? FindNvidiaDisplayInstanceId()
    {
        var classGuid = DisplayClassGuid;
        var set = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        if (set == InvalidHandleValue) return null;

        try
        {
            var data = new SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };

            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var buffer = new StringBuilder(256);
                if (!SetupDiGetDeviceInstanceId(set, ref data, buffer, (uint)buffer.Capacity, out _))
                    continue;

                var id = buffer.ToString();
                if (id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool TryReadPowerState(uint devInst, out DevicePower state)
    {
        state = DevicePower.Unknown;

        var key = new DevPropKey { fmtid = PowerDataGuid, pid = PowerDataPropertyId };
        uint size = 0;

        var result = CmGetDevNodeProperty(devInst, ref key, out _, null, ref size, 0);
        if (result != CrBufferSmall || size < 8)
            return false;

        var buffer = new byte[size];
        result = CmGetDevNodeProperty(devInst, ref key, out _, buffer, ref size, 0);
        if (result != CrSuccess)
            return false;

        // CM_POWER_DATA starts with PD_Size, then PD_MostRecentPowerState, both DWORDs.
        var value = BitConverter.ToUInt32(buffer, 4);
        if (value is < (uint)DevicePower.D0 or > (uint)DevicePower.D3)
            return false;

        state = (DevicePower)value;
        return true;
    }

    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, StringBuilder deviceInstanceId,
        uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    private static int CmLocateDevNode(out uint devInst, string deviceId, uint flags) =>
        CM_Locate_DevNodeW(out devInst, deviceId, flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint devInst, ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer,
        ref uint propertyBufferSize, uint flags);

    private static int CmGetDevNodeProperty(
        uint devInst, ref DevPropKey key, out uint propertyType, byte[]? buffer,
        ref uint bufferSize, uint flags) =>
        CM_Get_DevNode_PropertyW(devInst, ref key, out propertyType, buffer, ref bufferSize, flags);
}
