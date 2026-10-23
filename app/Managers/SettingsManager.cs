using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Microsoft.Win32;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

public static class SettingsManager
{
    private const string RegPath = @"Software\Nitrous";

    public static void Save(string key, object value)
    {
        try
        {
            using RegistryKey regKey = Registry.CurrentUser.CreateSubKey(RegPath);
            regKey.SetValue(key, value);
        }
        catch (UnauthorizedAccessException ex) { Logger.Warn($"Settings: access denied saving '{key}'.", ex); }
        catch (SecurityException ex) { Logger.Warn($"Settings: security restriction saving '{key}'.", ex); }
        catch (Exception ex) { Logger.Warn($"Settings: failed to save '{key}'.", ex); }
    }

    public static T Get<T>(string key, T defaultValue)
    {
        try
        {
            using RegistryKey? regKey = Registry.CurrentUser.OpenSubKey(RegPath);
            object? val = regKey?.GetValue(key);
            if (val != null) return (T)Convert.ChangeType(val, typeof(T))!;
        }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (Exception ex) { Logger.Warn($"Settings: failed to read '{key}'.", ex); }

        return defaultValue;
    }

    public static string[] GetValueNames()
    {
        try
        {
            using RegistryKey? regKey = Registry.CurrentUser.OpenSubKey(RegPath);
            return regKey?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            Logger.Warn("Settings: failed to enumerate values.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Returns only Nitrous' own registry values for the diagnostics export. This deliberately
    /// does not expose the rest of HKCU.
    /// </summary>
    public static Dictionary<string, object?> GetAllValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? regKey = Registry.CurrentUser.OpenSubKey(RegPath);
            if (regKey == null) return values;

            foreach (var name in regKey.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var value = regKey.GetValue(name);
                if (value != null) values[name] = value;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Settings: failed to read values for diagnostics.", ex);
        }

        return values;
    }

    public static void Delete(string key)
    {
        try
        {
            using RegistryKey? regKey = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            regKey?.DeleteValue(key, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Settings: failed to delete '{key}'.", ex);
        }
    }
}
