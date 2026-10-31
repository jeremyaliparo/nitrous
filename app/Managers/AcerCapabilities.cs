using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using Nitrous.Diagnostics;
using Nitrous.Enums;

namespace Nitrous.Managers;

/// <summary>Where the operating mode is read and set on this machine.</summary>
internal enum PowerModeChannel
{
    /// <summary>The firmware offers no channel Nitrous can use.</summary>
    None,

    /// <summary>The embedded controller's HID interface (2024+ models).</summary>
    EcHid,

    /// <summary>The Acer gaming WMI register, misc setting 0x0B.</summary>
    Wmi,

    /// <summary>
    /// <c>APGeAction</c> function 7, where it is the mode (NL16-71G, PHN16-73). Detected and
    /// reported, but not written: no such machine has been measured, and an unverified firmware
    /// write is worse than a clear "not supported".
    /// </summary>
    ActionFunction7
}

/// <summary>What <c>APGeAction</c> function 7 is on this firmware, from its own answer.</summary>
internal enum AcerActionFunction
{
    Unknown,
    CoolBoost,
    OperatingMode
}

/// <summary>The Acer gaming WMI surface as this firmware presents it.</summary>
internal sealed record AcerWmiSurface(
    bool GamingClassPresent,
    bool ActionClassPresent,
    IReadOnlyList<string> GamingMethods,
    IReadOnlyList<PowerProfile> SupportedProfiles,
    AcerActionFunction ActionFunction)
{
    public static AcerWmiSurface Empty { get; } = new(
        false, false, Array.Empty<string>(), Array.Empty<PowerProfile>(), AcerActionFunction.Unknown);
}

/// <summary>
/// Everything Nitrous knows about this machine's Acer interfaces, probed once and cached.
///
/// The point is that no model is special-cased in code: the channel is chosen from what the
/// hardware answers, and a model that is not in any table still works as long as it follows the
/// protocol. <see cref="AcerModelProfile"/> exists only for firmware that answers wrongly.
/// </summary>
internal sealed record AcerCapabilities(
    string? Manufacturer,
    string? Model,
    string? BiosVersion,
    AcerSmbios Smbios,
    AcerWmiSurface Wmi,
    EcHidInfo? EcHid,
    AcerModelProfile Profile)
{
    public PowerModeChannel PowerModeChannel =>
        EcHid is { Modes.Count: > 0 } ? PowerModeChannel.EcHid
        : Wmi.SupportedProfiles.Count > 0 ? PowerModeChannel.Wmi
        : Wmi.ActionFunction == AcerActionFunction.OperatingMode ? PowerModeChannel.ActionFunction7
        : PowerModeChannel.None;

    /// <summary>True when Nitrous can actually change the operating mode here.</summary>
    public bool CanSetPowerMode => PowerModeChannel is PowerModeChannel.EcHid or PowerModeChannel.Wmi;

    /// <summary>
    /// Whether the channel Nitrous would use can select Turbo. This is what the UI shows, so it
    /// has to agree with what <see cref="AcerWmiManager.SetPowerModeAsync"/> will do — the WMI
    /// mask alone disagreed on models whose controller lists the mode but whose register does not.
    /// </summary>
    public bool SupportsTurbo => PowerModeChannel switch
    {
        PowerModeChannel.EcHid => EcHid is { } hid && hid.Modes.Contains(PowerProfile.Turbo),
        PowerModeChannel.Wmi => Wmi.SupportedProfiles.Contains(PowerProfile.Turbo),
        _ => false
    };

    public string Describe() =>
        $"model {Model ?? "unknown"} ({BiosVersion ?? "unknown BIOS"}); " +
        $"channel {PowerModeChannel}; " +
        $"ec hid {(EcHid is null ? "absent" : $"{EcHid.Version ?? "?"} modes [{string.Join(", ", EcHid.Modes)}]")}; " +
        $"wmi modes [{string.Join(", ", Wmi.SupportedProfiles)}]; " +
        $"apge action {Wmi.ActionFunction}; " +
        Smbios.Describe();
}

/// <summary>
/// Probes <see cref="AcerCapabilities"/> once and hands out the cached copy. Call
/// <see cref="Invalidate"/> when a device operation fails, so the next use re-reads the machine
/// instead of trusting a snapshot that may have gone stale across a resume or a BIOS update.
/// </summary>
internal static class AcerCapabilitiesProvider
{
    private static readonly object Gate = new();
    private static AcerCapabilities? _cached;

    public static AcerCapabilities Current
    {
        get
        {
            lock (Gate)
            {
                return _cached ??= Probe();
            }
        }
    }

    public static AcerCapabilities Refresh()
    {
        lock (Gate)
        {
            _cached = Probe();
            return _cached;
        }
    }

    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    private static AcerCapabilities Probe()
    {
        var (manufacturer, model, bios) = ReadIdentity();
        var smbios = AcerSmbios.Read();
        var wmi = ProbeWmi();
        var ecHid = ProbeEcHid();

        var capabilities = new AcerCapabilities(
            manufacturer, model, bios, smbios, wmi, ecHid, AcerModelProfile.For(model));

        Logger.Info($"Capabilities: {capabilities.Describe()}");

        if (capabilities.PowerModeChannel == PowerModeChannel.ActionFunction7)
        {
            Logger.Warn(
                "Capabilities: the operating mode is on APGeAction function 7 here. That path is " +
                "detected but not written yet, so power modes are read-only on this model.");
        }

        return capabilities;
    }

    private static (string? Manufacturer, string? Model, string? Bios) ReadIdentity()
    {
        string? manufacturer = null, model = null, bios = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject row in searcher.Get())
            {
                using (row)
                {
                    manufacturer = row["Manufacturer"]?.ToString();
                    model = row["Model"]?.ToString();
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Capabilities: could not read the machine model.", ex);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (ManagementObject row in searcher.Get())
            {
                using (row)
                {
                    bios = row["SMBIOSBIOSVersion"]?.ToString();
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Capabilities: could not read the BIOS version.", ex);
        }

        return (manufacturer, model, bios);
    }

    private static AcerWmiSurface ProbeWmi()
    {
        var gamingPresent = false;
        var actionPresent = false;
        var methods = new List<string>();
        var profiles = new List<PowerProfile>();
        var action = AcerActionFunction.Unknown;

        try
        {
            using var gamingClass = new ManagementClass(
                AcerWmiProtocol.WmiNamespace, AcerWmiProtocol.GamingClass, null);
            gamingPresent = true;
            methods.AddRange(gamingClass.Methods.Cast<MethodData>().Select(m => m.Name));
        }
        catch
        {
            gamingPresent = false;
        }

        if (gamingPresent)
        {
            profiles.AddRange(ReadSupportedProfiles());
        }

        try
        {
            using var actionClass = new ManagementClass(AcerWmiProtocol.WmiNamespace, "APGeAction", null);
            actionPresent = true;
            action = ReadActionFunction();
        }
        catch
        {
            actionPresent = false;
        }

        return new AcerWmiSurface(gamingPresent, actionPresent, methods, profiles, action);
    }

    private static IReadOnlyList<PowerProfile> ReadSupportedProfiles()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                AcerWmiProtocol.WmiNamespace,
                $"SELECT * FROM {AcerWmiProtocol.GamingClass}");

            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var inParams = instance.GetMethodParameters(AcerWmiProtocol.GetGamingMiscSetting);
                    inParams["gmInput"] = AcerWmiProtocol.SupportedProfilesSetting;
                    using var outParams = instance.InvokeMethod(
                        AcerWmiProtocol.GetGamingMiscSetting, inParams, null);

                    var raw = outParams?.Properties["gmOutput"]?.Value;
                    if (raw == null) return Array.Empty<PowerProfile>();

                    var mask = (int)((Convert.ToUInt64(raw, CultureInfo.InvariantCulture) >> 8) & 0xFF);
                    return Enum.GetValues<PowerProfile>()
                        .Where(profile => (mask & (1 << (int)profile)) != 0)
                        .ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Capabilities: could not read the supported power profiles.", ex);
        }

        return Array.Empty<PowerProfile>();
    }

    private static AcerActionFunction ReadActionFunction()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                AcerWmiProtocol.WmiNamespace, "SELECT * FROM APGeAction");

            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var inParams = instance.GetMethodParameters("GetFunction");
                    inParams["uiInput"] = 0x0007u; // sub-function 0: "what is function 7?"
                    using var outParams = instance.InvokeMethod("GetFunction", inParams, null);

                    var raw = outParams?.Properties["uiOutput"]?.Value;
                    if (raw == null) return AcerActionFunction.Unknown;

                    return (byte)((Convert.ToUInt64(raw, CultureInfo.InvariantCulture) >> 16) & 0xFF) switch
                    {
                        1 => AcerActionFunction.CoolBoost,
                        6 => AcerActionFunction.OperatingMode,
                        _ => AcerActionFunction.Unknown
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Capabilities: could not read the APGeAction function.", ex);
        }

        return AcerActionFunction.Unknown;
    }

    private static EcHidInfo? ProbeEcHid()
    {
        try
        {
            return AcerEcHid.Probe();
        }
        catch (Exception ex)
        {
            Logger.Warn("Capabilities: could not probe the EC HID interface.", ex);
            return null;
        }
    }
}
