using System.Management;

namespace Nitrous.Managers;

public static class SystemInfoManager
{
    private static string? _cachedModel;

    public static string GetSystemModel()
    {
        if (_cachedModel != null) return _cachedModel;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            using var collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    _cachedModel = item["Model"]?.ToString() ?? "Unknown System";
                    return _cachedModel;
                }
            }
        }
        catch { }
        return "Unknown System";
    }
}
