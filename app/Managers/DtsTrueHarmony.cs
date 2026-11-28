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

    /// <summary>The operating-mode control the APO exposes on every mode object.</summary>
    private const string OperatingModeControlName = "APO4-Operating Mode";

    private const string AutomaticBase = "APO4-Automatic";

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
        IReadOnlyList<string> Bases,
        bool Automatic,
        string RegistryPath,
        bool ApoEnabled,
        bool AutoContentModeEnabled);

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
        result["endpointGuid"] = EndpointGuid(state.RegistryPath);
        result["currentPreset"] = state.CurrentBase;
        result["automatic"] = state.Automatic;
        result["output"] = state.OutputSuffix;
        result["apoEnabled"] = state.ApoEnabled;
        result["autoContentModeEnabled"] = state.AutoContentModeEnabled;
        result["enhancementsDisabled"] = EnhancementsDisabled(state.RegistryPath);
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

        // Automatic is not a mode object: the APO's operating-mode control takes it directly and
        // keeps the last preset's output (speakers, headphones) as the speaker type.
        if (string.Equals(baseName, AutomaticBase, StringComparison.OrdinalIgnoreCase))
        {
            if (!WriteOperatingModeControl(endpoint, AutomaticBase))
                return (null, "This output does not offer Automatic.");

            var applied = ReadEndpoint(endpoint);
            return applied is { Automatic: true }
                ? (applied, $"{LabelFor(AutomaticBase)} · {OutputLabel(applied.OutputSuffix)}")
                : (null, "DTS did not keep Automatic.");
        }

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

            // The mode object can already be this preset - the user may be coming from Automatic -
            // and then SetMode is a no-op; the control is what the APO actually processes.
            WriteOperatingModeControl(endpoint, baseName);

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
        endpoint.RegistryPath(out var registryPath);
        if (endpoint.GetMode(out var current) != 0 || current is null) return null;

        string? currentName = null;
        string? controlValue = null;
        var apoEnabled = false;
        var autoContentMode = false;
        try
        {
            current.Name(out currentName);
            controlValue = ReadOperatingModeControl(current);
            apoEnabled = current.GetApoEnable(out var apo) == 0 && apo != 0;
            autoContentMode = current.GetAutoContentModeEnable(out var auto) == 0 && auto != 0;
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }

        if (!TryParse(currentName, out var modeBase, out var suffix) || modeBase.Length == 0)
            return null;

        // The control, not the mode object, is what the APO processes: it also carries
        // "APO4-Automatic", which has no mode object of its own.
        var automaticSupported = TryParse(controlValue, out var controlBase, out _);
        var automatic = automaticSupported &&
                        string.Equals(controlBase, AutomaticBase, StringComparison.OrdinalIgnoreCase);
        var currentBase = automaticSupported ? controlBase : modeBase;

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
                        if (TryParse(name, out var candidate, out _) &&
                            Presets.Any(p => string.Equals(p.Key, candidate, StringComparison.OrdinalIgnoreCase)) &&
                            !bases.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                        {
                            bases.Add(candidate);
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

        if (automaticSupported) bases.Add(AutomaticBase);
        if (bases.Count == 0) return null;

        // Keep the curated order, so the dropdown does not follow the controller's own order.
        var ordered = Presets
            .Where(p => bases.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .ToList();

        return new State(friendly, currentBase, suffix, ordered, automatic,
            registryPath ?? "", apoEnabled, autoContentMode);
    }

    /// <summary>The endpoint GUID the controller's registry path carries, e.g. "{096836A5-...}".</summary>
    private static string? EndpointGuid(string? registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath)) return null;
        var open = registryPath.LastIndexOf('{');
        return open >= 0 ? registryPath[open..].Trim() : null;
    }

    /// <summary>
    /// Windows' own "disable audio enhancements" flag for the endpoint. When it is set, the DTS APO
    /// is not in the processing chain: the controller still accepts a mode, but nothing is heard.
    /// </summary>
    private static bool? EnhancementsDisabled(string? registryPath)
    {
        var guid = EndpointGuid(registryPath);
        if (guid is null) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\Properties");

            if (key is null) return null;
            return key.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5") is int value && value != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The APO's operating-mode string for a mode object, e.g. "APO4-Music" or "APO4-Automatic".
    /// This is the value the DTS:X app writes; it is read back after every change.
    /// </summary>
    private static string? ReadOperatingModeControl(IDTSOperatingMode mode)
    {
        using var control = OpenOperatingModeControl(mode);
        if (control is null) return null;

        return control.Value.GetStrValue(out var value) == 0 ? value : null;
    }

    /// <summary>Writes the APO's operating-mode string; false when the control is absent or refused it.</summary>
    private static bool WriteOperatingModeControl(IDTSEndpointInfo endpoint, string value)
    {
        if (endpoint.GetMode(out var mode) != 0 || mode is null) return false;

        try
        {
            using var control = OpenOperatingModeControl(mode);
            return control is not null && control.Value.SetStrValue(value) == 0;
        }
        finally
        {
            Marshal.ReleaseComObject(mode);
        }
    }

    private static ControlHandle? OpenOperatingModeControl(IDTSOperatingMode mode)
    {
        if (mode.DTSAPOControlEnumerator(0, out var pointer) != 0 || pointer == IntPtr.Zero)
            return null;

        var enumerator = (IDTSAPOControlEnumerator2)Marshal.GetObjectForIUnknown(pointer);
        Marshal.Release(pointer);

        try
        {
            if (enumerator.GetControlFromName(OperatingModeControlName, out var control) != 0 ||
                control is null)
            {
                return null;
            }

            return new ControlHandle(control, enumerator);
        }
        catch
        {
            Marshal.ReleaseComObject(enumerator);
            throw;
        }
    }

    /// <summary>Owns the control and the enumerator it came from, so both are released together.</summary>
    private sealed class ControlHandle(IDTSAPOControl2 value, object owner) : IDisposable
    {
        public IDTSAPOControl2 Value { get; } = value;

        public void Dispose()
        {
            Marshal.ReleaseComObject(Value);
            Marshal.ReleaseComObject(owner);
        }
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
        [PreserveSig] int RestoreDefaults();
        [PreserveSig] int GetApoEnable(out int value);
        [PreserveSig] int SetApoEnable(int value);
        [PreserveSig] int GetAutoContentModeEnable(out int value);
        [PreserveSig] int SetAutoContentModeEnable(int value);
    }

    [ComImport, Guid("00b60358-499c-489d-881c-0d10da751c8f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDTSAPOControlEnumerator2
    {
        [PreserveSig] int Next(out IDTSAPOControl2 control);
        [PreserveSig] int GetControlFromName([MarshalAs(UnmanagedType.BStr)] string name, out IDTSAPOControl2 control);
    }

    [ComImport, Guid("a128945a-0990-4156-b00c-dbadba10065e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDTSAPOControl2
    {
        [PreserveSig] int DisplayValue([MarshalAs(UnmanagedType.BStr)] out string value);
        [PreserveSig] int Scale(out int scale);
        [PreserveSig] int MinValue(out int value);
        [PreserveSig] int MaxValue(out int value);
        [PreserveSig] int GetValue(out int value);
        [PreserveSig] int SetValue(int value);
        [PreserveSig] int GetStrValue([MarshalAs(UnmanagedType.BStr)] out string value);
        [PreserveSig] int SetStrValue([MarshalAs(UnmanagedType.BStr)] string value);
    }
}
