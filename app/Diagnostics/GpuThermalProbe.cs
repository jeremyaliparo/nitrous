using System;
using System.Linq;
using System.Threading;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using Nitrous.Helpers;
using Nitrous.Managers;

namespace Nitrous.Diagnostics;

/// <summary>
/// Read-only look at the GPU's thermal policy, plus one reversible write of its target.
///
/// This is the NVAPI surface, not NVML: `GetThermalPoliciesInfo` / `GetStatus` /
/// `SetThermalPoliciesStatus`. The power side was explored and abandoned - NVML refuses the write and
/// NVAPI exposes no client power policy on the hardware tested - so none of that is kept here.
///
/// Setters require administrator; without it NVAPI answers `INVALID_USER_PRIVILEGE`, which is a
/// different answer from "unsupported" and worth keeping distinguishable in the log.
/// </summary>
public static class GpuThermalProbe
{
    public static int Read(bool allowWake)
    {
        if (!TryOpen(allowWake, out var gpu))
            return 1;

        try
        {
            var info = GPUApi.GetThermalPoliciesInfo(gpu.Handle).ThermalPoliciesInfoEntries.ToList();
            if (info.Count == 0)
            {
                Logger.Warn("GPU thermal probe: this driver exposes no thermal policy.");
                return 2;
            }

            Logger.Info($"GPU thermal probe: {gpu.FullName} ({gpu.SystemType}), mode {CurrentMode()}");

            foreach (var entry in info)
            {
                var target = TargetOf(gpu, entry.Controller);
                Logger.Info($"GPU thermal probe:   {entry.Controller}: range {entry.MinimumTemperature}-" +
                            $"{entry.MaximumTemperature} C, default {entry.DefaultTemperature} C, target {target} C");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU thermal probe: reading the policy failed: {ex.Message}");
            return 3;
        }
    }

    /// <summary>
    /// One reversible write: lower the target, watch whether it holds, then put the driver's own value
    /// back. Lower-only - a lower target throttles sooner, which is the direction this would be used in.
    /// </summary>
    public static int Write(int celsius, int holdSeconds, bool allowWake, bool keep = false)
    {
        if (!TryOpen(allowWake, out var gpu))
            return 1;

        try
        {
            var info = GPUApi.GetThermalPoliciesInfo(gpu.Handle).ThermalPoliciesInfoEntries.ToList();
            if (info.Count == 0)
            {
                Logger.Warn("GPU thermal probe: this driver exposes no thermal policy.");
                return 2;
            }

            var entry = info[0];
            var original = TargetOf(gpu, entry.Controller) ?? entry.DefaultTemperature;

            Logger.Info($"GPU thermal probe: before - target {original} C, range {entry.MinimumTemperature}-" +
                        $"{entry.MaximumTemperature} C, mode {CurrentMode()}");

            if (celsius >= original || celsius < entry.MinimumTemperature)
            {
                Logger.Warn($"GPU thermal probe: {celsius} C is not a lowering inside " +
                            $"{entry.MinimumTemperature}-{original} C, so nothing was written.");
                return 4;
            }

            try
            {
                Write(gpu, entry.Controller, celsius);

                var after = TargetOf(gpu, entry.Controller);
                Logger.Info($"GPU thermal probe: wrote {celsius} C, target now {after} C " +
                            $"({(after == celsius ? "accepted" : "not taken")})");

                var held = 0;
                var samples = 0;
                var deadline = Environment.TickCount64 + Math.Max(1, holdSeconds) * 1000L;

                while (Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(2000);

                    var now = TargetOf(gpu, entry.Controller);
                    samples++;
                    if (now == celsius) held++;

                    Logger.Info($"GPU thermal probe: holding - mode {CurrentMode()}, target {now} C " +
                                $"({(now == celsius ? "held" : "moved")})");
                }

                Logger.Info($"GPU thermal probe: held {held}/{samples} samples at {celsius} C" +
                            (samples > 0 && held == samples
                                ? " - the driver keeps it."
                                : " - something reset it."));
            }
            finally
            {
                if (keep)
                {
                    Logger.Info($"GPU thermal probe: left at {celsius} C (--keep); restore with --restore.");
                }
                else
                {
                    Write(gpu, entry.Controller, original);
                    var back = TargetOf(gpu, entry.Controller);
                    Logger.Info($"GPU thermal probe: reverted to {original} C, target now {back} C " +
                                $"({(back == original ? "ok" : "MISMATCH")})");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU thermal probe: the write failed: {ex.Message}");
            return 5;
        }
    }

    /// <summary>Puts the driver's own default target back, whatever the current value is.</summary>
    public static int Restore(bool allowWake)
    {
        if (!TryOpen(allowWake, out var gpu))
            return 1;

        try
        {
            var info = GPUApi.GetThermalPoliciesInfo(gpu.Handle).ThermalPoliciesInfoEntries.ToList();
            if (info.Count == 0) return 2;

            var entry = info[0];
            Write(gpu, entry.Controller, entry.DefaultTemperature);

            var back = TargetOf(gpu, entry.Controller);
            Logger.Info($"GPU thermal probe: restored to the driver default {entry.DefaultTemperature} C, " +
                        $"target now {back} C ({(back == entry.DefaultTemperature ? "ok" : "MISMATCH")})");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU thermal probe: restore failed: {ex.Message}");
            return 5;
        }
    }

    private static bool TryOpen(bool allowWake, out PhysicalGPU gpu)
    {
        gpu = null!;

        if (GpuPowerState.IsAsleep)
        {
            if (!allowWake)
            {
                Logger.Warn("GPU thermal probe: the dGPU is powered down; refusing to wake it. Pass --wake to allow it.");
                return false;
            }

            Logger.Info("GPU thermal probe: the dGPU is asleep and --wake was passed; waking it for the run.");
        }

        try
        {
            NVIDIA.Initialize();

            gpu = PhysicalGPU.GetPhysicalGPUs()
                .OrderByDescending(candidate => candidate.SystemType == SystemType.Laptop)
                .FirstOrDefault()!;

            if (gpu is null)
            {
                Logger.Warn("GPU thermal probe: NVAPI reports no physical GPU.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU thermal probe: NVAPI did not start: {ex.Message}");
            return false;
        }
    }

    private static int? TargetOf(PhysicalGPU gpu, ThermalController controller)
    {
        foreach (var entry in GPUApi.GetThermalPoliciesStatus(gpu.Handle).ThermalPoliciesStatusEntries)
        {
            if (entry.Controller == controller) return entry.TargetTemperature;
        }

        return null;
    }

    private static void Write(PhysicalGPU gpu, ThermalController controller, int celsius)
    {
        var status = new PrivateThermalPoliciesStatusV2(
            new[] { new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(controller, celsius) });

        GPUApi.SetThermalPoliciesStatus(gpu.Handle, status);
    }

    private static string CurrentMode() =>
        AcerWmiManager.GetEcPowerMode()?.ToString()
        ?? ((Nitrous.Enums.PowerProfile)SettingsManager.Get("LastPowerMode", (int)Nitrous.Enums.PowerProfile.Balanced))
            .ToString();
}
