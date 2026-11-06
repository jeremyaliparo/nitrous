using System;
using System.Reflection;

namespace Nitrous.Helpers;

/// <summary>
/// The version as it should be shown to a person.
///
/// <see cref="AssemblyName.Version"/> is numeric only, so a test build would report itself as a
/// plain "0.10.0" and a tester could not tell which build they were running. The prerelease suffix
/// survives only in <c>AssemblyInformationalVersion</c>, which also carries "+&lt;sha&gt;" build
/// metadata that is trimmed here.
/// </summary>
public static class AppVersion
{
    public static string Display { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = typeof(AppVersion).Assembly.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
