# Echo Music Windows Launcher

A Windows single-file launcher for the Android edition of Echo Music. It automatically sets up an Android tablet emulator on first run, downloads the latest official APK, and opens it in a landscape tablet window sized to fit the screen.

## What the EXE does

- No Android Studio, BlueStacks, or manual ADB installation is required.
- On first run, it asks you to review the Android SDK licence and then downloads official Android command-line tools, emulator components, the Android 36 Google APIs x86-64 system image, and a Java 21 runtime only if a usable Java 17+ runtime is not already installed.
- It creates a 1920 × 1200 landscape tablet virtual device, scales its window to fit your screen, installs/updates Echo Music, then launches it.
- Later launches reuse the downloaded SDK and virtual device. The launcher checks for the latest official Echo Music release each time.

## Requirements and important limitations

- First-run setup needs internet access and several GB of disk space (approximately 2–3 GB of downloads, depending on SDK package updates).
- CPU virtualization must be enabled in BIOS/UEFI and supported by Windows. The Android emulator is resource-intensive.
- The EXE contains the self-contained .NET launcher, but does not bundle the Android emulator or system image. Those are downloaded on first run from official sources. This is a self-provisioning bootstrapper, not an offline all-in-one installer.
- Echo Music remains an Android app running in an emulator; this is not a native Windows port.
- The launcher currently expects package ID `echo.music.iad1tya` and an APK asset in the official [Echo Music releases](https://github.com/EchoMusicApp/Echo-Music/releases).

## Download

Use the repository's **Releases** section and download `EchoMusicLauncher.exe` directly from a release named **Echo Music Windows Launcher**. GitHub Actions also publishes a raw EXE artifact (not a ZIP) for each build. The workflow additionally publishes the launcher payload to GitHub Packages / GHCR.

## Build automation

The GitHub Actions workflow checks the latest stable upstream Echo Music release daily and builds/publishes when the upstream version changes. It can also be run manually from **Actions → Build Echo Music Windows Launcher**.

## Optional Authenticode signing

Unsigned executables from a new project may trigger Chrome/Edge download warnings or Windows SmartScreen's “unrecognized app” prompt. This warning cannot be reliably disabled by changing the EXE's author/description metadata, and code signing does not guarantee SmartScreen will immediately stop warning.

For legitimate publisher identification, obtain a trusted Authenticode code-signing certificate for the publisher and export its PFX. Add these repository **Actions secrets**:
- `WINDOWS_SIGNING_CERT_BASE64`: Base64 text of the PFX file.
- `WINDOWS_SIGNING_CERT_PASSWORD`: Password protecting the PFX.

The release workflow will sign and verify the EXE before publishing when both secrets are present. Never commit a PFX or its password to the repository. A self-signed certificate will not establish public Windows trust, and a newly signed app can still receive a reputation warning.

## Android SDK terms

The first-run dialog links to the [Android SDK License Agreement](https://developer.android.com/studio/terms). The command-line-tools download is SHA-256 checked against Google's published checksum before extraction. Review and accept the SDK terms before proceeding.
