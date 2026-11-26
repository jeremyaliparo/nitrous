using System;
using System.Linq;
using System.Runtime.InteropServices;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;

namespace Nitrous.Managers;

/// <summary>
/// A cap on the GPU's boost clock: the write is NVML's locked clocks (the call `nvidia-smi -lgc 0,N` makes),
/// and the read-back is NVAPI's clock-boost lock.
///
/// Two different surfaces on purpose, and the split is not obvious:
/// - **Writing** through `NvAPI_GPU_SetClockBoostLock` directly is a different operation - it pins a point
///   on the V/F curve by voltage. G-Helper, which ships this feature, shells out to
///   `nvidia-smi -lgc 0,clock` and so do we, through the same NVML entry point.
/// - **Reading** has no NVML equivalent on this driver (`nvmlDeviceGetGpuLockedClocks` is not exported), but
///   the driver stores the active cap in MHz in the *voltage* field of the first clock-boost lock entry.
///   That is how G-Helper reports it, and it was confirmed here: with `-lgc 0,1500` the entry reads
///   `Manual, 1500 mV`.
///
/// Held per power profile, and written only when "Manage GPU tuning" is on.
/// </summary>
internal static class GpuClockLimit
{
    private const string NvmlDll = "nvml.dll";
    private const uint ClockGraphics = 0;

    /// <summary>Below this a cap is indistinguishable from idle; G-Helper uses 400 and so do we.</summary>
    public const int FloorMhz = 400;

    public readonly record struct Result(bool Ok, string Message);

    /// <summary>Whether Nitrous may write GPU settings at all - the one gate the GPU page exposes.</summary>
    public static bool Managing => SettingsManager.Get("ManageGpuClocks", 0) == 1;

    /// <summary>The cap chosen for a profile, or zero for none.</summary>
    public static int SavedFor(PowerProfile profile) =>
        Math.Max(0, SettingsManager.Get(KeyFor(profile), 0));

    public static void SaveFor(PowerProfile profile, int mhz) =>
        SettingsManager.Save(KeyFor(profile), Math.Max(0, mhz));

    private static string KeyFor(PowerProfile profile) => $"GpuClockLimit_{profile}";

    /// <summary>
    /// The cap the driver currently holds, in MHz, or zero when there is none. Read from the NVAPI lock
    /// entry because NVML exposes no getter for it.
    /// </summary>
    public static int? CurrentCapMhz()
    {
        if (GpuPowerState.IsAsleep) return null;

        try
        {
            NVIDIA.Initialize();

            var gpu = LaptopGpu();
            if (gpu is null) return null;

            var locks = GPUApi.GetClockBoostLock(gpu.Handle).ClockBoostLocks;
            if (locks.Length == 0) return null;

            var first = locks[0];
            var mhz = (int)(first.VoltageInMicroV / 1000);

            return first.LockMode == ClockLockMode.None || mhz <= 0 ? 0 : mhz;
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU clock limit: the current cap could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>The GPU's maximum boost clock, which bounds what may be offered. Null when unavailable.</summary>
    public static int? MaximumMhz()
    {
        if (GpuPowerState.IsAsleep) return null;

        try
        {
            if (NvmlInit() != 0) return null;
            if (NvmlDevice(0, out var device) is not { } handle) return null;

            return NvmlGetMaxClock(handle, ClockGraphics, out var mhz) == 0 ? (int)mhz : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU clock limit: the maximum clock could not be read: {ex.Message}");
            return null;
        }
        finally
        {
            try { NvmlShutdown(); } catch { }
        }
    }

    /// <summary>
    /// Applies a cap, or clears it, then reads the driver back and says what it actually holds. A cap at or
    /// above the maximum boost is treated as "no cap", which is also what G-Helper does.
    /// </summary>
    public static Result Apply(int? mhz)
    {
        if (GpuPowerState.IsAsleep)
            return new Result(false, "The GPU is asleep.");

        try
        {
            if (NvmlInit() != 0)
                return new Result(false, "NVML would not initialise.");

            if (NvmlDevice(0, out var device) is not { } handle)
                return new Result(false, "NVML reports no device.");

            var ceiling = NvmlGetMaxClock(handle, ClockGraphics, out var max) == 0 ? (int)max : 0;
            var target = mhz is { } wanted ? Math.Clamp(wanted, FloorMhz, Math.Max(ceiling, FloorMhz)) : 0;

            if (ceiling > 0 && target >= ceiling) target = 0;

            var status = target > 0
                ? NvmlLock(handle, 0, (uint)target)
                : NvmlReset(handle);

            NvmlShutdown();

            if (status != 0)
            {
                Logger.Warn($"GPU clock limit: NVML returned {status} for {(target > 0 ? target.ToString() : "reset")}.");
                return new Result(false, $"NVML refused the change ({status}).");
            }

            var actual = CurrentCapMhz();

            Logger.Info($"GPU clock limit: requested {(target > 0 ? $"{target} MHz" : "no cap")}, " +
                        $"driver reports {(actual is { } held ? (held > 0 ? $"{held} MHz" : "no cap") : "nothing")}.");

            if (actual is not { } current)
                return new Result(false, "The cap was sent but the driver would not report it back.");

            if (current != target)
                return new Result(false, $"The driver kept {(current > 0 ? $"{current} MHz" : "no cap")} instead.");

            return new Result(true, target > 0
                ? $"Capped at {target} MHz."
                : "No cap; the GPU boosts freely.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU clock limit: the write failed: {ex.Message}");
            return new Result(false, "The driver refused the change (administrator rights are required).");
        }
    }

    /// <summary>
    /// Applies the profile's cap on a mode change, at start-up or after a resume. A profile with no cap
    /// clears the driver's lock: the cap is one global setting, so "this profile has none" can only mean
    /// no lock, or the previous profile's cap would follow the user into every mode.
    /// </summary>
    public static void Reassert(PowerProfile profile)
    {
        if (!Managing) return;

        var saved = SavedFor(profile);
        if (CurrentCapMhz() is not { } current) return;
        if (current == saved) return;

        // Apply(null) is what clears the lock; zero would be clamped up to the floor.
        Apply(saved == 0 ? null : saved);
    }

    private static PhysicalGPU? LaptopGpu() =>
        PhysicalGPU.GetPhysicalGPUs()
            .OrderByDescending(candidate => candidate.SystemType == SystemType.Laptop)
            .FirstOrDefault();

    private static IntPtr? NvmlDevice(uint index, out IntPtr device)
    {
        device = IntPtr.Zero;
        return NvmlGetHandle(index, out device) == 0 && device != IntPtr.Zero ? device : null;
    }

    [DllImport(NvmlDll, EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport(NvmlDll, EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlGetHandle(uint index, out IntPtr device);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMaxClockInfo")]
    private static extern int NvmlGetMaxClock(IntPtr device, uint clockType, out uint mhz);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceSetGpuLockedClocks")]
    private static extern int NvmlLock(IntPtr device, uint minMhz, uint maxMhz);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceResetGpuLockedClocks")]
    private static extern int NvmlReset(IntPtr device);
}
