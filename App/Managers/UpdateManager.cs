using System;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using System.Diagnostics;

namespace Nitrous.Managers;

public static class UpdateManager
{
    public const string CurrentVersion = "0.4.0";
    private const string GithubRepo = "jeremyaliparo/nitrous";

    public static async Task CheckForUpdatesAsync(bool silent, Action exitCallback)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NitrousApp/1.0");
            string res = await client.GetStringAsync($"https://api.github.com/repos/{GithubRepo}/releases/latest");
            string latestTag = JsonDocument.Parse(res).RootElement.GetProperty("tag_name").GetString() ?? "";

            if (Version.TryParse(latestTag, out Version? vLatest) && Version.TryParse(CurrentVersion, out Version? vCurrent))
            {
                if (vLatest > vCurrent) // Only prompt if GitHub is strictly mathematically greater
                {
                    if (MessageBox.Show($"New version ({latestTag}) is available! Update now?", "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        await PerformUpdateAsync(latestTag, exitCallback);
                    }
                }
                else if (!silent)
                {
                    MessageBox.Show($"Nitrous is up to date! ({CurrentVersion})", "Up to date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show($"Update check failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static async Task PerformUpdateAsync(string tag, Action exitCallback)
    {
        try
        {
            string dlUrl = $"https://github.com/{GithubRepo}/releases/download/{tag}/Nitrous.exe";
            string tempExe = Path.Combine(Path.GetTempPath(), "Nitrous_new.exe");
            string currentExe = Application.ExecutablePath;

            using (var client = new HttpClient())
            {
                await File.WriteAllBytesAsync(tempExe, await client.GetByteArrayAsync(dlUrl));
            }

            string cmd = $"/c timeout /t 2 /nobreak & move /y \"{tempExe}\" \"{currentExe}\" & start \"\" \"{currentExe}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
            exitCallback.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
