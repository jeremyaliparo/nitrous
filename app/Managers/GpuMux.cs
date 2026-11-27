using System.Threading.Tasks;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>GPU (MUX) mode. The values are the firmware encoding: 1 hybrid, 2 discrete, 3 automatic.</summary>
public enum GpuMuxMode : byte
{
    Hybrid = 1,
    Discrete = 2,
    Automatic = 3,
}

/// <summary>Support mask (misc 0x09) and stored mode (misc 0x02) as the firmware reports them.</summary>
public readonly record struct GpuMuxState(byte Mask, byte Mode)
{
    /// <summary>
    /// A real switch: the firmware lists hybrid and discrete, and the stored mode is one of the three.
    /// The ANV15-51 ZYQ answers 7 for the mask but 0xFF for the mode and refuses every write, so both
    /// registers have to be believable.
    /// </summary>
    public bool Supported => (Mask & 3) == 3 && Mode is 1 or 2 or 3;

    public bool Automatic => Supported && (Mask & 4) != 0;

    public GpuMuxMode Current => (GpuMuxMode)Mode;
}

public readonly record struct GpuMuxResult(bool Ok, string Message, GpuMuxMode? Stored);

/// <summary>
/// The Acer BIOS GPU MUX. A stored setting for the next boot, not a live display switch: the same
/// contract as OpenSense and Nitro-Hub, confirmed by PreySense-Nitro. It is never written on startup,
/// never re-applied on a power-mode change and never stored per profile.
/// </summary>
internal static class GpuMux
{
    public static string Name(GpuMuxMode mode) => mode switch
    {
        GpuMuxMode.Hybrid => "Hybrid",
        GpuMuxMode.Discrete => "Discrete",
        GpuMuxMode.Automatic => "Automatic",
        _ => "Unknown",
    };

    public static string Describe(GpuMuxMode mode) => mode switch
    {
        GpuMuxMode.Hybrid =>
            "The integrated GPU drives the built-in display; the NVIDIA GPU renders on demand. Best battery life.",
        GpuMuxMode.Discrete =>
            "Only the NVIDIA GPU drives the built-in display. Best performance; battery life drops.",
        GpuMuxMode.Automatic =>
            "The firmware chooses the GPU that drives the built-in display.",
        _ => string.Empty,
    };

    public static async Task<GpuMuxState?> ReadAsync()
    {
        var raw = await AcerWmiManager.ReadGpuMuxAsync().ConfigureAwait(false);
        if (raw is not { } value) return null;

        var state = new GpuMuxState(value.Mask, value.Mode);
        Logger.Info($"GPU MUX: support 0x{value.Mask:X2}, stored mode {(byte)value.Mode} ({Name(state.Current)}).");
        return state;
    }

    public static async Task<GpuMuxResult> ApplyAsync(GpuMuxMode mode)
    {
        var current = await ReadAsync().ConfigureAwait(false);
        if (current is not { } state)
            return new(false, "The firmware did not answer the GPU mode registers.", null);

        if (!state.Supported)
            return new(false, "This firmware does not list a GPU mode switch.", null);

        if (mode == GpuMuxMode.Automatic && !state.Automatic)
            return new(false, "This firmware does not offer automatic selection.", null);

        if (state.Current == mode)
            return new(true, $"{Name(mode)} is already stored.", null);

        var (ok, status, stored) = await AcerWmiManager.SetGpuMuxAsync((byte)mode).ConfigureAwait(false);
        if (!ok)
            return new(false, status == 0xFF
                ? "The GPU mode write did not reach the firmware."
                : $"The firmware refused the GPU mode (status {status}).", null);

        if (stored != (byte)mode)
            return new(false, $"The firmware stored mode {stored} instead of {(byte)mode}.", null);

        Logger.Info($"GPU MUX: stored {Name(mode)}; it applies at the next restart.");
        return new(true, $"{Name(mode)} stored. Restart to apply.", mode);
    }
}

