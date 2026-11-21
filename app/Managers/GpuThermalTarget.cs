using System;
using System.Linq;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using Nitrous.Diagnostics;
using Nitrous.Helpers;

namespace Nitrous.Managers;

/// <summary>
/// The GPU's thermal (throttle) target, through NVAPI's thermal policy.
///
/// This is a single global value, not a per-profile one: measured on an RTX 5060 Laptop, the target read
/// 87 °C in Quiet, Balanced and Performance, and a written value survived mode switches. So there is
/// nothing mode-shaped to compose with, unlike the CPU power limits.
///
/// Lower-only by construction: the offered range stops at the driver's own default, and the driver's
/// advertised minimum. NVAPI setters need administrator; without it the call returns
/// INVALID_USER_PRIVILEGE, which is a different answer from "unsupported" and is reported as such.
/// </summary>
internal static class GpuThermalTarget
{
    private const string Key = "GpuThermalTargetC";

    public readonly record struct Limits(int Min, int Default, int Current);

    public readonly record struct Result(bool Ok, string Message);

    /// <summary>The chosen target, or zero when the driver's default is in use.</summary>
    public static int Saved => Math.Max(0, SettingsManager.Get(Key, 0));

    /// <summary>The policy's range and current value, or null when the GPU is asleep or exposes none.</summary>
    public static Limits? Read()
    {
        if (GpuPowerState.IsAsleep) return null;

        try
        {
            NVIDIA.Initialize();

            var gpu = LaptopGpu();
            if (gpu is null) return null;

            var info = GPUApi.GetThermalPoliciesInfo(gpu.Handle).ThermalPoliciesInfoEntries.ToList();
            if (info.Count == 0) return null;

            var entry = info[0];
            var current = GPUApi.GetThermalPoliciesStatus(gpu.Handle).ThermalPoliciesStatusEntries
                .FirstOrDefault(seen => seen.Controller == entry.Controller);

            return new Limits(
                entry.MinimumTemperature,
                entry.DefaultTemperature,
                current.TargetTemperature);
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU thermal: the policy could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes the target, verifies it, and puts the previous value back when it does not stick. A null
    /// argument means "the driver's default".
    /// </summary>
    public static Result Apply(int? celsius)
    {
        if (Read() is not { } limits)
            return new Result(false, "This GPU does not expose a thermal policy.");

        var target = celsius is { } chosen
            ? Math.Clamp(chosen, limits.Min, limits.Default)
            : limits.Default;

        try
        {
            Write(target);
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU thermal: the write failed: {ex.Message}");
            return new Result(false, "The driver refused the change (administrator rights are required).");
        }

        if (Read() is not { } after || after.Current != target)
        {
            try { Write(limits.Current); } catch { }
            return new Result(false, "The target did not stick; the previous value is back.");
        }

        SettingsManager.Save(Key, target == limits.Default ? 0 : target);
        Logger.Info($"GPU thermal: target {target} C (range {limits.Min}-{limits.Default} C).");

        return new Result(true, target == limits.Default
            ? $"Following the driver default ({limits.Default} C)."
            : $"Target set to {target} C.");
    }

    /// <summary>Re-applies the saved target after a resume or a start-up. Does nothing when unset.</summary>
    public static void Reassert()
    {
        var saved = Saved;
        if (saved == 0) return;

        if (Read() is not { } limits) return;
        if (limits.Current == Math.Min(saved, limits.Default)) return;

        Apply(saved);
    }

    private static PhysicalGPU? LaptopGpu() =>
        PhysicalGPU.GetPhysicalGPUs()
            .OrderByDescending(candidate => candidate.SystemType == SystemType.Laptop)
            .FirstOrDefault();

    private static void Write(int celsius)
    {
        var gpu = LaptopGpu() ?? throw new InvalidOperationException("no physical GPU");
        var info = GPUApi.GetThermalPoliciesInfo(gpu.Handle).ThermalPoliciesInfoEntries.ToList();
        if (info.Count == 0) throw new InvalidOperationException("no thermal policy");

        var status = new PrivateThermalPoliciesStatusV2(
            new[] { new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(info[0].Controller, celsius) });

        GPUApi.SetThermalPoliciesStatus(gpu.Handle, status);
    }
}
