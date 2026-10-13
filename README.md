# Echo Music Windows Launcher (experimental)

This project packages a Windows `.exe` launcher that downloads the latest APK asset from the official Echo Music GitHub release, installs/updates it using Android Debug Bridge (ADB), and asks Android to launch it.

## Important limitations

- This is **not a native Windows port** of Echo Music.
- An Android runtime/emulator (for example, BlueStacks) must already be installed and running.
- ADB must be enabled and the emulator/device must be visible to ADB.
- The launcher does not bundle Android or the APK. It downloads the latest APK at runtime from the official GitHub release.
- The current implementation expects the official release to publish an APK asset and uses package ID `echo.music.iad1tya`.

## Build the EXE on GitHub

1. Create a GitHub repository and upload these files to its `main` branch.
2. Open **Actions** and choose **Build Echo Music Windows Launcher**.
3. Click **Run workflow**.
4. When it completes, open the run and download the `EchoMusicLauncher-Windows-x64` artifact. Extract it to get `EchoMusicLauncher.exe`.

## Run

1. Install and start an Android runtime/emulator.
2. Enable its ADB access.
3. Ensure `adb.exe` is on PATH, or connect the runtime using its own ADB tool.
4. Run `EchoMusicLauncher.exe`.

For BlueStacks, the ADB address/port varies by configuration. If necessary, run `adb connect 127.0.0.1:5555` in PowerShell, replacing the port with the one configured by your emulator.

## Security note

The launcher downloads and installs the APK from the official `EchoMusicApp/Echo-Music` GitHub release. Review the source before running it.
