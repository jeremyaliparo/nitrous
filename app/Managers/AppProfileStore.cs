using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nitrous.Diagnostics;
using Nitrous.Enums;

namespace Nitrous.Managers;

/// <summary>One app-to-power-mode rule. The executable is the file name only; matching ignores case.</summary>
public sealed class AppProfileRule
{
    public string Executable { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public PowerProfile Profile { get; set; } = PowerProfile.Performance;
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string MatchName => Path.GetFileNameWithoutExtension(Executable);
}

/// <summary>
/// The app-profile rule list, stored as one JSON value in the existing settings key. The dashboard
/// writes it; the tray reads it every few seconds, so no cross-process signal is needed.
/// </summary>
public static class AppProfileStore
{
    public const string RulesKey = "AppProfileRules";
    public const string EnabledKey = "AppProfilesEnabled";
    public const string ActiveKey = "AppProfileActive";
    public const string RestoreKey = "AppProfileRestoreMode";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool Enabled => SettingsManager.Get(EnabledKey, 1) == 1;

    public static void SetEnabled(bool enabled) => SettingsManager.Save(EnabledKey, enabled ? 1 : 0);

    public static List<AppProfileRule> Load()
    {
        var json = SettingsManager.Get(RulesKey, "");
        if (string.IsNullOrWhiteSpace(json))
            return new List<AppProfileRule>();

        try
        {
            var rules = JsonSerializer.Deserialize<List<AppProfileRule>>(json, JsonOptions);
            return Sanitize(rules ?? new List<AppProfileRule>());
        }
        catch (JsonException ex)
        {
            Logger.Warn("App profiles: the saved rule list is not valid JSON; ignoring it.", ex);
            return new List<AppProfileRule>();
        }
    }

    public static void Save(IEnumerable<AppProfileRule> rules)
    {
        var clean = Sanitize(rules);
        SettingsManager.Save(RulesKey, JsonSerializer.Serialize(clean, JsonOptions));
    }

    /// <summary>Trims to the executable name, drops empty/duplicate rules and repairs missing fields.</summary>
    public static List<AppProfileRule> Sanitize(IEnumerable<AppProfileRule> rules)
    {
        var clean = new List<AppProfileRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            var executable = Path.GetFileName(rule.Executable ?? "");
            if (string.IsNullOrWhiteSpace(executable) || !seen.Add(executable))
                continue;

            if (!Enum.IsDefined(rule.Profile))
                rule.Profile = PowerProfile.Performance;

            rule.Executable = executable;
            rule.DisplayName = string.IsNullOrWhiteSpace(rule.DisplayName)
                ? Path.GetFileNameWithoutExtension(executable)
                : rule.DisplayName.Trim();

            clean.Add(rule);
        }

        return clean;
    }

    /// <summary>Turbo cannot be selected on a model whose firmware does not expose it.</summary>
    public static PowerProfile ClampForThisMachine(PowerProfile profile) =>
        profile == PowerProfile.Turbo && !AcerWmiManager.IsTurboModeSupported()
            ? PowerProfile.Performance
            : profile;
}
