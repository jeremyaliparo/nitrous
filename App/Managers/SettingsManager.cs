using System;
using Microsoft.Win32;
namespace Nitrous.Managers;

public static class SettingsManager
{
    private const string RegPath = @"Software\Nitrous";

    public static void Save(string key, object value)
    {
        using RegistryKey regKey = Registry.CurrentUser.CreateSubKey(RegPath);

        regKey.SetValue(key, value);
    }

    public static T Get<T>(string key, T defaultValue)
    {
        try
        {
            using RegistryKey? regKey = Registry.CurrentUser.OpenSubKey(RegPath);

            object? val = regKey?.GetValue(key);

            if (val != null) return (T)Convert.ChangeType(val, typeof(T))!;
        }
        catch { }

        return defaultValue;
    }
}
