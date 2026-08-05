<p align="center">
  <img src="assets/app-icon.png" alt="AI Work Status Light icon" width="144">
</p>

<p align="center">
  <a href="README.md">简体中文</a> | <strong>English</strong>
</p>

<h1 align="center">AI Work Status Light</h1>

<p align="center">
  Display Codex or Cursor activity in real time with red, yellow, and green LEDs connected to an ESP32-C3.
</p>

<p align="center">
  <a href="https://github.com/carlbuu/ai-status-light-esp32/actions/workflows/build-release.yml"><img src="https://github.com/carlbuu/ai-status-light-esp32/actions/workflows/build-release.yml/badge.svg" alt="Build and Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License"></a>
</p>

The Windows bridge reads task states through Codex or Cursor Hooks and controls the ESP32-C3 over a USB serial connection. Only one AI platform is enabled at a time, preventing both applications from controlling the light simultaneously.

## Features

- Shows working, permission requested, completed but unread, error, and sleep states.
- Optionally uses solid, double-flash, or triple-flash yellow patterns based on the number of concurrent tasks.
- Supports 5%–100% brightness control, automatic device connection, startup with Windows, and a system tray icon.
- Switches safely between Codex and Cursor while preserving unrelated user Hooks.
- Runs as a single Windows executable or as a portable installation package.

## Requirements

- Windows 10 or Windows 11
- ESP32-C3 development board
- One red, one yellow, and one green LED with suitable current-limiting resistors
- USB cable with data support
- Arduino IDE, required only when flashing the firmware
- Codex or Cursor

## Download

Download the latest version from [GitHub Releases](https://github.com/carlbuu/ai-status-light-esp32/releases/latest):

- `CodexStatusLight-OneClick.exe`: single-file graphical version recommended for most users.
- `CodexStatusLight-portable.zip`: includes the application plus installation and uninstall scripts.

Build outputs are not committed to the Git repository.

## Quick Start

1. Open and flash [`sketch_jul16a/sketch_jul16a.ino`](sketch_jul16a/sketch_jul16a.ino) with Arduino IDE.
2. Set **USB CDC On Boot** to **Enabled** before flashing.
3. Connect the LEDs according to the table below, then close the Arduino IDE Serial Monitor.
4. Download and run `CodexStatusLight-OneClick.exe`.
5. Select Codex or Cursor and click **Apply and Configure**.
6. Fully exit and reopen the selected platform so the Hooks take effect.
7. Connect the ESP32-C3 in the **Device Connection** section and verify the wiring with **Light Test**.

| LED | ESP32-C3 pin | Active level |
| --- | --- | --- |
| Red | GPIO2 | HIGH |
| Yellow | GPIO3 | HIGH |
| Green | GPIO4 | HIGH |

Each LED must use a suitable series resistor and share ground with the ESP32-C3.

## Light Status Reference

| State | Light behavior |
| --- | --- |
| Tasks running, task-count animation disabled | Solid yellow |
| 1 task running, task-count animation enabled | Solid yellow |
| 2 tasks running, task-count animation enabled | Double-flashing yellow |
| 3 or more tasks running, task-count animation enabled | Triple-flashing yellow |
| Any task is waiting for user permission | Flashing green |
| A completed project has not been reviewed | Solid green |
| No running or unreviewed projects | Lights turn off automatically |
| Error, disconnection, or heartbeat timeout | Solid red |
| Computer sleeping or display manually disabled | All lights off |

See the [user guide](docs/user-guide.md) for state priorities, platform differences, and interface instructions.

## Documentation

- [Installation, wiring, and uninstall](docs/installation.md)
- [User guide](docs/user-guide.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Architecture and protocol](docs/architecture.md)
- [Development, build, and release](docs/development.md)
- [Contributing](CONTRIBUTING.md)

## Build

Run this command from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```

The script compiles the Windows application, creates both distribution packages, and runs the built-in self-tests. See the [development documentation](docs/development.md) for details.

## Important Notes

- The application modifies the user-level Hooks configuration for the selected platform. It creates timestamped backups first and preserves Hooks that do not belong to this application.
- To accurately display the permission-request state, Cursor mode makes Shell, MCP, web search, and web reading operations request permission. These Hooks are removed when switching back to Codex.
- Configuration and logs are stored in `%LOCALAPPDATA%\CodexStatusLight`.

## License

This project is licensed under the [MIT License](LICENSE).
