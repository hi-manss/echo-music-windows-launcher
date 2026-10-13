using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static class Program
{
    private const string RepoApi = "https://api.github.com/repos/EchoMusicApp/Echo-Music/releases/latest";
    private const string PackageId = "echo.music.iad1tya";
    private const string AndroidApi = "36";
    private const string SystemImage = "system-images;android-" + AndroidApi + ";google_apis;x86_64";
    private const string CommandToolsUrl = "https://dl.google.com/android/repository/commandlinetools-win-15859902_latest.zip";
    private const string CommandToolsSha256 = "90ae805d20434428bffcb699c290860f19bb5f66a67e6b330067e3de801fb04a";
    private const string JavaDownloadUrl = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jdk/hotspot/normal/eclipse";
    private const string AvdName = "EchoMusicTablet";

    private static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoMusicLauncher");
    private static string SdkRoot => Path.Combine(DataRoot, "android-sdk");
    private static string JavaRoot => Path.Combine(DataRoot, "jdk21");
    private static string LicenseMarker => Path.Combine(DataRoot, ".android-sdk-license-accepted");
    private static string SdkReadyMarker => Path.Combine(DataRoot, ".android-sdk-ready");

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var window = new LauncherStatusWindow();

        window.Shown += async (_, _) =>
        {
            try
            {
                await LaunchAsync(window);
            }
            catch (OperationCanceledException)
            {
                // The user cancelled first-run setup.
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, ex.Message, "Echo Music Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!window.IsDisposed)
                    window.Close();
            }
        };

        Application.Run(window);
    }

    private static async Task LaunchAsync(LauncherStatusWindow window)
    {
        Directory.CreateDirectory(DataRoot);

        if (!File.Exists(LicenseMarker))
        {
            using var consent = new LicenseConsentWindow();
            if (consent.ShowDialog(window) != DialogResult.OK)
                throw new OperationCanceledException();

            File.WriteAllText(LicenseMarker, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EchoMusicWindowsLauncher", "1.1"));

        window.SetStatus("Checking for the latest Echo Music release…");
        var (tag, apkName, apkUrl) = await GetLatestApkAsync(http);

        var downloads = Path.Combine(DataRoot, "downloads");
        Directory.CreateDirectory(downloads);
        var apkPath = Path.Combine(downloads, "EchoMusic-" + Sanitize(tag) + ".apk");
        if (!File.Exists(apkPath) || new FileInfo(apkPath).Length == 0)
        {
            window.SetStatus($"Downloading Echo Music {tag}…");
            await DownloadFileAsync(http, apkUrl, apkPath);
        }

        var javaHome = await EnsureJavaAsync(http, window);
        var sdkManager = await EnsureCommandLineToolsAsync(http, window);
        var processEnvironment = new Dictionary<string, string?>
        {
            ["JAVA_HOME"] = javaHome,
            ["ANDROID_HOME"] = SdkRoot,
            ["ANDROID_SDK_ROOT"] = SdkRoot,
            ["PATH"] = Path.Combine(javaHome, "bin") + Path.PathSeparator +
                      Path.Combine(SdkRoot, "platform-tools") + Path.PathSeparator +
                      (Environment.GetEnvironmentVariable("PATH") ?? "")
        };

        if (!File.Exists(SdkReadyMarker) || !SdkComponentsExist())
        {
            window.SetStatus("Installing Android emulator, ADB, and tablet system image (first run can take a while)…");

            var licenseResult = await RunBatchAsync(
                sdkManager,
                new[] { "--sdk_root=" + SdkRoot, "--licenses" },
                string.Concat(Enumerable.Repeat("y\n", 100)),
                processEnvironment);

            if (licenseResult.ExitCode != 0)
                throw new InvalidOperationException("Android SDK license setup failed:\n" + TrimOutput(licenseResult.Output));

            var installResult = await RunBatchAsync(
                sdkManager,
                new[]
                {
                    "--sdk_root=" + SdkRoot,
                    "--install",
                    "platform-tools",
                    "emulator",
                    "platforms;android-" + AndroidApi,
                    SystemImage
                },
                string.Concat(Enumerable.Repeat("y\n", 100)),
                processEnvironment);

            if (installResult.ExitCode != 0 || !SdkComponentsExist())
                throw new InvalidOperationException(
                    "The Android emulator components could not be installed. Check your internet connection and free disk space, then run the launcher again.\n\n" +
                    TrimOutput(installResult.Output));

            File.WriteAllText(SdkReadyMarker, AndroidApi);
        }

        var adb = Path.Combine(SdkRoot, "platform-tools", "adb.exe");
        var emulator = Path.Combine(SdkRoot, "emulator", "emulator.exe");
        var avdManager = Path.Combine(SdkRoot, "cmdline-tools", "latest", "bin", "avdmanager.bat");

        window.SetStatus("Preparing a landscape tablet profile…");
        await EnsureTabletAvdAsync(avdManager, processEnvironment);

        var device = await FindRunningEmulatorAsync(adb, processEnvironment);
        Process? emulatorProcess = null;
        if (device is null)
        {
            var scale = ComputeEmulatorScale();
            window.SetStatus("Starting the Android tablet…");
            var startInfo = new ProcessStartInfo
            {
                FileName = emulator,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(emulator)!
            };
            startInfo.ArgumentList.Add("-avd");
            startInfo.ArgumentList.Add(AvdName);
            startInfo.ArgumentList.Add("-scale");
            startInfo.ArgumentList.Add(scale.ToString("0.##", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-gpu");
            startInfo.ArgumentList.Add("auto");
            startInfo.ArgumentList.Add("-no-boot-anim");
            startInfo.ArgumentList.Add("-no-audio");
            foreach (var item in processEnvironment)
                startInfo.Environment[item.Key] = item.Value;

            emulatorProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the Android emulator. Check that hardware virtualization is enabled in BIOS/UEFI and Windows.");

            device = await WaitForTabletBootAsync(adb, emulatorProcess, processEnvironment, window);
        }

        window.SetStatus("Installing/updating Echo Music…");
        var install = await RunExecutableAsync(adb,
            new[] { "-s", device, "install", "-r", apkPath }, processEnvironment);
        if (install.ExitCode != 0 || !install.Output.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Echo Music could not be installed in the Android tablet:\n" + TrimOutput(install.Output));

        // Use a landscape tablet logical display, not a phone-size display.
        await RunExecutableAsync(adb, new[] { "-s", device, "shell", "wm", "size", "1920x1200" }, processEnvironment);
        await RunExecutableAsync(adb, new[] { "-s", device, "shell", "wm", "density", "240" }, processEnvironment);

        window.SetStatus("Launching Echo Music in tablet mode…");
        var launch = await RunExecutableAsync(adb,
            new[] { "-s", device, "shell", "monkey", "-p", PackageId, "1" }, processEnvironment);
        if (launch.ExitCode != 0 || launch.Output.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Echo Music was installed, but Android could not open it:\n" + TrimOutput(launch.Output));

        window.SetStatus($"Echo Music {tag} is running in a screen-fitted tablet window.");
        await Task.Delay(900);
        emulatorProcess?.Dispose();
    }

    private static async Task<(string Tag, string ApkName, string ApkUrl)> GetLatestApkAsync(HttpClient http)
    {
        using var response = await http.GetAsync(RepoApi);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var release = document.RootElement;
        var tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "latest" : "latest";

        var apk = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a =>
            a.TryGetProperty("name", out var name) &&
            name.GetString() is string n &&
            n.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

        if (apk.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"The latest Echo Music release ({tag}) does not contain an APK asset.");

        return (
            tag,
            apk.GetProperty("name").GetString() ?? "EchoMusic.apk",
            apk.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidOperationException("The Echo Music APK download URL was missing."));
    }

    private static async Task<string> EnsureJavaAsync(HttpClient http, LauncherStatusWindow window)
    {
        var existing = FindJavaHome();
        if (existing is not null && await IsJava17OrNewerAsync(Path.Combine(existing, "bin", "java.exe")))
            return existing;

        var javaExe = Path.Combine(JavaRoot, "bin", "java.exe");
        if (File.Exists(javaExe) && await IsJava17OrNewerAsync(javaExe))
            return JavaRoot;

        window.SetStatus("Setting up the Java runtime needed by the Android SDK…");
        var zipPath = Path.Combine(DataRoot, "jdk21.zip");
        var extractPath = Path.Combine(DataRoot, "jdk-extract");
        await DownloadFileAsync(http, JavaDownloadUrl, zipPath);

        if (Directory.Exists(extractPath))
            Directory.Delete(extractPath, true);
        Directory.CreateDirectory(extractPath);
        ZipFile.ExtractToDirectory(zipPath, extractPath, true);

        var foundJava = Directory.EnumerateFiles(extractPath, "java.exe", SearchOption.AllDirectories)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(Path.GetDirectoryName(p)), "bin", StringComparison.OrdinalIgnoreCase));
        if (foundJava is null)
            throw new InvalidOperationException("The downloaded Java runtime did not contain bin\\java.exe.");

        var binDir = Path.GetDirectoryName(foundJava)!;
        var extractedHome = Directory.GetParent(binDir)!.FullName;
        if (Directory.Exists(JavaRoot))
            Directory.Delete(JavaRoot, true);
        Directory.Move(extractedHome, JavaRoot);
        Directory.Delete(extractPath, true);
        File.Delete(zipPath);

        if (!await IsJava17OrNewerAsync(javaExe))
            throw new InvalidOperationException("The Java runtime installed by the launcher could not be started.");

        return JavaRoot;
    }

    private static string? FindJavaHome()
    {
        var candidates = new List<string>();
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
            candidates.Add(home);

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "java.exe");
            if (File.Exists(candidate))
                candidates.Add(Directory.GetParent(Path.GetDirectoryName(candidate)!)!.FullName);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "bin", "java.exe")));
    }

    private static async Task<bool> IsJava17OrNewerAsync(string javaExe)
    {
        if (!File.Exists(javaExe))
            return false;

        try
        {
            var result = await RunExecutableAsync(javaExe, new[] { "-version" }, null);
            var match = Regex.Match(result.Output, "version\\s+\"(?<v>[^\"]+)\"");
            if (!match.Success)
                return false;

            var first = match.Groups["v"].Value.Split('.')[0];
            return int.TryParse(first, out var major) && major >= 17;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> EnsureCommandLineToolsAsync(HttpClient http, LauncherStatusWindow window)
    {
        var sdkManager = Path.Combine(SdkRoot, "cmdline-tools", "latest", "bin", "sdkmanager.bat");
        if (File.Exists(sdkManager))
            return sdkManager;

        window.SetStatus("Downloading the official Android command-line tools…");
        Directory.CreateDirectory(SdkRoot);
        var zipPath = Path.Combine(DataRoot, "android-commandlinetools.zip");
        var extractPath = Path.Combine(DataRoot, "android-commandlinetools-extract");
        await DownloadFileAsync(http, CommandToolsUrl, zipPath);

        await using (var stream = File.OpenRead(zipPath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (!string.Equals(hash, CommandToolsSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Android command-line tools download failed its official SHA-256 integrity check.");
        }

        if (Directory.Exists(extractPath))
            Directory.Delete(extractPath, true);
        Directory.CreateDirectory(extractPath);
        ZipFile.ExtractToDirectory(zipPath, extractPath, true);

        var extractedTools = Path.Combine(extractPath, "cmdline-tools");
        var targetTools = Path.Combine(SdkRoot, "cmdline-tools", "latest");
        if (!Directory.Exists(extractedTools))
            throw new InvalidOperationException("The Android command-line tools ZIP did not contain the expected cmdline-tools folder.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetTools)!);
        if (Directory.Exists(targetTools))
            Directory.Delete(targetTools, true);
        Directory.Move(extractedTools, targetTools);
        Directory.Delete(extractPath, true);
        File.Delete(zipPath);

        if (!File.Exists(sdkManager))
            throw new InvalidOperationException("sdkmanager.bat was not found after extracting the Android command-line tools.");

        return sdkManager;
    }

    private static bool SdkComponentsExist() =>
        File.Exists(Path.Combine(SdkRoot, "platform-tools", "adb.exe")) &&
        File.Exists(Path.Combine(SdkRoot, "emulator", "emulator.exe")) &&
        File.Exists(Path.Combine(SdkRoot, "system-images", "android-" + AndroidApi, "google_apis", "x86_64", "system.img"));

    private static async Task EnsureTabletAvdAsync(string avdManager, Dictionary<string, string?> environment)
    {
        var avdHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");
        var avdDir = Path.Combine(avdHome, AvdName + ".avd");
        var configPath = Path.Combine(avdDir, "config.ini");
        if (!File.Exists(configPath))
        {
            var create = await RunBatchAsync(avdManager,
                new[] { "create", "avd", "-n", AvdName, "-k", SystemImage, "-f" },
                "no\n", environment);

            if (create.ExitCode != 0 || !File.Exists(configPath))
                throw new InvalidOperationException("The virtual tablet could not be created:\n" + TrimOutput(create.Output));
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["avd.ini.encoding"] = "UTF-8",
            ["abi.type"] = "x86_64",
            ["hw.device.name"] = "Echo Music Tablet",
            ["hw.lcd.width"] = "1920",
            ["hw.lcd.height"] = "1200",
            ["hw.lcd.density"] = "240",
            ["hw.ramSize"] = "4096",
            ["vm.heapSize"] = "512",
            ["hw.cpu.ncore"] = "4",
            ["hw.keyboard"] = "yes",
            ["hw.gpu.enabled"] = "yes",
            ["hw.gpu.mode"] = "auto",
            ["showDeviceFrame"] = "no"
        };

        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new List<string>();
        foreach (var key in values.Keys)
            lines.RemoveAll(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        lines.AddRange(values.Select(x => x.Key + "=" + x.Value));
        File.WriteAllLines(configPath, lines, new UTF8Encoding(false));
    }

    private static async Task<string?> FindRunningEmulatorAsync(string adb, Dictionary<string, string?> environment)
    {
        var result = await RunExecutableAsync(adb, new[] { "devices" }, environment);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('\t'))
            .Where(parts => parts.Length >= 2 && parts[0].StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)
                            && parts[1] == "device")
            .Select(parts => parts[0])
            .FirstOrDefault();
    }

    private static async Task<string> WaitForTabletBootAsync(
        string adb, Process emulatorProcess, Dictionary<string, string?> environment, LauncherStatusWindow window)
    {
        var deadline = DateTime.UtcNow.AddMinutes(6);
        string? device = null;
        while (DateTime.UtcNow < deadline)
        {
            if (emulatorProcess.HasExited)
                throw new InvalidOperationException(
                    "The Android emulator closed before starting. Make sure CPU virtualization is enabled in BIOS/UEFI and Windows Hypervisor Platform is available.");

            device = await FindRunningEmulatorAsync(adb, environment);
            if (device is not null)
            {
                window.SetStatus("Booting the Android tablet (first boot can take several minutes)…");
                var boot = await RunExecutableAsync(adb, new[] { "-s", device, "shell", "getprop", "sys.boot_completed" }, environment);
                if (boot.Output.Trim().EndsWith("1", StringComparison.Ordinal))
                    return device;
            }

            await Task.Delay(3000);
        }

        throw new TimeoutException("The Android tablet did not finish booting within six minutes. Check that hardware virtualization is enabled and that your PC has enough free memory.");
    }

    private static double ComputeEmulatorScale()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 800);
        var widthScale = Math.Max(640, area.Width - 64) / 1920.0;
        var heightScale = Math.Max(480, area.Height - 100) / 1200.0;
        return Math.Clamp(Math.Min(1.0, Math.Min(widthScale, heightScale)), 0.35, 1.0);
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".download";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true))
                await input.CopyToAsync(output);

            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(partial, destination);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    private static async Task<CommandResult> RunBatchAsync(
        string batchFile, IReadOnlyList<string> args, string? stdin, Dictionary<string, string?>? environment)
    {
        var command = "\"" + batchFile + "\" " + string.Join(" ", args.Select(QuoteCmdArgument));
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /s /c \"" + command + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true
        };
        ApplyEnvironment(startInfo, environment);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync();
        var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);
        return new CommandResult(process.ExitCode, output);
    }

    private static async Task<CommandResult> RunExecutableAsync(
        string executable, IReadOnlyList<string> args, Dictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        ApplyEnvironment(startInfo, environment);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);
        return new CommandResult(process.ExitCode, output);
    }

    private static void ApplyEnvironment(ProcessStartInfo info, Dictionary<string, string?>? environment)
    {
        if (environment is null)
            return;
        foreach (var pair in environment)
        {
            if (pair.Value is not null)
                info.Environment[pair.Key] = pair.Value;
        }
    }

    private static string QuoteCmdArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string TrimOutput(string output)
    {
        output = output.Trim();
        return output.Length <= 6000 ? output : output[^6000..];
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private sealed record CommandResult(int ExitCode, string Output);
}

internal sealed class LauncherStatusWindow : Form
{
    private readonly Label _message;
    private readonly ProgressBar _progress;

    public LauncherStatusWindow()
    {
        Text = "Echo Music";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new System.Drawing.Size(560, 142);

        _message = new Label
        {
            Left = 22,
            Top = 22,
            Width = 516,
            Height = 50,
            Text = "Starting Echo Music…",
            Font = new System.Drawing.Font("Segoe UI", 10F),
            AutoEllipsis = true
        };
        _progress = new ProgressBar
        {
            Left = 22,
            Top = 88,
            Width = 516,
            Height = 18,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28
        };
        Controls.Add(_message);
        Controls.Add(_progress);
    }

    public void SetStatus(string value)
    {
        if (IsDisposed)
            return;
        _message.Text = value;
        _message.Refresh();
    }
}

internal sealed class LicenseConsentWindow : Form
{
    public LicenseConsentWindow()
    {
        Text = "First-run setup";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new System.Drawing.Size(570, 235);

        var text = new Label
        {
            Left = 18,
            Top = 16,
            Width = 534,
            Height = 75,
            Text = "The first run downloads and configures Google Android SDK tools, an Android tablet emulator, and a system image. It may require 2–3 GB of downloads and several GB of free disk space. CPU virtualization must be enabled."
        };
        var licenseLink = new LinkLabel
        {
            Left = 18,
            Top = 96,
            Width = 330,
            Height = 24,
            Text = "Read the Android SDK License Agreement"
        };
        licenseLink.LinkClicked += (_, _) =>
        {
            Process.Start(new ProcessStartInfo("https://developer.android.com/studio/terms") { UseShellExecute = true });
        };

        var check = new CheckBox
        {
            Left = 18,
            Top = 131,
            Width = 530,
            Height = 26,
            Text = "I have reviewed and accept the Android SDK license terms."
        };
        var continueButton = new Button
        {
            Left = 394,
            Top = 183,
            Width = 158,
            Height = 32,
            Text = "Continue",
            DialogResult = DialogResult.OK,
            Enabled = false
        };
        var cancelButton = new Button
        {
            Left = 280,
            Top = 183,
            Width = 100,
            Height = 32,
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };
        check.CheckedChanged += (_, _) => continueButton.Enabled = check.Checked;
        Controls.AddRange(new Control[] { text, licenseLink, check, continueButton, cancelButton });
        AcceptButton = continueButton;
        CancelButton = cancelButton;
    }
}
