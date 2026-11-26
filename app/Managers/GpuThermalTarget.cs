using System;
using System.Linq;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;

namespace Nitrous.Managers;

/// <summary>
/// The GPU's thermal (throttle) target, through NVAPI's thermal policy, held per power profile.
///
/// The driver's policy has a single global target - measured identically in Quiet, Balanced and
/// Performance - and a mode switch does not touch it. So per-profile behaviour is ours to provide: a value
/// is stored per profile and written when that profile becomes active, which is why this sits behind the
/// same opt-in as the clock offsets rather than applying itself unasked.
///
/// Lower-only by construction: the offered range stops at the driver's own default and its advertised
/// minimum. NVAPI setters need administrator; without it the call returns INVALID_USER_PRIVILEGE, which is
/// a different answer from "unsupported" and is reported as such.
/// </summary>
internal static class GpuThermalTarget
{

    public readonly record struct Limits(int Min, int Default, int Current);

    public readonly record struct Result(bool Ok, string Message);

    /// <summary>
    /// Whether the user has opted in to GPU writes at all. The same switch that governs clock offsets
    /// governs this: one "Manage GPU tuning" gate rather than two, so a machine cannot end up with
    /// offsets allowed and thermal writes not, or the reverse.
    /// </summary>
    public static bool Managing => SettingsManager.Get("ManageGpuClocks", 0) == 1;

    /// <summary>The target chosen for a profile, or zero when that profile follows the driver default.</summary>
    public static int SavedFor(PowerProfile profile) =>
        Math.Max(0, SettingsManager.Get(KeyFor(profile), 0));

    public static void SaveFor(PowerProfile profile, int celsius) =>
        SettingsManager.Save(KeyFor(profile), Math.Max(0, celsius));

    private static string KeyFor(PowerProfile profile) => $"GpuThermal_{profile}";

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

        Logger.Info($"GPU thermal: target {target} C (range {limits.Min}-{limits.Default} C).");

        return new Result(true, target == limits.Default
            ? $"Following the driver default ({limits.Default} C)."
            : $"Target set to {target} C.");
    }

    /// <summary>
    /// Applies the profile's chosen target - on a mode change, after a resume, or at start-up.
    ///
    /// A profile with no value restores the driver's default rather than leaving the register alone: the
    /// policy is one global target, so "this profile has none" can only mean the default, or the previous
    /// profile's target would follow the user into every mode (measured: 85 C set in Balanced stayed on in
    /// Performance, and GPU-Z confirmed the driver still held 85).
    /// </summary>
    public static void Reassert(PowerProfile profile)
    {
        if (!Managing) return;

        var saved = SavedFor(profile);
        if (Read() is not { } limits) return;

        var target = saved == 0 ? limits.Default : Math.Min(saved, limits.Default);
        if (limits.Current == target) return;

        Apply(saved == 0 ? null : saved);
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
