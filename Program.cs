using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    private const string RepoApi = "https://api.github.com/repos/EchoMusicApp/Echo-Music/releases/latest";
    private const string PackageId = "echo.music.iad1tya";

    [STAThread]
    private static async Task Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var adb = FindAdb();
            if (adb is null)
            {
                MessageBox.Show(
                    "No Android Debug Bridge (adb) was found.\n\n" +
                    "Install an Android emulator/runtime such as BlueStacks, enable its ADB option, " +
                    "and install Android platform-tools. Then add adb.exe to PATH and run this launcher again.\n\n" +
                    "This launcher does not include an Android runtime.",
                    "Echo Music Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EchoMusicWindowsLauncher", "1.0"));
            http.Timeout = TimeSpan.FromMinutes(3);

            var releaseJson = await http.GetStringAsync(RepoApi);
            using var releaseDoc = JsonDocument.Parse(releaseJson);
            var release = releaseDoc.RootElement;
            var tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "latest" : "latest";
            var assets = release.GetProperty("assets").EnumerateArray();
            var apkAsset = assets.FirstOrDefault(a =>
                a.TryGetProperty("name", out var name) &&
                name.GetString() is string n &&
                n.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

            if (apkAsset.ValueKind == JsonValueKind.Undefined)
            {
                MessageBox.Show("The latest GitHub release has no APK asset.", "Echo Music Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var apkName = apkAsset.GetProperty("name").GetString() ?? "EchoMusic.apk";
            var apkUrl = apkAsset.GetProperty("browser_download_url").GetString()
                         ?? throw new InvalidOperationException("Release APK URL was missing.");
            var apkPath = Path.Combine(Path.GetTempPath(), "EchoMusic-" + Sanitize(tag) + ".apk");

            using (var response = await http.GetAsync(apkUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(apkPath);
                await response.Content.CopyToAsync(output);
            }

            var devices = await RunAdb(adb, "devices");
            if (!devices.Contains("\tdevice", StringComparison.Ordinal))
            {
                MessageBox.Show(
                    "No Android device/emulator is connected to ADB.\n\n" +
                    "Start your Android runtime (for example BlueStacks), enable ADB, and connect it with:\n" +
                    "adb connect 127.0.0.1:5555\n\n" +
                    "Then run this launcher again.",
                    "Echo Music Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var install = await RunAdb(adb, $"install -r \"{apkPath}\"");
            if (!install.Contains("Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("APK installation failed:\n" + install);

            var launch = await RunAdb(adb, $"shell monkey -p {PackageId} 1");
            if (launch.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Echo Music was installed, but Android could not launch it:\n" + launch);

            MessageBox.Show($"Echo Music {tag} was installed/updated and launched.\n\nAPK: {apkName}",
                "Echo Music Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Echo Music Launcher — error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string? FindAdb()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "adb.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BlueStacks_nxt", "HD-Adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BlueStacks", "HD-Adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<string> RunAdb(string adb, string args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = adb,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            return stderr;
        return stdout + Environment.NewLine + stderr;
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
