# RottenApple

A small GUI for backing up, restoring and updating iPhones and iPads on Linux.
It is a front end for [libimobiledevice](https://libimobiledevice.org/) and
[idevicerestore](https://github.com/libimobiledevice/idevicerestore).

## Features

- List connected devices (name, iOS version, UDID)
- Pair (trust) a device
- Full backup to a folder you choose
- Restore a backup, including onto a different device
- Update to the latest signed iOS, keeping data

## Install

Arch:

```bash
sudo pacman -S usbmuxd libimobiledevice idevicerestore python tk
```

Fedora:

```bash
sudo dnf install usbmuxd libimobiledevice-utils idevicerestore python3-tkinter
```

Then start the USB daemon:

```bash
sudo systemctl enable --now usbmuxd
```

## Run

```bash
python3 rottenapple.py
```

Plug in the device, unlock it, click **Pair**, and tap **Trust** on the device.

## Limits

- Encrypted backups are not supported yet.
- Turn off Find My before restoring a backup.
- Update iOS needs an `idevicerestore` version that supports `-y`.

## Tests

```bash
python3 -m unittest test_rottenapple
```
