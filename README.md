# Echo Music Windows Launcher (experimental)

This repository creates a single-file Windows launcher for the Android edition of Echo Music.

## User experience

- Run `EchoMusicLauncher.exe`; you do not need to install Android Studio, BlueStacks, or ADB manually.
- On first run, the launcher asks you to review and accept the Android SDK license terms, then downloads and configures the official Android command-line tools, emulator, ADB, Java (only if a usable Java 17+ runtime isn't already installed), and an Android tablet system image.
- First-run setup requires internet access and several GB of free disk space (typically around 2–3 GB of downloads). The Android emulator also requires hardware virtualization support to be enabled.
- The launcher downloads the latest official Echo Music APK, creates a landscape tablet virtual device (1920 × 1200 at 240 dpi), scales the emulator window to fit the available desktop screen, installs/updates Echo Music, and launches it.
- SDK files and the virtual device are saved under the current Windows user's Local AppData and reused on later launches.

## Important limitations

- This is an Android app running inside an automatically provisioned emulator, not a native Windows desktop port.
- The EXE includes the self-contained .NET launcher. Android's official emulator/system-image downloads are fetched on first run instead of bundling multiple GB of third-party SDK files into the EXE.
- Hardware virtualization must be enabled. If the emulator exits before starting, check BIOS/UEFI virtualization and Windows hypervisor support.
- The launcher currently uses package ID `echo.music.iad1tya` and the latest APK asset from the official [Echo Music releases](https://github.com/EchoMusicApp/Echo-Music/releases).

## Build the EXE with GitHub Actions

1. Open the repository's **Actions** tab.
2. Run **Build Echo Music Windows Launcher**, or push a change to the launcher source.
3. Download `EchoMusicLauncher-Windows-x64-<upstream-tag>` from the workflow run's **Artifacts** section.

The workflow also publishes a GHCR package to [GitHub Packages](https://github.com/users/hi-manss/packages).

## License and downloads

The first-run dialog links to the [Android SDK License Agreement](https://developer.android.com/studio/terms). Review it and check the acceptance box before allowing the launcher to download SDK components. Android command-line tools are downloaded from Google's official distribution and SHA-256 checked before extraction.

The Java fallback is downloaded from the Eclipse Adoptium API over HTTPS if the system has no usable Java 17+ runtime.
