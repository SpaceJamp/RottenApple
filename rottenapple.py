#!/usr/bin/env python3
"""RottenApple: a small Tk front end for libimobiledevice backup/restore and idevicerestore updates."""
import os
import queue
import shutil
import subprocess
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

TOOLS = ["idevice_id", "ideviceinfo", "idevicepair", "idevicebackup2", "idevicerestore"]


def run_quiet(*cmd):
    """Return stdout of a short command, or '' if it fails or is missing."""
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=15).stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        return ""


def list_devices():
    """Return [(udid, label)] for connected devices."""
    devices = []
    for line in run_quiet("idevice_id", "-l").splitlines():
        if not line.strip():
            continue
        udid = line.split()[0]
        name = run_quiet("ideviceinfo", "-u", udid, "-k", "DeviceName") or "Unknown (pair first)"
        version = run_quiet("ideviceinfo", "-u", udid, "-k", "ProductVersion") or "?"
        devices.append((udid, f"{name}  iOS {version}  [{udid}]"))
    return devices


def is_backup_dir(path):
    """idevicebackup2 writes one folder per device UDID holding Info.plist and Manifest.plist."""
    return all(os.path.isfile(os.path.join(path, f)) for f in ("Info.plist", "Manifest.plist"))


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("RottenApple")
        self.geometry("760x480")
        self.out = queue.Queue()  # worker threads push str (log text) or callables (run on the Tk thread)
        self.busy = False
        self.devices = []

        top = ttk.Frame(self, padding=8)
        top.pack(fill="x")
        self.combo = ttk.Combobox(top, state="readonly")
        self.combo.pack(side="left", fill="x", expand=True)
        self.refresh_btn = ttk.Button(top, text="Refresh", command=self.refresh)
        self.refresh_btn.pack(side="left", padx=(4, 0))

        row = ttk.Frame(self, padding=(8, 0, 8, 8))
        row.pack(fill="x")
        self.buttons = [self.refresh_btn]
        for text, command in (("Pair", self.pair), ("Back up", self.backup),
                              ("Restore backup", self.restore), ("Update iOS", self.update_ios)):
            button = ttk.Button(row, text=text, command=command)
            button.pack(side="left", padx=(0, 4))
            self.buttons.append(button)

        body = ttk.Frame(self, padding=(8, 0, 8, 8))
        body.pack(fill="both", expand=True)
        self.log = tk.Text(body, state="disabled", wrap="word", height=20)
        scroll = ttk.Scrollbar(body, command=self.log.yview)
        self.log.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        self.log.pack(side="left", fill="both", expand=True)

        self.protocol("WM_DELETE_WINDOW", self.on_close)
        missing = [t for t in TOOLS if not shutil.which(t)]
        if missing:
            self.write("Missing tools: " + ", ".join(missing)
                       + "\nArch: sudo pacman -S usbmuxd libimobiledevice idevicerestore"
                       + "\nFedora: sudo dnf install usbmuxd libimobiledevice-utils idevicerestore\n")
        self.after(100, self.drain)
        self.refresh()

    def write(self, text):
        self.log.configure(state="normal")
        self.log.insert("end", text)
        self.log.see("end")
        self.log.configure(state="disabled")

    def drain(self):
        while not self.out.empty():
            item = self.out.get()
            item() if callable(item) else self.write(item)
        self.after(100, self.drain)

    def set_busy(self, busy):
        self.busy = busy
        for button in self.buttons:
            button.configure(state="disabled" if busy else "normal")

    def refresh(self):
        def work():
            devices = list_devices()
            self.out.put(lambda: self.set_devices(devices))
        threading.Thread(target=work, daemon=True).start()

    def set_devices(self, devices):
        self.devices = devices
        self.combo["values"] = [label for _, label in devices]
        self.combo.current(0) if devices else self.combo.set("No device found. Plug in, unlock, tap Trust.")

    def udid(self):
        i = self.combo.current()
        if i < 0:
            messagebox.showwarning("RottenApple", "No device selected.")
            return None
        return self.devices[i][0]

    def run(self, *cmd):
        """Stream a long command's output into the log on a worker thread."""
        self.set_busy(True)
        self.write("\n$ " + " ".join(cmd) + "\n")

        def work():
            try:
                # text mode turns the tools' \r progress updates into separate lines
                proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                        stdin=subprocess.DEVNULL, text=True, bufsize=1)
                for line in proc.stdout:
                    self.out.put(line)
                self.out.put(f"[finished, exit code {proc.wait()}]\n")
            except OSError as e:
                self.out.put(f"[error] {e}\n")
            self.out.put(self.finish)
        threading.Thread(target=work, daemon=True).start()

    def finish(self):
        self.set_busy(False)
        self.refresh()

    def pair(self):
        udid = self.udid()
        if udid:
            self.run("idevicepair", "-u", udid, "pair")

    def backup(self):
        udid = self.udid()
        folder = udid and filedialog.askdirectory(title="Folder to store backups in")
        if folder:
            self.run("idevicebackup2", "-u", udid, "backup", "--full", folder)

    def restore(self):
        udid = self.udid()
        folder = udid and filedialog.askdirectory(title="Pick the backup folder (named after a device UDID)")
        if not folder:
            return
        if not is_backup_dir(folder):
            messagebox.showerror("RottenApple", "That folder is not an idevicebackup2 backup.\n"
                                 "Pick the UDID-named folder inside your backup location.")
            return
        if messagebox.askyesno("Restore backup", "This overwrites data on the device.\n"
                               "Turn off Find My first. Do not unplug until it finishes.\n\nContinue?",
                               icon="warning"):
            # -s lets a backup from one device restore onto another
            self.run("idevicebackup2", "-u", udid, "-s", os.path.basename(folder),
                     "restore", "--system", "--settings", "--reboot", os.path.dirname(folder))

    def update_ios(self):
        udid = self.udid()
        if udid and messagebox.askyesno("Update iOS", "Download and install the latest signed iOS.\n"
                                        "Data is kept, but back up first. Do not unplug until it finishes.\n\n"
                                        "Continue?", icon="warning"):
            # -y: no stdin prompts, the dialog above is the confirmation
            self.run("idevicerestore", "-u", udid, "--latest", "-y")

    def on_close(self):
        if not self.busy or messagebox.askyesno(
                "RottenApple", "A job is still running. Closing may leave the device half-written.\n\nQuit anyway?",
                icon="warning"):
            self.destroy()


if __name__ == "__main__":
    App().mainloop()
