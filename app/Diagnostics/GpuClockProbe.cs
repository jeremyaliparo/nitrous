using System;
using System.Linq;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using Nitrous.Helpers;

namespace Nitrous.Diagnostics;

/// <summary>
/// Read-only look at the driver's clock-boost surface: the ranges it reports, whether a clock lock is
/// already held, and how large the boost table is.
///
/// `NvAPI_GPU_SetClockBoostLock` is not a "max MHz" setter: it takes a clock domain, a lock mode and a
/// **voltage**, pinning a point on the V/F curve. So the first question is whether the ranges and the
/// current lock read at all on a laptop GPU - a write test only makes sense afterwards.
/// </summary>
public static class GpuClockProbe
{
    public static int Read(bool allowWake)
    {
        if (GpuPowerState.IsAsleep && !allowWake)
        {
            Logger.Warn("GPU clock probe: the dGPU is powered down; refusing to wake it. Pass --wake to allow it.");
            return 1;
        }

        try
        {
            NVIDIA.Initialize();

            var gpu = PhysicalGPU.GetPhysicalGPUs()
                .OrderByDescending(candidate => candidate.SystemType == SystemType.Laptop)
                .FirstOrDefault();

            if (gpu is null)
            {
                Logger.Warn("GPU clock probe: NVAPI reports no physical GPU.");
                return 2;
            }

            Logger.Info($"GPU clock probe: {gpu.FullName} ({gpu.SystemType})");

            try
            {
                foreach (var range in GPUApi.GetClockBoostRanges(gpu.Handle).ClockBoostRanges)
                {
                    Logger.Info($"GPU clock probe:   {range.ClockType}: " +
                                $"{range.MinimumInkHz / 1000}-{range.MaximumInkHz / 1000} MHz, " +
                                $"max temperature {range.MaximumTemperature} C");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"GPU clock probe: ranges unavailable: {ex.Message}");
            }

            try
            {
                foreach (var held in GPUApi.GetClockBoostLock(gpu.Handle).ClockBoostLocks)
                {
                    Logger.Info($"GPU clock probe:   lock {held.ClockDomain}: {held.LockMode}, " +
                                $"{held.VoltageInMicroV / 1000} mV");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"GPU clock probe: lock state unavailable: {ex.Message}");
            }

            try
            {
                var table = GPUApi.GetClockBoostTable(gpu.Handle).GPUDeltas;
                var top = table.Length == 0 ? 0 : table.Max(delta => delta.FrequencyDeltaInkHz) / 1000;
                Logger.Info($"GPU clock probe:   boost table: {table.Length} points, " +
                            $"largest offset {top:+0;-0;0} MHz");
            }
            catch (Exception ex)
            {
                Logger.Warn($"GPU clock probe: boost table unavailable: {ex.Message}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU clock probe: NVAPI did not start: {ex.Message}");
            return 3;
        }
    }
}
