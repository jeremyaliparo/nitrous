using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Acer TrueHarmony, which is the DTS APO4 operating-mode preset. The control is the DTS
/// controller's own COM surface (<c>DTSAPOSystem</c>), not an Acer service and not a registry
/// write. The mode objects carry an output suffix (<c>APO4-Internal Stereo</c>,
/// <c>APO4-Headphone</c>, <c>APO4-External Stereo</c>); Nitrous preserves the active suffix so it
/// never moves a headphone preset onto the speakers.
/// </summary>
internal static class DtsTrueHarmony
{
    private const string Clsid = "0113592D-0FDC-481F-88C8-294E28635456";

    /// <summary>The preset names as the controller spells them, with the labels Nitrous shows.</summary>
    public static readonly (string Key, string Label)[] Presets =
    [
        ("APO4-Off", "Off"),
        ("APO4-Music", "Music"),
        ("APO4-Movie", "Movie"),
        ("APO4-Voice", "Voice"),
        ("APO4-Game1", "Game 1"),
        ("APO4-Game2", "Game 2"),
        ("APO4-Game3", "Game 3"),
        ("APO4-Custom Audio", "Custom"),
        ("APO4-Automatic", "Automatic")
    ];

    /// <summary>The endpoint in use: its current preset, the output it is on, and the presets it offers.</summary>
    public sealed record State(
        string FriendlyName,
        string CurrentBase,
        string OutputSuffix,
        IReadOnlyList<string> Bases);

    private static bool _loggedUnavailable;

    /// <summary>Reads the active endpoint and its presets; null when DTS is not installed or idle.</summary>
    public static State? Read()
    {
        try
        {
            return RunSta(() => WithBestEndpoint((_, state) => (state, "")).State);
        }
        catch (Exception ex)
        {
            LogUnavailable(ex);
            return null;
        }
    }

    /// <summary>
    /// Selects a preset on the current output (speaker/headphone/external) and returns the state
    /// DTS reports afterwards, so the UI shows the mode that was actually applied.
    /// </summary>
    public static (State? Applied, string Message) Set(string baseName)
    {
        try
        {
            return RunSta(() => WithBestEndpoint((endpoint, state) => SetOnEndpoint(endpoint, state, baseName)));
        }
        catch (Exception ex)
        {
            LogUnavailable(ex);
            return (null, ex.Message);
        }
    }

    public static string LabelFor(string baseName)
    {
        foreach (var (key, label) in Presets)
        {
            if (string.Equals(key, baseName, StringComparison.OrdinalIgnoreCase))
                return label;
        }

        return baseName.Replace("APO4-", "");
    }

    /// <summary>What the controller reports, for a diagnostics bundle - no state is changed.</summary>
    public static Dictionary<string, object?> GetDiagnosticsSnapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var controller = FindController();

        result["installed"] = controller is not null;
        result["controllerDirectory"] = controller;

        var state = Read();
        if (state is null)
        {
            result["available"] = false;
            return result;
        }

        result["available"] = true;
        result["endpoint"] = state.FriendlyName;
        result["currentPreset"] = state.CurrentBase;
        result["output"] = state.OutputSuffix;
        result["presets"] = state.Bases.ToArray();
        return result;
    }

    /// <summary>The output the active mode is on, in the words the control shows.</summary>
    public static string OutputLabel(string suffix)
    {
        if (suffix.Contains("Headphone", StringComparison.OrdinalIgnoreCase)) return "headphones";
        if (suffix.Contains("External", StringComparison.OrdinalIgnoreCase)) return "external output";
        return "built-in speakers";
    }

    private static (State? Applied, string Message) SetOnEndpoint(
        IDTSEndpointInfo endpoint, State state, string baseName)
    {
        // Match the suffix the current output is on, not another output's stored preset.
        var suffix = string.IsNullOrWhiteSpace(state.OutputSuffix)
            ? "APO4-Internal Stereo"
            : state.OutputSuffix;

        IDTSOperatingMode? target = null;
        IDTSOperatingMode? fallback = null;

        try
        {
            if (endpoint.GetOpModeEnumerator(out var modes) == 0 && modes is not null)
            {
                try
                {
                    while (modes.Next(out var mode) == 0 && mode is not null)
                    {
                        mode.Name(out var name);
                        if (!TryParse(name, out var modeBase, out var modeSuffix) ||
                            !string.Equals(modeBase, baseName, StringComparison.OrdinalIgnoreCase))
                        {
                            Marshal.ReleaseComObject(mode);
                            continue;
                        }

                        if (target is null &&
                            string.Equals(modeSuffix, suffix, StringComparison.OrdinalIgnoreCase))
                        {
                            target = mode;
                        }
                        else if (fallback is null)
                        {
                            fallback = mode;
                        }
                        else
                        {
                            Marshal.ReleaseComObject(mode);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(modes);
                }
            }

            target ??= fallback;
            if (target is null) return (null, "That preset is not offered for this output.");

            var hr = endpoint.SetMode(target);
            if (hr != 0) return (null, $"DTS returned 0x{hr:X8}.");

            // Read back: the UI must not claim a mode DTS refused.
            var after = ReadEndpoint(endpoint);
            if (after is { CurrentBase.Length: > 0 } fresh &&
                string.Equals(fresh.CurrentBase, baseName, StringComparison.OrdinalIgnoreCase))
            {
                return (fresh, $"{LabelFor(fresh.CurrentBase)} · {OutputLabel(fresh.OutputSuffix)}");
            }

            return (after ?? state, $"{LabelFor(baseName)} · DTS did not confirm the change.");
        }
        finally
        {
            if (target is not null) Marshal.ReleaseComObject(target);
            if (fallback is not null && !ReferenceEquals(fallback, target)) Marshal.ReleaseComObject(fallback);
        }
    }

    /// <summary>
    /// Initialises the controller, picks the most useful endpoint (the speaker endpoint first, then
    /// whatever advertises presets) and runs <paramref name="action"/> while the controller is
    /// still alive - the endpoint objects live inside it.
    /// </summary>
    private static (State? State, string Message) WithBestEndpoint(
        Func<IDTSEndpointInfo, State, (State?, string)> action)
    {
        var controller = FindController();
        if (controller is null) return (null, "DTS is not installed.");

        var system = (IDTSAPOSystem)new DTSAPOSystemClass();
        try
        {
            if (system.InitializeSystem(controller) != 0) return (null, "DTS did not initialise.");
            if (system.EnumerateDTSEndpoints(out var endpoints) != 0 || endpoints is null)
                return (null, "DTS has no audio endpoints.");

            IDTSEndpointInfo? best = null;
            State? bestState = null;
            var bestScore = -1;
            var seen = false;

            try
            {
                while (endpoints.Next(out var endpoint) == 0 && endpoint is not null)
                {
                    seen = true;

                    var state = ReadEndpoint(endpoint);
                    if (state is null)
                    {
                        Marshal.ReleaseComObject(endpoint);
                        continue;
                    }

                    var score = Score(state);
                    if (score <= bestScore)
                    {
                        Marshal.ReleaseComObject(endpoint);
                        continue;
                    }

                    if (best is not null) Marshal.ReleaseComObject(best);
                    best = endpoint;
                    bestState = state;
                    bestScore = score;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(endpoints);
            }

            if (best is null || bestState is null)
                return (null, seen ? "DTS has no usable endpoint." : "DTS has no audio endpoints.");

            try
            {
                return action(best, bestState);
            }
            finally
            {
                Marshal.ReleaseComObject(best);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(system);
        }
    }

    private static int Score(State state)
    {
        var score = 1;
        if (!string.IsNullOrWhiteSpace(state.OutputSuffix)) score += 2;
        if (state.FriendlyName.Contains("Speaker", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (state.OutputSuffix.Contains("Internal", StringComparison.OrdinalIgnoreCase)) score += 2;
        return score;
    }

    private static State? ReadEndpoint(IDTSEndpointInfo endpoint)
    {
        endpoint.FriendlyName(out var friendly);
        if (endpoint.GetMode(out var current) != 0 || current is null) return null;

        string? currentName = null;
        try
        {
            current.Name(out currentName);
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }

        if (!TryParse(currentName, out var currentBase, out var suffix) || currentBase.Length == 0)
            return null;

        var bases = new List<string>();
        if (endpoint.GetOpModeEnumerator(out var modes) == 0 && modes is not null)
        {
            try
            {
                while (modes.Next(out var mode) == 0 && mode is not null)
                {
                    try
                    {
                        mode.Name(out var name);
                        if (TryParse(name, out var modeBase, out _) &&
                            Presets.Any(p => string.Equals(p.Key, modeBase, StringComparison.OrdinalIgnoreCase)) &&
                            !bases.Contains(modeBase, StringComparer.OrdinalIgnoreCase))
                        {
                            bases.Add(modeBase);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(mode);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(modes);
            }
        }

        if (bases.Count == 0) return null;

        // Keep the curated order, so the dropdown does not follow the controller's own order.
        var ordered = Presets
            .Where(p => bases.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .ToList();

        return new State(friendly, currentBase, suffix, ordered);
    }

    internal static bool TryParse(string? modeName, out string baseName, out string suffix)
    {
        baseName = "";
        suffix = "";
        if (string.IsNullOrWhiteSpace(modeName)) return false;

        var colon = modeName.IndexOf(':');
        var rest = colon >= 0 ? modeName[(colon + 1)..] : modeName;
        var slash = rest.IndexOf('/');
        baseName = (slash >= 0 ? rest[..slash] : rest).Trim();
        suffix = slash >= 0 ? rest[(slash + 1)..].Trim() : "";
        return baseName.StartsWith("APO4-", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindController()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{Clsid}}}\InProcServer32");
            var dll = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) return null;
            return Path.GetDirectoryName(dll);
        }
        catch
        {
            return null;
        }
    }

    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Nitrous DTS"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The DTS controller did not answer.");

        if (error is not null)
            throw new InvalidOperationException(error.Message, error);

        return result;
    }

    private static void LogUnavailable(Exception ex)
    {
        if (_loggedUnavailable) return;
        _loggedUnavailable = true;
        Logger.Warn("TrueHarmony: the DTS controller is unavailable; the control will be hidden.", ex);
    }

    [ComImport, Guid(Clsid)]
    private class DTSAPOSystemClass
    {
    }

    [ComImport, Guid("b0fe726b-d73c-4762-afb2-cf80a3df8515"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDTSAPOSystem
    {
        [PreserveSig] int EnumerateDTSEndpoints(out IDTSEndpointEnumerator epEnum);
        [PreserveSig] int InitializeSystem([MarshalAs(UnmanagedType.BStr)] string apoPath);
    }

    [ComImport, Guid("e8361eaf-0bfe-4a6b-b0db-4ebea2f9e8fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDTSEndpointEnumerator
    {
        [PreserveSig] int Next(out IDTSEndpointInfo device);
        [PreserveSig] int Reset();
        [PreserveSig] int GetEndpointFromDevice([MarshalAs(UnmanagedType.BStr)] string deviceId, out IDTSEndpointInfo device);
    }

    [ComImport, Guid("3f120a03-8348-4b25-a0ba-7350a28748fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDTSEndpointInfo
    {
        [PreserveSig] int FriendlyName([MarshalAs(UnmanagedType.BStr)] out string friendlyName);
        [PreserveSig] int ConfigFile([MarshalAs(UnmanagedType.BStr)] out string configFile);
        [PreserveSig] int RegistryPath([MarshalAs(UnmanagedType.BStr)] out string registryPath);
        [PreserveSig] int GetOpModeEnumerator(out IDTSAPOOpModeEnumerator e);
        [PreserveSig] int SetDefault();

        // The type library labels these get/put, but the vtable slot order is put then get: the
        // single-pointer slot takes the mode object. Verified against the controller in a probe.
        [PreserveSig] int SetMode([MarshalAs(UnmanagedType.Interface)] IDTSOperatingMode mode);
        [PreserveSig] int GetMode(out IDTSOperatingMode mode);
    }

    [ComImport, Guid("d455ec58-c868-4dd5-951d-70b4bd6e2d38"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDTSAPOOpModeEnumerator
    {
        [PreserveSig] int Next(out IDTSOperatingMode opMode);
    }

    [ComImport, Guid("2a6fd46b-3d43-4b41-82dd-dd5daa0debcb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDTSOperatingMode
    {
        [PreserveSig] int DTSAPOControlEnumerator(int type, out IntPtr controlEnumerator);
        [PreserveSig] int Name([MarshalAs(UnmanagedType.BStr)] out string name);
    }
}
