using System;
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
