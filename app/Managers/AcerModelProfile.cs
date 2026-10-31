using System;
using System.Collections.Generic;

namespace Nitrous.Managers;

/// <summary>
/// Per-model deviations that cannot be probed.
///
/// Almost everything Nitrous needs it asks the machine for: which WMI methods exist, whether the
/// embedded controller has a HID interface, which modes that interface lists, and what the SMBIOS
/// gaming version is. This table holds only the cases where firmware answers plausibly and is
/// wrong, so it stays short on purpose.
///
/// A model that is not listed gets <see cref="Unknown"/>, which assumes nothing beyond the
/// protocol itself.
/// </summary>
internal sealed record AcerModelProfile(string? ModelCode, bool RequiresBalancedModeReset)
{
    /// <summary>No known deviation: every decision comes from probing.</summary>
    public static AcerModelProfile Unknown { get; } = new(null, RequiresBalancedModeReset: false);

    private static readonly Dictionary<string, AcerModelProfile> Models =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Measured: the mode is set through the EC HID interface, and writing Balanced first
            // makes no difference to the enforced limit. Recorded so the WMI fallback never
            // inserts an extra write on this model.
            ["AN16S-61"] = new("AN16S-61", RequiresBalancedModeReset: false)
        };

    /// <summary>
    /// The profile for a model name, matched on a whole model code so "AN16S-61" does not match
    /// "AN16S-611".
    /// </summary>
    public static AcerModelProfile For(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return Unknown;

        foreach (var (code, profile) in Models)
        {
            if (ContainsCode(model, code)) return profile;
        }

        return Unknown;
    }

    private static bool ContainsCode(string model, string code)
    {
        var at = model.IndexOf(code, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var before = at == 0 || !char.IsLetterOrDigit(model[at - 1]);
            var end = at + code.Length;
            var after = end >= model.Length || !char.IsLetterOrDigit(model[end]);
            if (before && after) return true;

            at = model.IndexOf(code, at + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
