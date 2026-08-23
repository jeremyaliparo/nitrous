using System;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Nitrous.Managers;

public static class UpdateManager
{
    public const string CurrentVersion = "0.6.1";
    private const string GithubRepo = "jeremyaliparo/nitrous";

    private static readonly HttpClient SharedClient = new HttpClient();

    static UpdateManager()
    {
        SharedClient.DefaultRequestHeaders.UserAgent.ParseAdd("NitrousApp/1.0");
    }

    public static async Task CheckForUpdatesAsync(bool silent, Action exitCallback)
    {
        try
        {
            string res = await SharedClient.GetStringAsync($"https://api.github.com/repos/{GithubRepo}/releases/latest");
            string latestTag = JsonDocument.Parse(res).RootElement.GetProperty("tag_name").GetString() ?? "";

            if (Version.TryParse(latestTag, out Version? vLatest) && Version.TryParse(CurrentVersion, out Version? vCurrent))
            {
                if (vLatest > vCurrent)
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

            await File.WriteAllBytesAsync(tempExe, await SharedClient.GetByteArrayAsync(dlUrl));

            string cmd = $"/c timeout /t 2 /nobreak & move /y \"{tempExe}\" \"{currentExe}\" & start \"\" \"{currentExe}\"";

            using var p = Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });

            exitCallback.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
