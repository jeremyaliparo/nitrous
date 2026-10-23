using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

public static class UpdateManager
{
    private const string GithubRepo = "jeremyaliparo/nitrous";
    private const string ReleaseAssetName = "Nitrous.exe";

    public static string CurrentVersion { get; } = GetCurrentVersion();

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly SemaphoreSlim CheckGate = new(1, 1);

    public static Dictionary<string, object?> GetDiagnosticsSnapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["currentVersion"] = CurrentVersion,
            ["updateCheckInProgress"] = CheckGate.CurrentCount == 0
        };

        try
        {
            var executable = Environment.ProcessPath;
            result["executablePath"] = executable;

            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            {
                var info = new FileInfo(executable);
                result["executableBytes"] = info.Length;
                result["fileVersion"] = FileVersionInfo.GetVersionInfo(executable).FileVersion;

                using var stream = File.OpenRead(executable);
                result["sha256"] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

                var directory = info.DirectoryName;
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    result["updateArtifacts"] = Directory
                        .GetFiles(directory, "Nitrous.exe.*")
                        .Select(Path.GetFileName)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
            Logger.Warn("Updater: diagnostics snapshot failed.", ex);
        }

        return result;
    }

    static UpdateManager()
    {
        SharedClient.DefaultRequestHeaders.UserAgent.ParseAdd("NitrousApp/1.0");
    }

    private static string GetCurrentVersion()
    {
        var version = typeof(UpdateManager).Assembly.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    public static async Task CheckForUpdatesAsync(bool silent, Action exitCallback)
    {
        if (!await CheckGate.WaitAsync(0))
        {
            if (!silent)
                MessageBox.Show("An update check is already in progress.", "Update",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            await CheckForUpdatesCoreAsync(silent, exitCallback);
        }
        finally
        {
            CheckGate.Release();
        }
    }

    private static async Task CheckForUpdatesCoreAsync(bool silent, Action exitCallback)
    {
        try
        {
            using var response = await SharedClient.GetAsync(
                $"https://api.github.com/repos/{GithubRepo}/releases/latest");
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            var latestTag = root.TryGetProperty("tag_name", out var tagProperty)
                ? tagProperty.GetString() ?? ""
                : "";
            var releaseUrl = root.TryGetProperty("html_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;
            releaseUrl ??= $"https://github.com/{GithubRepo}/releases/tag/{latestTag}";

            if (!TryNormalizeVersion(latestTag, out var latest) ||
                !TryNormalizeVersion(CurrentVersion, out var current))
            {
                Logger.Warn($"Updater: could not parse versions (current '{CurrentVersion}', latest '{latestTag}').");
                if (!silent)
                    MessageBox.Show($"Could not parse the latest version ('{latestTag}').",
                        "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (latest <= current)
            {
                if (!silent)
                    MessageBox.Show($"Nitrous is up to date! ({CurrentVersion})",
                        "Up to date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                ShowManualUpdate(silent, latestTag, releaseUrl, "The release has no downloadable assets.");
                return;
            }

            var exeAsset = assets.EnumerateArray().FirstOrDefault(asset =>
                string.Equals(GetString(asset, "name"), ReleaseAssetName, StringComparison.OrdinalIgnoreCase));

            if (exeAsset.ValueKind == JsonValueKind.Undefined)
            {
                ShowManualUpdate(silent, latestTag, releaseUrl, "The Nitrous.exe release asset was not found.");
                return;
            }

            var downloadUrl = GetString(exeAsset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                ShowManualUpdate(silent, latestTag, releaseUrl, "The release asset has no download URL.");
                return;
            }

            var expectedHash = await GetExpectedHashAsync(exeAsset, assets, latestTag);
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                // Without code signing or a published digest there is no way to distinguish a
                // tampered binary from a good one. Refuse the silent auto-update and let the
                // user update from the release page.
                ShowManualUpdate(silent, latestTag, releaseUrl,
                    "This release does not publish a SHA-256 digest. Automatic update is disabled.");
                return;
            }

            if (MessageBox.Show(
                    $"New version {latestTag} is available (installed {CurrentVersion}). Update now?",
                    "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

            await PerformUpdateAsync(downloadUrl, expectedHash, releaseUrl, exitCallback);
        }
        catch (Exception ex)
        {
            Logger.Warn("Updater: update check failed.", ex);
            if (!silent)
                MessageBox.Show($"Update check failed: {ex.Message}",
                    "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static async Task<string?> GetExpectedHashAsync(
        JsonElement exeAsset, JsonElement assets, string tag)
    {
        // GitHub's release API exposes a digest for uploaded assets.
        var digest = GetString(exeAsset, "digest");
        if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return digest["sha256:".Length..].Trim();

        // Also accept an explicitly uploaded checksum file.
        var checksumAsset = assets.EnumerateArray().FirstOrDefault(asset =>
        {
            var name = GetString(asset, "name") ?? "";
            return name.Equals("Nitrous.exe.sha256", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("sha256sums.txt", StringComparison.OrdinalIgnoreCase);
        });

        if (checksumAsset.ValueKind == JsonValueKind.Undefined) return null;

        var checksumUrl = GetString(checksumAsset, "browser_download_url");
        if (string.IsNullOrWhiteSpace(checksumUrl)) return null;

        try
        {
            var text = await SharedClient.GetStringAsync(checksumUrl);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;

                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var candidate = tokens[0];
                if (candidate.Length != 64 || !candidate.All(Uri.IsHexDigit)) continue;

                // Accept the single-hash form, but when the file is a standard SHA256SUMS
                // listing, require the entry to be for Nitrous.exe (not another asset).
                if (tokens.Length == 1) return candidate;

                var fileName = tokens[^1].TrimStart('*');
                if (Path.GetFileName(fileName).Equals(ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Updater: could not download the checksum for {tag}.", ex);
        }

        return null;
    }

    private static async Task PerformUpdateAsync(
        string downloadUrl, string expectedHash, string releaseUrl, Action exitCallback)
    {
        string? tempDirectory = null;
        try
        {
            tempDirectory = Path.Combine(Path.GetTempPath(), "Nitrous", $"update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);

            var tempExe = Path.Combine(tempDirectory, ReleaseAssetName);
            Logger.Info($"Updater: downloading {downloadUrl}.");

            using (var response = await SharedClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(tempExe);
                await response.Content.CopyToAsync(file);
            }

            ValidateDownloadedFile(tempExe, expectedHash);

            var currentExe = Application.ExecutablePath;
            var backupExe = currentExe + ".old";

            try
            {
                if (File.Exists(backupExe)) File.Delete(backupExe);

                // Windows allows renaming a running executable, but not overwriting it.
                File.Move(currentExe, backupExe);

                try
                {
                    File.Copy(tempExe, currentExe, overwrite: true);
                    Process.Start(new ProcessStartInfo(currentExe, "--updated") { UseShellExecute = true });
                }
                catch
                {
                    // Roll back so the user still has a working application. Once the new
                    // process has started we no longer roll back, even if the exit callback
                    // fails: deleting the executable under a running process is worse.
                    try
                    {
                        if (File.Exists(currentExe)) File.Delete(currentExe);
                        File.Move(backupExe, currentExe);
                    }
                    catch (Exception rollbackEx)
                    {
                        Logger.Error("Updater: rollback failed; the next launch may need a manual reinstall.", rollbackEx);
                    }

                    throw;
                }

                Logger.Info($"Updater: installed {expectedHash[..8]}… and started the new process.");

                try
                {
                    exitCallback();
                }
                catch (Exception ex)
                {
                    // The replacement is already running. Failing to close the old process is
                    // not an update failure; the new instance waits for the mutex and takes over
                    // once this process exits.
                    Logger.Warn("Updater: could not close the previous process cleanly.", ex);
                }

                return;
            }
            catch (Exception ex)
            {
                Logger.Error("Updater: could not replace the running executable.", ex);
                MessageBox.Show(
                    $"Automatic update failed: {ex.Message}\n\nPlease download the latest release manually:\n{releaseUrl}",
                    "Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Updater: update failed.", ex);
            MessageBox.Show($"Update failed: {ex.Message}\n\nYou can download the latest release from:\n{releaseUrl}",
                "Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (tempDirectory != null)
            {
                try { Directory.Delete(tempDirectory, recursive: true); } catch { }
            }
        }
    }

    private static void ValidateDownloadedFile(string path, string expectedHash)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1024)
            throw new InvalidDataException("The downloaded update is empty or too small.");

        using (var stream = File.OpenRead(path))
        {
            var header = new byte[2];
            if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidDataException("The downloaded file is not a Windows executable.");
        }

        string actualHash;
        using (var stream = File.OpenRead(path))
            actualHash = Convert.ToHexString(SHA256.HashData(stream));

        if (!string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Checksum mismatch (expected {expectedHash}, got {actualHash}). The update was discarded.");
    }

    private static void ShowManualUpdate(bool silent, string tag, string releaseUrl, string reason)
    {
        Logger.Warn($"Updater: {reason} (release {tag}).");

        if (silent) return;

        var result = MessageBox.Show(
            $"{reason}\n\nOpen the {tag} release page to update manually?",
            "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Warn("Updater: could not open the release page.", ex); }
        }
    }

    private static bool TryNormalizeVersion(string value, out Version version)
    {
        value = value.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];

        if (Version.TryParse(value, out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value)
            ? value.GetString()
            : null;

    /// <summary>Removes the previous executable left behind by an in-place update.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            var oldExe = Application.ExecutablePath + ".old";
            if (File.Exists(oldExe)) File.Delete(oldExe);
        }
        catch (Exception ex)
        {
            Logger.Warn("Updater: could not remove the old executable.", ex);
        }
    }
}
