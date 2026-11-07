using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nitrous.Enums;
using Nitrous.Managers;

namespace Nitrous.Diagnostics;

public sealed record EcProbeResult(string JsonPath, string SummaryPath, int ErrorCount);

/// <summary>
/// Read-only fingerprint of the Acer EC/firmware contract.
///
/// Acer reuses the same <c>AcerGamingFunction</c> WMI class across models, but the meaning of
/// the addresses, the payload encodings and even the exposed method set differ between BIOS
/// versions. This probe records what a specific machine actually exposes so a report from a
/// model we do not own can be compared against one that is known to work.
///
/// The probe never writes. Every firmware call is routed through
/// <see cref="InvokeGetLocked"/>, which refuses any method name that does not start with
/// "Get", so a Set* call cannot be added here by accident.
/// </summary>
public static class EcProbe
{
    private const string ReadOnlyPrefix = "Get";

    // Addresses Nitrous already knows about. They are probed as-is: a model that uses
    // different addresses reports null rather than a plausible-looking zero.
    private static readonly (string Name, uint Address, ulong Mask)[] KnownSensors =
    {
        ("cpuTemperature", AcerWmiProtocol.CpuTemperature, AcerWmiProtocol.ByteMask),
        ("cpuFanRpm", AcerWmiProtocol.CpuFanRpm, AcerWmiProtocol.WordMask),
        ("gpuFanRpm", AcerWmiProtocol.GpuFanRpm, AcerWmiProtocol.WordMask),
        ("gpuTemperaturePrimary", AcerWmiProtocol.GpuTemperaturePrimary, AcerWmiProtocol.ByteMask),
        ("gpuTemperatureFallbackA", AcerWmiProtocol.GpuTemperatureFallbackA, AcerWmiProtocol.ByteMask),
        ("gpuTemperatureFallbackB", AcerWmiProtocol.GpuTemperatureFallbackB, AcerWmiProtocol.ByteMask),
    };

    private static readonly string[] ProbedClasses =
    {
        AcerWmiProtocol.GamingClass,

        // The class power-off USB charging actually lives on. It was missing from this list, so
        // every sweep ran against AcerGenericMethod instead and answered 0xE1 for everything.
        AcerWmiProtocol.ActionClass,
        "AcerGenericMethod",
        AcerWmiProtocol.BatteryClass,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string OutputDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nitrous", "ec-probe");

    public static Task<EcProbeResult> RunAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(cancellationToken), cancellationToken);

    private static EcProbeResult Run(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(OutputDirectory);

        var errors = new List<string>();
        var report = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            // Bumped whenever a section or field is added: reports are compared across models, and
            // a reader has to know which shape it is looking at.
            ["probeVersion"] = 11,
            ["capturedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["readOnly"] = true,
            ["note"] = "Read-only Acer EC/firmware fingerprint. No Set* method is called by this probe."
        };

        report["machine"] = CollectMachine(errors);
        cancellationToken.ThrowIfCancellationRequested();

        report["capabilities"] = CollectCapabilities(errors);
        cancellationToken.ThrowIfCancellationRequested();

        report["classes"] = CollectClasses(errors);
        cancellationToken.ThrowIfCancellationRequested();

        report["ecHid"] = CollectEcHid(errors);
        cancellationToken.ThrowIfCancellationRequested();

        report["sensors"] = CollectSensors(errors);
        cancellationToken.ThrowIfCancellationRequested();

        report["miscSettings"] = CollectMiscSettings(errors);
        cancellationToken.ThrowIfCancellationRequested();

        report["profiles"] = CollectProfiles(errors);
        cancellationToken.ThrowIfCancellationRequested();

        report["genericFunction"] = CollectGenericFunction(errors);
        report["keyboardBacklight"] = CollectKeyboardBacklight(errors);
        report["ledState"] = CollectLedState(errors);
        report["fans"] = CollectFans(errors);
        report["battery"] = CollectBattery(errors);
        report["keyboardAutoOff"] = CollectKeyboardAutoOff(errors);
        report["hidDevices"] = CollectHidDevices(errors);
        report["errors"] = errors;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(OutputDirectory, $"ec-probe-{stamp}.json");
        var summaryPath = Path.Combine(OutputDirectory, $"ec-probe-{stamp}.txt");

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(summaryPath, BuildSummary(report, errors), Encoding.UTF8);

        Logger.Info($"EC probe: wrote {jsonPath} ({errors.Count} section error(s)).");
        return new EcProbeResult(jsonPath, summaryPath, errors.Count);
    }

    /// <summary>
    /// The capability probe's own view: which channel Nitrous picked, and why. This is the section
    /// that says whether a model is supported without anyone having to add it to a table.
    /// </summary>
    private static Dictionary<string, object?> CollectCapabilities(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var capabilities = AcerCapabilitiesProvider.Refresh();

            result["model"] = capabilities.Model;
            result["manufacturer"] = capabilities.Manufacturer;
            result["biosVersion"] = capabilities.BiosVersion;
            result["powerModeChannel"] = capabilities.PowerModeChannel.ToString();
            result["canSetPowerMode"] = capabilities.CanSetPowerMode;
            result["wmiGamingClassPresent"] = capabilities.Wmi.GamingClassPresent;
            result["wmiActionClassPresent"] = capabilities.Wmi.ActionClassPresent;
            result["wmiGamingMethods"] = capabilities.Wmi.GamingMethods.ToArray();
            result["wmiSupportedProfiles"] = capabilities.Wmi.SupportedProfiles
                .Select(profile => profile.ToString()).ToArray();
            result["apgeActionFunction"] = capabilities.Wmi.ActionFunction.ToString();
            result["smbiosGamingVersion"] = capabilities.Smbios.GamingVersion;
            result["smbiosOperatingModesListed"] = capabilities.Smbios.OperatingModesListed;
            result["smbiosKeyboardColor"] = capabilities.Smbios.KeyboardColor;
            result["smbiosEcLightBars"] = capabilities.Smbios.HasEcLightBars;
            result["smbiosLedArrayLength"] = capabilities.Smbios.LedArrayLength;
            result["smbiosHotkeyFunctions"] = capabilities.Smbios.HotkeyFunctions.Count;
            result["modelProfile"] = capabilities.Profile.ModelCode ?? "generic";
            result["requiresBalancedModeReset"] = capabilities.Profile.RequiresBalancedModeReset;
            result["summary"] = capabilities.Describe();
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
            errors.Add($"capabilities: {ex.Message}");
        }

        return result;
    }

    private static Dictionary<string, object?> CollectMachine(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        void Put(string key, Func<object?> read)
        {
            try { result[key] = read(); }
            catch (Exception ex) { errors.Add($"{key}: {ex.Message}"); }
        }

        Put("manufacturer", () => QueryFirst("Win32_ComputerSystem", "Manufacturer"));
        Put("model", () => QueryFirst("Win32_ComputerSystem", "Model"));
        Put("productName", () => QueryFirst("Win32_ComputerSystemProduct", "Name"));
        Put("productVersion", () => QueryFirst("Win32_ComputerSystemProduct", "Version"));
        Put("biosVersion", () => QueryFirst("Win32_BIOS", "SMBIOSBIOSVersion"));
        Put("biosReleaseDate", () => QueryFirst("Win32_BIOS", "ReleaseDate") is string d && d.Length >= 8
            ? $"{d[..4]}-{d[4..6]}-{d[6..8]}"
            : null);
        Put("osVersion", () => Environment.OSVersion.VersionString);
        Put("osArchitecture", () => Environment.Is64BitOperatingSystem ? "x64" : "x86");
        Put("isElevated", () => IsElevated());

        Put("gpuName", () => QueryFirst("Win32_VideoController", "Name"));
        Put("gpuDriverVersion", () => QueryFirst("Win32_VideoController", "DriverVersion"));

        return result;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? QueryFirst(string wmiClass, string property)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
        foreach (ManagementObject row in searcher.Get())
        {
            using (row)
            {
                return row[property]?.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Class metadata is readable even on machines where the instance itself is ACL'd, which
    /// makes the method list the most portable part of the fingerprint.
    /// </summary>
    private static Dictionary<string, object?> CollectClasses(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var className in ProbedClasses)
        {
            var entry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var managementClass = new ManagementClass(
                    AcerWmiProtocol.WmiNamespace, className, null);

                entry["present"] = true;
                entry["methods"] = managementClass.Methods
                    .Cast<MethodData>()
                    .Select(method => new Dictionary<string, object?>
                    {
                        ["name"] = method.Name,
                        ["wmiMethodId"] = QualifierValue(method.Qualifiers, "WmiMethodId"),
                        ["parameters"] = method.InParameters?.Properties
                            .Cast<PropertyData>()
                            .Select(p => $"{p.Name}:{p.Type}")
                            .ToArray() ?? Array.Empty<string>()
                    })
                    .OrderBy(m => m["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                entry["instanceName"] = TryGetInstanceName(className);
            }
            catch (Exception ex)
            {
                entry["present"] = false;
                entry["error"] = ex.Message;
                errors.Add($"{className}: {ex.Message}");
            }

            result[className] = entry;
        }

        return result;
    }

    private static object? QualifierValue(QualifierDataCollection? qualifiers, string name)
    {
        if (qualifiers == null) return null;
        foreach (QualifierData qualifier in qualifiers)
        {
            if (string.Equals(qualifier.Name, name, StringComparison.OrdinalIgnoreCase))
                return qualifier.Value?.ToString();
        }

        return null;
    }

    private static string? TryGetInstanceName(string className)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                AcerWmiProtocol.WmiNamespace, $"SELECT * FROM {className}");
            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    return instance["InstanceName"]?.ToString() ?? instance.Path?.ToString();
                }
            }
        }
        catch
        {
            // A denied instance is expected on some models; the method list still helps.
        }

        return null;
    }

    /// <summary>
    /// The embedded controller's HID interface. On models that have it, this is the one that
    /// actually changes the operating mode; the Acer gaming WMI register does not.
    /// </summary>
    private static Dictionary<string, object?> CollectEcHid(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var hid = AcerEcHid.Open();
            if (hid == null)
            {
                result["present"] = false;
                result["note"] =
                    "No Acer EC HID interface (VID 1025 / PID 174B, usage page 0xFF05) was found. " +
                    "On this model the operating mode is not settable through it.";
                return result;
            }

            result["present"] = true;
            result["devicePath"] = hid.DevicePath;
            result["vendorId"] = $"0x{hid.VendorId:X4}";
            result["productId"] = $"0x{hid.ProductId:X4}";
            result["usagePage"] = $"0x{hid.UsagePage:X4}";
            result["usage"] = $"0x{hid.Usage:X4}";
            result["featureLength"] = hid.FeatureLength;
            result["version"] = hid.Version;
            result["modeCapability"] = hid.Capability;
            result["modes"] = hid.Modes.Select(mode => mode.ToString()).ToArray();
            result["currentMode"] = hid.ReadMode()?.ToString();
            result["note"] =
                "This is the interface that sets the operating mode on models that have it. " +
                "It is read here only; the probe never writes.";
        }
        catch (Exception ex)
        {
            result["present"] = false;
            result["error"] = ex.Message;
            errors.Add($"ecHid: {ex.Message}");
        }

        return result;
    }

    private static Dictionary<string, object?> CollectSensors(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        WithGamingInstance(instance =>
        {
            foreach (var (name, address, mask) in KnownSensors)
            {
                var raw = TryGetSysInfo(instance, address);
                result[name] = new Dictionary<string, object?>
                {
                    ["address"] = $"0x{address:X4}",
                    ["raw"] = raw,
                    ["decoded"] = raw.HasValue ? (long)((raw.Value >> 8) & mask) : null
                };
            }

            return true;
        }, errors, "sensors");

        return result;
    }

    /// <summary>
    /// Sweeps the low misc-setting addresses. The supported-profile bitmap and the current
    /// power-mode register both live in this range, and the remaining addresses are what
    /// differ most between models.
    /// </summary>
    private static Dictionary<string, object?> CollectMiscSettings(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        WithGamingInstance(instance =>
        {
            for (uint address = 0x00; address <= 0x0F; address++)
            {
                var raw = TryGetMisc(instance, address);
                result[$"0x{address:X2}"] = new Dictionary<string, object?>
                {
                    ["raw"] = raw,
                    ["profileByte"] = raw.HasValue ? (int)((raw.Value >> 8) & 0xFF) : null
                };
            }

            return true;
        }, errors, "miscSettings");

        return result;
    }

    private static Dictionary<string, object?> CollectProfiles(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        WithGamingInstance(instance =>
        {
            var profiles = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index <= 7; index++)
            {
                profiles[$"{index}"] = TryInvoke(
                    instance, "GetGamingProfile", "gmInput", index, "gmOutput");
            }

            result["getGamingProfile"] = profiles;

            var supported = TryGetMisc(instance, AcerWmiProtocol.SupportedProfilesSetting);
            result["supportedProfilesRaw"] = supported;
            result["supportedProfilesBitmap"] = supported.HasValue
                ? $"0x{((supported.Value >> 8) & 0xFF):X2}"
                : null;
            result["supportedProfilesNames"] = supported.HasValue
                ? DecodeProfiles((int)((supported.Value >> 8) & 0xFF))
                : Array.Empty<string>();
            result["supportedProfilesUnmappedBits"] = supported.HasValue
                ? UnmappedProfileBits((int)((supported.Value >> 8) & 0xFF))
                : string.Empty;

            // A separate register set from GetGamingProfile. Nothing here has been swept before, and
            // a per-profile setting is a plausible home for the keyboard backlight switch.
            var profileSettings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index <= 0x0F; index++)
            {
                var (value, error) = TryReadRaw(
                    instance, "GetGamingProfileSetting", "gmInput", index, "gmOutput");
                profileSettings[$"0x{index:X2}"] = value;
                if (error != null) profileSettings[$"0x{index:X2}Error"] = error;
            }

            result["getGamingProfileSetting"] = profileSettings;

            var current = TryGetMisc(instance, (uint)AcerWmiProtocol.PowerModeOpcode);
            result["misc0BRaw"] = current;
            result["misc0BValue"] = current.HasValue
                ? DecodeProfile((int)((current.Value >> 8) & 0xFF))
                : null;
            result["misc0BNote"] =
                "Register 0x0B is the value last written by SetGamingMiscSetting. " +
                "It is not proof that the EC applied that profile: on the AN16S-61 the " +
                "enforced power limit does not follow it.";

            return true;
        }, errors, "profiles");

        return result;
    }

    private static string[] DecodeProfiles(int bitmap) =>
        Enumerable.Range(0, 8)
            .Where(bit => (bitmap & (1 << bit)) != 0)
            .Where(bit => Enum.IsDefined(typeof(PowerProfile), (ulong)bit))
            .Select(DecodeProfile)
            .ToArray();

    /// <summary>
    /// Bits set in the supported-profile mask that do not name a power profile. Both the AN16S-61
    /// (0x73) and the ANV15-41 (0x53) set bit 6, so it carries something other than a mode and
    /// should not be reported as one.
    /// </summary>
    private static string UnmappedProfileBits(int bitmap) =>
        string.Join(",", Enumerable.Range(0, 8)
            .Where(bit => (bitmap & (1 << bit)) != 0)
            .Where(bit => !Enum.IsDefined(typeof(PowerProfile), (ulong)bit))
            .Select(bit => $"0x{bit:X}"));

    private static string DecodeProfile(int value)
    {
        var name = Enum.IsDefined(typeof(PowerProfile), (ulong)value)
            ? ((PowerProfile)(ulong)value).ToString()
            : "unknown";
        return $"{name}({value})";
    }

    /// <summary>
    /// The generic Acer WMID door. Its return values are status codes rather than data, but
    /// whether it answers at all is one of the clearest differences between models.
    /// </summary>
    private static Dictionary<string, object?> CollectGenericFunction(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Two classes expose GetFunction and they are different function spaces. APGeAction is the
        // one Nitrous and Acer's QuickAccess agent use for power-off USB charging; AcerGenericMethod
        // is a separate space that some models implement instead. Sweeping only the latter answered
        // 0xE1 for every input on an ANV15-41 and hid whatever the real one is.
        foreach (var className in new[] { AcerWmiProtocol.ActionClass, "AcerGenericMethod" })
        {
            WithInstance(className, instance =>
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                for (uint index = 0; index <= 0x40; index++)
                {
                    var (value, error) = TryReadRaw(
                        instance, "GetFunction", "uiInput", index, "uiOutput");

                    values[$"{index}"] = value;
                    if (error != null) values[$"{index}Error"] = error;
                }

                result[className] = values;
                return true;
            }, errors, $"genericFunction/{className}");
        }

        return result;
    }

    private static void WithGamingInstance(
        Func<ManagementObject, bool> action,
        List<string> errors,
        string section) =>
        WithInstance(AcerWmiProtocol.GamingClass, action, errors, section);

    /// <summary>
    /// What this machine offers for the keyboard backlight. The auto-off lives on the EC HID
    /// interface on some models and inside the Acer WMI class on others, so both are reported
    /// alongside the SMBIOS colour count. Every call here is a Get.
    /// </summary>
    private static Dictionary<string, object?> CollectKeyboardBacklight(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var capabilities = AcerCapabilitiesProvider.Current;
            result["smbiosKeyboardColour"] = capabilities.Smbios.KeyboardColor;
            result["ecHidPresent"] = capabilities.EcHid != null;
        }
        catch (Exception ex)
        {
            errors.Add($"keyboardBacklight/capabilities: {ex.Message}");
        }

        try
        {
            result["eNekHidPresent"] = AcerKeyboard.IsAvailable();
        }
        catch (Exception ex)
        {
            errors.Add($"keyboardBacklight/eNek: {ex.Message}");
        }

        WithGamingInstance(instance =>
        {
            // The input is an address, as with the other Get* methods. Several are tried because
            // which one holds the backlight is exactly what differs between models, and the error
            // text is kept so a failure can be told apart from an unsupported method.
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var failures = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            var inputs = Enumerable.Range(0, 0x11).Select(index => (uint)index).Append(0xFFu);
            foreach (var input in inputs)
            {
                var (value, error) = TryReadRaw(
                    instance, "GetGamingKBBacklight", "gmInput", input, "gmOutput");

                values[$"0x{input:X2}"] = value;
                if (error != null) failures[$"0x{input:X2}"] = error;
            }

            result["getGamingKBBacklight"] = values;
            result["getGamingKBBacklightErrors"] = failures;

            // A null value cannot say whether the method returned nothing or returned a property
            // with another name, so the live address is described in full.
            try
            {
                result["getGamingKBBacklightOutput"] = DescribeOutput(
                    instance, "GetGamingKBBacklight", "gmInput", 0x01);
            }
            catch (Exception ex)
            {
                result["getGamingKBBacklightOutputError"] = ex.Message;
            }

            return true;
        }, errors, "keyboardBacklight");

        return result;
    }

    /// <summary>
    /// The other read-only LED methods, swept over their low inputs. The keyboard backlight timeout
    /// is not in the GetGamingKBBacklight answer on an ANV15-41, so it is either behind another
    /// input or carried by one of these. Nothing here writes.
    /// </summary>
    private static Dictionary<string, object?> CollectLedState(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        WithGamingInstance(instance =>
        {
            var sweeps = new (string Method, uint[] Inputs)[]
            {
                ("GetGamingLEDBehavior", new uint[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 }),
                ("GetGamingLED", new uint[] { 0x00, 0x01, 0x02, 0x03, 0x08, 0x10 }),
                ("GetGamingRgbKb", new uint[] { 0x01, 0x02, 0x04, 0x08, 0x0F })
            };

            foreach (var (method, inputs) in sweeps)
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                foreach (var input in inputs)
                {
                    var (value, error) = TryReadRaw(instance, method, "gmInput", input, "gmOutput");
                    values[$"0x{input:X2}"] = value;
                    if (error != null) values[$"0x{input:X2}Error"] = error;
                }

                result[method] = values;
            }

            return true;
        }, errors, "ledState");

        return result;
    }

    /// <summary>
    /// Fan behaviour and speed, read-only. A model that cannot answer these is a model where the fan
    /// controls will be disabled, so the report should say so rather than leave it to guesswork.
    /// </summary>
    private static Dictionary<string, object?> CollectFans(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        WithGamingInstance(instance =>
        {
            var behavior = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index <= 3; index++)
            {
                var (value, error) = TryReadRaw(
                    instance, "GetGamingFanBehavior", "gmInput", index, "gmOutput");

                behavior[$"{index}"] = value;
                if (error != null) behavior[$"{index}Error"] = error;
            }

            result["getGamingFanBehavior"] = behavior;

            var speeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in new uint[] { 1, 2, 4, 7 })
            {
                var (value, error) = TryReadRaw(
                    instance, "GetGamingFanSpeed", "gmInput", device, "gmOutput");

                speeds[$"0x{device:X}"] = value;
                if (error != null) speeds[$"0x{device:X}Error"] = error;
            }

            result["getGamingFanSpeed"] = speeds;

            // This one takes no parameters, so its whole answer is reported.
            result["getGamingFanTable"] = DescribeOutputWith(
                instance, "GetGamingFanTable", Array.Empty<(string Name, object Value)>());

            return true;
        }, errors, "fans");

        return result;
    }

    /// <summary>
    /// BatteryControl, where the charge limit lives. Only the interface is reported: the two Get
    /// methods reject every parameter shape tried - scalar and array, with reserved lengths one and
    /// five - and Nitrous does not call them, because the charge limit is applied with
    /// SetBatteryHealthControl and never read back. GetBattInfoInterface does answer, so it is kept.
    /// </summary>
    private static Dictionary<string, object?> CollectBattery(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        WithInstance(AcerWmiProtocol.BatteryClass, instance =>
        {
            result["getBattInfoInterface"] = DescribeOutputWith(
                instance,
                "GetBattInfoInterface",
                new (string Name, object Value)[]
                {
                    ("uBatteryInfoIndex", 0u),
                    ("uBatteryNo", 1u)
                });

            result["note"] =
                "GetBatteryFunctionData and GetBatteryHealthControlStatus reject every parameter shape " +
                "tried: scalar and array, reserved length one and five. Nitrous does not call them; the " +
                "charge limit is applied with SetBatteryHealthControl and is not read back.";

            return true;
        }, errors, "battery");

        return result;
    }

    /// <summary>
    /// The keyboard backlight auto-off, read-only. Acer's agent reads it with
    /// <c>APGeAction.GetFunction((hotkey &lt;&lt; 8) | 0x80001)</c> and treats the answer's byte 5 as the
    /// timeout in seconds, 0x1E meaning "off after 30 s". The whole raw value is reported, plus
    /// bytes 4, 5 and 6, because one published analysis puts the timeout at byte 6 while the code
    /// here reads byte 5.
    /// </summary>
    private static Dictionary<string, object?> CollectKeyboardAutoOff(List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var hotkey = ReadAcerHotkeyNumber();
        var input = (uint)((hotkey << 8) | 0x80001);

        result["bkHotkeyNumber"] = hotkey;
        result["readInput"] = $"0x{input:X}";

        WithInstance(AcerWmiProtocol.ActionClass, instance =>
        {
            var (value, error) = TryReadRaw(instance, "GetFunction", "uiInput", input, "uiOutput");
            result["getFunctionRaw"] = value;

            if (error != null)
            {
                result["getFunctionError"] = error;
                return true;
            }

            if (value != null &&
                ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
            {
                result["byte4"] = (int)((raw >> 32) & 0xFF);
                result["byte5"] = (int)((raw >> 40) & 0xFF);
                result["byte6"] = (int)((raw >> 48) & 0xFF);
                result["enabled30s"] = ((raw >> 40) & 0xFF) == 0x1E;
            }

            result["note"] =
                "Read-only. The write value is not sent by this probe; it is " +
                "((brightness << 24 | hotkey) << 8) | 0x1E0000080002 to enable and " +
                "((brightness << 24 | hotkey) << 8) | 0x80002 to disable.";

            return true;
        }, errors, "keyboardAutoOff");

        return result;
    }

    /// <summary>The hotkey number Acer's agent uses as the command's second byte.</summary>
    private static int ReadAcerHotkeyNumber()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\OEM\AcerAgentService");

            if (key?.GetValue("BK_Hotkey_Number") is int value && value > 0 && value <= 0xFF)
                return value;
        }
        catch
        {
            // Absent on machines without Acer's agent; the observed default is 132.
        }

        return 132;
    }

    /// <summary>
    /// Every Acer-vendor HID interface on the machine, by usage page. This is what identifies an
    /// unknown model's keyboard and embedded-controller variants from a report alone.
    /// </summary>
    private static List<Dictionary<string, object?>> CollectHidDevices(List<string> errors)
    {
        var devices = new List<Dictionary<string, object?>>();

        try
        {
            foreach (var device in AcerEcHid.ListDiagnosticDevices())
            {
                devices.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["vendorId"] = $"0x{device.VendorId:X4}",
                    ["productId"] = $"0x{device.ProductId:X4}",
                    ["usagePage"] = $"0x{device.UsagePage:X4}",
                    ["usage"] = $"0x{device.Usage:X2}",
                    ["featureLength"] = device.FeatureLength
                });
            }
        }
        catch (Exception ex)
        {
            errors.Add($"hidDevices: {ex.Message}");
        }

        return devices;
    }

    /// <summary>
    /// Every property the method answered, by name. Read-only: the method name must start with Get,
    /// which <see cref="InvokeGetLocked"/> already enforces for the other probes.
    /// </summary>
    private static Dictionary<string, object?> DescribeOutput(
        ManagementObject instance,
        string methodName,
        string inputName,
        uint input)
    {
        var dump = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (!methodName.StartsWith(ReadOnlyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"EC probe is read-only; refusing {methodName}.");

        using var inParams = instance.GetMethodParameters(methodName);
        inParams[inputName] = input;

        using var outParams = instance.InvokeMethod(methodName, inParams, null);
        if (outParams == null)
        {
            dump["<no output object>"] = true;
            return dump;
        }

        foreach (PropertyData property in outParams.Properties)
            dump[property.Name] = DescribeValue(property.Value);

        return dump;
    }

    /// <summary>
    /// Reads one output property without assuming its type. Some Acer methods answer a scalar and
    /// others answer a byte array, and converting the array throws before it can be reported.
    /// </summary>
    private static (string? Value, string? Error) TryReadRaw(
        ManagementObject instance,
        string methodName,
        string inputName,
        uint input,
        string outputName) =>
        TryReadWith(instance, methodName, new (string Name, object Value)[] { (inputName, input) }, outputName);

    /// <summary>
    /// The same, for methods that take several parameters (BatteryControl does). Everything here is
    /// a Get, enforced below.
    /// </summary>
    private static (string? Value, string? Error) TryReadWith(
        ManagementObject instance,
        string methodName,
        (string Name, object Value)[] inputs,
        string outputName)
    {
        try
        {
            if (!methodName.StartsWith(ReadOnlyPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"EC probe is read-only; refusing {methodName}.");

            using var inParams = instance.GetMethodParameters(methodName);
            foreach (var (name, value) in inputs) inParams[name] = value;

            using var outParams = instance.InvokeMethod(methodName, inParams, null);
            if (outParams == null) return (null, "no output object");

            foreach (PropertyData property in outParams.Properties)
            {
                if (string.Equals(property.Name, outputName, StringComparison.OrdinalIgnoreCase))
                    return (DescribeValue(property.Value), null);
            }

            return (null, $"no {outputName} property");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Calls a Get method with the given parameters and returns every output property by name. Used
    /// where the output names are not known in advance, which is the case for BatteryControl.
    /// </summary>
    private static Dictionary<string, object?> DescribeOutputWith(
        ManagementObject instance,
        string methodName,
        (string Name, object Value)[] inputs)
    {
        var dump = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!methodName.StartsWith(ReadOnlyPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"EC probe is read-only; refusing {methodName}.");

            using var inParams = instance.GetMethodParameters(methodName);
            foreach (var (name, value) in inputs) inParams[name] = value;

            using var outParams = instance.InvokeMethod(methodName, inParams, null);
            if (outParams == null)
            {
                dump["<no output object>"] = true;
                return dump;
            }

            foreach (PropertyData property in outParams.Properties)
                dump[property.Name] = DescribeValue(property.Value);
        }
        catch (Exception ex)
        {
            dump["<error>"] = ex.Message;
        }

        return dump;
    }

    /// <summary>A scalar as text, and a byte array as hex, so the layout survives the report.</summary>
    /// <summary>
    /// Renders any output value without throwing. Acer answers scalars, byte arrays and other array
    /// types depending on the method, and a value this cannot convert must still reach the report:
    /// an exception here is reported as the call failing, which is misleading.
    /// </summary>
    private static string? DescribeValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case byte[] bytes:
                return Convert.ToHexString(bytes);

            case Array array:
                return "[" + string.Join(",", array.Cast<object?>().Select(DescribeValue)) + "]";

            default:
                try
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    return value.ToString();
                }
        }
    }

    private static void WithInstance(
        string className,
        Func<ManagementObject, bool> action,
        List<string> errors,
        string section)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                AcerWmiProtocol.WmiNamespace,
                $"SELECT * FROM {className}");

            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    action(instance);
                    return;
                }
            }

            errors.Add($"{section}: {className} has no instance.");
        }
        catch (Exception ex)
        {
            errors.Add($"{section}: {ex.Message}");
        }
    }

    private static ulong? TryGetSysInfo(ManagementObject instance, uint address) =>
        TryInvoke(instance, AcerWmiProtocol.GetGamingSysInfo, "gmInput", address, "gmOutput");

    private static ulong? TryGetMisc(ManagementObject instance, uint address) =>
        TryInvoke(instance, AcerWmiProtocol.GetGamingMiscSetting, "gmInput", address, "gmOutput");

    private static ulong? TryInvoke(
        ManagementObject instance,
        string methodName,
        string inputName,
        uint input,
        string outputName)
    {
        try
        {
            return InvokeGetLocked(instance, methodName, inputName, input, outputName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The single place this type is allowed to call into the firmware, and it only ever
    /// forwards a read method. Anything else throws before WMI sees it.
    /// </summary>
    private static ulong? InvokeGetLocked(
        ManagementObject instance,
        string methodName,
        string inputName,
        uint input,
        string outputName)
    {
        if (!methodName.StartsWith(ReadOnlyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"EC probe is read-only; refusing {methodName}.");

        using var inParams = instance.GetMethodParameters(methodName);
        inParams[inputName] = input;

        using var outParams = instance.InvokeMethod(methodName, inParams, null);
        if (outParams == null) return null;

        foreach (PropertyData property in outParams.Properties)
        {
            if (string.Equals(property.Name, outputName, StringComparison.OrdinalIgnoreCase))
                return property.Value == null
                    ? null
                    : Convert.ToUInt64(property.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string BuildSummary(Dictionary<string, object?> report, List<string> errors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Nitrous EC probe (read-only)");
        sb.AppendLine($"Captured: {report.GetValueOrDefault("capturedAt")}");
        sb.AppendLine();

        if (report.GetValueOrDefault("machine") is Dictionary<string, object?> machine)
        {
            sb.AppendLine("Machine");
            foreach (var (key, value) in machine)
                sb.AppendLine($"  {key,-16} {value}");
            sb.AppendLine();
        }

        if (report.GetValueOrDefault("capabilities") is Dictionary<string, object?> capabilities)
        {
            sb.AppendLine("Capabilities");
            sb.AppendLine($"  channel          : {capabilities.GetValueOrDefault("powerModeChannel")} " +
                          $"(can set power mode: {capabilities.GetValueOrDefault("canSetPowerMode")})");
            sb.AppendLine($"  model profile    : {capabilities.GetValueOrDefault("modelProfile")}");
            sb.AppendLine($"  smbios gaming    : {capabilities.GetValueOrDefault("smbiosGamingVersion")}");
            sb.AppendLine($"  wmi modes        : " +
                          $"[{string.Join(", ", capabilities.GetValueOrDefault("wmiSupportedProfiles") as string[] ?? Array.Empty<string>())}]");
            sb.AppendLine($"  apge action      : {capabilities.GetValueOrDefault("apgeActionFunction")}");
            sb.AppendLine();
        }

        if (report.GetValueOrDefault("classes") is Dictionary<string, object?> classes)
        {
            sb.AppendLine("Acer WMI classes");
            foreach (var (name, value) in classes)
            {
                if (value is not Dictionary<string, object?> entry) continue;

                var present = entry.TryGetValue("present", out var p) && p is true;
                sb.AppendLine($"  {name}: present={present} instance={entry.GetValueOrDefault("instanceName")}");

                if (entry.GetValueOrDefault("methods") is Dictionary<string, object?>[] methods)
                {
                    foreach (var method in methods)
                        sb.AppendLine($"      {method["name"]} (id={method["wmiMethodId"]})");
                }
            }

            sb.AppendLine();
        }

        if (report.GetValueOrDefault("ecHid") is Dictionary<string, object?> ecHid)
        {
            var present = ecHid.TryGetValue("present", out var p) && p is true;
            sb.AppendLine("EC HID interface");
            sb.AppendLine($"  present          : {present}");
            if (present)
            {
                sb.AppendLine($"  device           : {ecHid.GetValueOrDefault("devicePath")}");
                sb.AppendLine($"  ids              : {ecHid.GetValueOrDefault("vendorId")} / " +
                              $"{ecHid.GetValueOrDefault("productId")} usage " +
                              $"{ecHid.GetValueOrDefault("usagePage")}/{ecHid.GetValueOrDefault("usage")}");
                sb.AppendLine($"  version          : {ecHid.GetValueOrDefault("version")}");
                sb.AppendLine($"  mode capability  : {ecHid.GetValueOrDefault("modeCapability")}");
                sb.AppendLine($"  modes            : " +
                              $"[{string.Join(", ", ecHid.GetValueOrDefault("modes") as string[] ?? Array.Empty<string>())}]");
                sb.AppendLine($"  current mode     : {ecHid.GetValueOrDefault("currentMode")}");
            }
            sb.AppendLine();
        }

        if (report.GetValueOrDefault("profiles") is Dictionary<string, object?> profiles)
        {
            var names = profiles.GetValueOrDefault("supportedProfilesNames") as string[]
                        ?? Array.Empty<string>();
            sb.AppendLine("Profiles");
            sb.AppendLine($"  supported bitmap : {profiles.GetValueOrDefault("supportedProfilesBitmap")} " +
                          $"[{string.Join(", ", names)}]");
            sb.AppendLine($"  misc 0x0B        : {profiles.GetValueOrDefault("misc0BValue")} " +
                          "(last value written, not necessarily applied)");
            sb.AppendLine();
        }

        if (report.GetValueOrDefault("genericFunction") is Dictionary<string, object?> generic &&
            generic.Count > 0)
        {
            sb.AppendLine("AcerGenericMethod.GetFunction");
            foreach (var (index, value) in generic)
                sb.AppendLine($"  n={index,-2} -> {FormatRaw(value)}");
            sb.AppendLine();
        }

        sb.AppendLine($"Sections with errors: {errors.Count}");
        foreach (var error in errors)
            sb.AppendLine($"  - {error}");

        return sb.ToString();
    }

    private static string FormatRaw(object? value) => value switch
    {
        ulong number => $"{number} (0x{number:X})",
        null => "unavailable",
        _ => value.ToString() ?? "unavailable"
    };
}
