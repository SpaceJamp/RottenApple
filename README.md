# RottenApple

A small GUI for backing up, restoring and updating iPhones and iPads on Linux.
It is a front end for [libimobiledevice](https://libimobiledevice.org/) and
[idevicerestore](https://github.com/libimobiledevice/idevicerestore), built with
[Avalonia](https://avaloniaui.net/) on .NET 10.

RottenApple runs on **Wayland only**. X11 sessions are not supported, and the app exits
with a message if it cannot find a Wayland session. Your compositor needs `xdg_wm_base`
version 3 or later, which GNOME, KDE Plasma and wlroots-based compositors such as Sway
and Hyprland all provide.

The icon is a parody: a rotten, wormy take on a famous bitten apple. RottenApple is not
affiliated with or endorsed by Apple Inc.

## Features

- List connected devices (name, iOS version, UDID)
- Pair (trust) a device
- Full backup to a folder you choose
- Restore a backup, including onto a different device
- Update to the latest signed iOS, keeping data
- Erase & reinstall the latest signed iOS (you must type `ERASE` to confirm)
- Update or erase a device that is stuck in recovery or DFU mode: leave no device selected
- Progress bar instead of a flood of redrawn progress lines
- Plain-English hints for common idevicerestore and idevicebackup2 errors
- A timestamped log file for every job in `~/.local/state/rottenapple/`, ready to attach to a bug report

## Install

Run the installer. It detects Arch or Fedora (and derivatives), installs the .NET SDK and
the device tools, builds the app into `~/.local/share/rottenapple`, adds a `rottenapple`
command and an app menu entry, starts `usbmuxd`, and checks each tool:

```bash
./install.sh
```

If your distro has no `idevicerestore` package, the installer builds it from source,
together with the libimobiledevice libraries it needs, into `~/.local`. System packages
are not touched. To force that build, for example because your distro's version is too
old for your device:

```bash
./install.sh --build-idevicerestore
```

Other options: `--dry-run` shows what would run without changing anything, and `--verbose`
also prints debug lines on screen. Every run writes a full timestamped log to
`~/.local/state/rottenapple/install-<date>-<time>.log`.

To install by hand instead:

Arch:

```bash
sudo pacman -S usbmuxd libimobiledevice idevicerestore dotnet-sdk
```

Fedora:

```bash
sudo dnf install usbmuxd libimobiledevice-utils idevicerestore dotnet-sdk-10.0
```

Then start the USB daemon:

```bash
sudo systemctl start usbmuxd
```

## Run

After `./install.sh`, start **RottenApple** from your app menu, or:

```bash
rottenapple
```

Without installing, from the repository:

```bash
dotnet run --project src/RottenApple
```

Plug in the device, unlock it, click **Pair**, and tap **Trust** on the device.

## Limits

- Encrypted backups are not supported yet.
- Turn off Find My before restoring a backup.
- Update iOS and Erase need an `idevicerestore` version that supports `-y`.
- RottenApple does not fix bugs inside idevicerestore itself. Restore failures on specific
  devices (for example new iPhones or Apple silicon Macs) need fixes upstream.

## Tests

```bash
dotnet test
```

## Project layout

- `src/RottenApple/Core.cs`: device logic with no UI (tool lookup, device list, progress and error parsing)
- `src/RottenApple/MainWindow.axaml(.cs)`: the window and the job runner
- `src/RottenApple/Dialogs.cs`: confirm, info and text-input dialogs
- `src/RottenApple/Assets/rottenapple.svg`: the icon (the PNG is rendered from it with
  `rsvg-convert -w 256 -h 256 rottenapple.svg -o rottenapple.png`)
- `tests/RottenApple.Tests`: xUnit tests for `Core`
- `install.sh`: installer for Arch and Fedora

## AI usage disclosure

[!IMPORTANT]
**This project was written with the help of an AI assistant: Claude (Anthropic), working
through Claude Code. The GUI, the installer, the tests and this README were generated
by AI from the maintainer's instructions. The code has been checked with unit tests and
simulated installer runs, but has not yet been tested on real Arch or Fedora systems or
with real devices. Read `install.sh` before you run it, since it uses `sudo`. Issues and
pull requests are welcome.**
