using Nitrous.Managers;

namespace Nitrous.Helpers;

/// <summary>
/// Presentation of a temperature. Everything is measured and stored in Celsius; this only
/// decides what the user reads, so switching units never rewrites a sensor, limit or curve.
/// </summary>
internal static class TemperatureDisplay
{
    public const int Celsius = 0;
    public const int Fahrenheit = 1;

    public static bool UseFahrenheit => SettingsManager.Get("TemperatureUnit", Celsius) == Fahrenheit;

    public static string Unit => UseFahrenheit ? "\u00b0F" : "\u00b0C";

    /// <summary>The same temperature in the chosen unit.</summary>
    public static int Convert(int celsius) =>
        UseFahrenheit ? (int)Math.Round(celsius * 9.0 / 5.0 + 32.0) : celsius;

    /// <summary>A temperature step, so a 5 °C snap step reads as 9 °F rather than 5 °F.</summary>
    public static int Delta(int celsius) =>
        UseFahrenheit ? (int)Math.Round(celsius * 9.0 / 5.0) : celsius;

    /// <summary>A reading with its unit; zero or less is the placeholder.</summary>
    public static string Reading(int celsius) =>
        celsius > 0 ? $"{Convert(celsius)} {Unit}" : $"-- {Unit}";

    /// <summary>A value on an axis that already carries the degree sign.</summary>
    public static string Value(int celsius) => $"{Convert(celsius)}\u00b0";

    /// <summary>A height on an axis that has no room for a unit: "72°".</summary>
    public static string Axis(int celsius) => Value(celsius);

    /// <summary>A compact value with its unit: "72°C".</summary>
    public static string Short(int celsius) => $"{Convert(celsius)}{Unit}";
}
