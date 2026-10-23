using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nitrous.Diagnostics;

/// <summary>
/// Removes local identifying data from the diagnostics bundle. The exporter is opt-in, but
/// users commonly attach the ZIP to a public issue, so the bundle must be safe by default.
/// </summary>
public static class DiagnosticsRedactor
{
    private static readonly Regex DomainUserPattern = new(
        @"\b[^\s\\]+\\" + Regex.Escape(Environment.UserName) + @"\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UserPathPattern = new(
        @"\b[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SidPattern = new(
        @"\bS-1-5-21-\d+(?:-\d+){3,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Ipv4Pattern = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b",
        RegexOptions.Compiled);

    private static readonly Regex MacAddressPattern = new(
        @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b",
        RegexOptions.Compiled);

    private static readonly Regex Ipv6Pattern = new(
        @"\b(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}\b|" +
        @"\b(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4})?\b",
        RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = text;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

        // Remove whole user paths before the domain\user rule, otherwise "C:\Users\name"
        // would be partially rewritten as "C:\<domain>\<user>".
        result = UserPathPattern.Replace(result, "%USERPROFILE%");

        if (!string.IsNullOrWhiteSpace(Environment.UserName))
        {
            result = DomainUserPattern.Replace(result, @"<domain>\<user>");
        }

        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
        {
            result = Regex.Replace(
                result,
                @"\b" + Regex.Escape(Environment.MachineName) + @"\b",
                "<host>",
                RegexOptions.IgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(Environment.UserName))
        {
            result = Regex.Replace(
                result,
                @"\b" + Regex.Escape(Environment.UserName) + @"\b",
                "<user>",
                RegexOptions.IgnoreCase);
        }

        result = SidPattern.Replace(result, "<sid>");
        result = Ipv4Pattern.Replace(result, "<ip>");
        result = Ipv6Pattern.Replace(result, "<ipv6>");
        result = MacAddressPattern.Replace(result, "<mac>");
        result = EmailPattern.Replace(result, "<email>");

        return result;
    }

    /// <summary>Recursively redacts string values before they are serialized to JSON.</summary>
    public static object? RedactObject(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return Redact(text);
            case IDictionary<string, object?> dictionary:
                return dictionary.ToDictionary(
                    pair => pair.Key,
                    pair => RedactObject(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            case IEnumerable<object?> enumerable:
                return enumerable.Select(RedactObject).ToList();
            case IEnumerable enumerable:
                return enumerable.Cast<object?>().Select(RedactObject).ToList();
            default:
                return value;
        }
    }
}
