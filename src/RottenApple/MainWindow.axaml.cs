using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace RottenApple;

public partial class MainWindow : Window
{
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const int MaxKeptOutput = 1_000_000;  // chars of job output kept for callers to inspect

    List<Core.Device> devices = [];
    bool busy, closeConfirmed;
    volatile Process? job;  // the running tool, so quitting can stop it

    public MainWindow()
    {
        InitializeComponent();
        var missing = Core.Tools.Where(t => Core.FindTool(t) is null).ToList();
        if (missing.Count > 0)
            Write($"Missing tools: {string.Join(", ", missing)}\nRun ./install.sh to install them.\n");
        Closing += OnClosing;
        _ = Guarded(Refresh);
    }

    void Write(string text)
    {
        LogBox.Text = (LogBox.Text ?? "") + text;
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    static void Post(Action action) => Dispatcher.UIThread.Post(action);

    void SetBusy(bool value)
    {
        busy = value;
        foreach (var button in new[] { RefreshButton, PairButton, BackupButton, RestoreButton, EncryptionButton, UpdateButton, EraseButton })
            button.IsEnabled = !value;
    }

    /// <summary>
    /// Run a whole button action (dialogs, pickers, the job and the refresh after it) as one busy section,
    /// so a second action can never start meanwhile, and no exception can crash the app or leave it stuck.
    /// </summary>
    async Task Guarded(Func<Task> action)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            await action();
        }
        catch (Exception e)
        {
            Write($"[error] {e.Message}\n");
        }
        finally
        {
            SetBusy(false);
        }
    }

    void SetProgress(string label, double percent)
    {
        Progress.Value = percent;
        Stage.Text = $"{label} {percent:F0}%".Trim();
    }

    async Task Refresh()
    {
        var previous = SelectedDevice()?.Udid;
        var found = await Task.Run(() => Core.ListDevices());
        devices = found;
        DeviceBox.ItemsSource = found.Select(d => d.Label).ToList();
        if (found.Count > 0)
        {
            // keep the user's choice: device order can change, and jumping to another device before Erase is dangerous
            DeviceBox.SelectedIndex = Math.Max(0, found.FindIndex(d => d.Udid == previous));
            return;
        }
        DeviceBox.PlaceholderText = "No device found. Plug in, unlock, tap Trust.";
        if (!await Task.Run(Core.UsbmuxdRunning))
            Write("usbmuxd is not running. Plug the device in, or run: sudo systemctl start usbmuxd\n");
    }

    Core.Device? SelectedDevice()
    {
        var i = DeviceBox.SelectedIndex;
        return i >= 0 && i < devices.Count ? devices[i] : null;
    }

    async Task<Core.Device?> RequireDevice()
    {
        if (SelectedDevice() is { } device) return device;
        await Dialogs.Info(this, "RottenApple", "No device selected.");
        return null;
    }

    async Task<string?> PickFolder(string title)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// Stream a long command into the log pane and a per-job log file; returns the output, minus progress bars.
    /// env carries secrets such as backup passwords: unlike arguments, other users cannot read them via ps,
    /// and they are never logged.
    /// </summary>
    async Task<string> RunJob(string action, string[] cmd, Dictionary<string, string>? env = null)
    {
        SetProgress("", 0);
        Write($"\n$ {string.Join(" ", cmd)}\n");
        var output = await Task.Run(() => Execute(action, cmd, env));
        await Refresh();
        return output;
    }

    // Runs on a worker thread; all UI changes go through Post.
    string Execute(string action, string[] cmd, Dictionary<string, string>? env)
    {
        var output = new System.Text.StringBuilder();
        // Logs hold UDIDs, device names and paths: owner-only directory and files. CreateNew never follows
        // or truncates an existing file or planted symlink, and the millisecond name keeps jobs apart.
        Directory.CreateDirectory(Core.LogDir, Private | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Core.LogDir, Private | UnixFileMode.UserExecute);
        var path = Path.Combine(Core.LogDir, $"{action}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        Post(() => Write($"Log file: {path}\n"));
        using var log = new StreamWriter(new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = Private,
        })) { AutoFlush = true };
        var gate = new object();
        var logWorks = true;
        void Record(string text)
        {
            lock (gate)
            {
                if (!logWorks) return;
                try
                {
                    log.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");
                }
                catch (IOException e)
                {
                    // e.g. disk full: this runs on a reader thread, where an exception would kill the app mid-job
                    logWorks = false;
                    Post(() => Write($"[warning] log file stopped: {e.Message}\n"));
                }
            }
        }

        Record("$ " + string.Join(" ", cmd));
        var shown = new HashSet<string>();
        (string, int)? lastStep = null;

        // stdout and stderr arrive on separate threads; the lock keeps line handling in order.
        void Handle(string? raw)
        {
            if (raw is null) return;
            var line = Core.Clean(raw);  // no terminal escape sequences into the log file
            lock (gate)
            {
                if (Core.ParseProgress(line) is { } p)
                {
                    Post(() => SetProgress(p.Label, p.Percent));
                    var step = (p.Label, (int)(p.Percent / 10));  // file gets every 10%, not every redraw
                    if (step != lastStep)
                    {
                        lastStep = step;
                        Record($"progress {p.Label} {p.Percent:F1}%");
                    }
                    return;
                }
                if (string.IsNullOrWhiteSpace(line)) return;
                if (output.Length < MaxKeptOutput) output.AppendLine(line);
                Post(() => Write(line + "\n"));
                Record(line);
                if (Core.HintFor(line) is { } hint && shown.Add(hint))
                {
                    Post(() => Write($"HINT: {hint}\n"));
                    Record("HINT: " + hint);
                }
            }
        }

        string result;
        try
        {
            // .NET line readers split on \r too, so each progress redraw arrives as its own line.
            var psi = new ProcessStartInfo(Core.Tool(cmd[0]))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (var arg in cmd.Skip(1)) psi.ArgumentList.Add(arg);
            foreach (var (key, value) in env ?? []) psi.Environment[key] = value;
            using var proc = Process.Start(psi)!;
            job = proc;
            proc.StandardInput.Close();  // tools must never wait on a prompt; dialogs did the confirming
            proc.OutputDataReceived += (_, e) => Handle(e.Data);
            proc.ErrorDataReceived += (_, e) => Handle(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            result = $"[finished, exit code {proc.ExitCode}]";
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            result = $"[error] {cmd[0]}: {e.Message}";
        }
        finally
        {
            job = null;
        }
        Record(result);
        Post(() => Write(result + "\n"));
        return output.ToString();
    }

    async void OnRefresh(object? sender, RoutedEventArgs e) => await Guarded(Refresh);

    async void OnPair(object? sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (await RequireDevice() is { } device)
            await RunJob("pair", ["idevicepair", "-u", device.Udid, "pair"]);
    });

    async void OnBackup(object? sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (await RequireDevice() is not { } device) return;
        // an encrypted backup needs no password here: the device encrypts with the one it already has
        if (await PickFolder("Folder to store backups in") is { } folder)
            await RunJob("backup", ["idevicebackup2", "-u", device.Udid, "backup", "--full", folder]);
    });

    async void OnEncryption(object? sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (await RequireDevice() is not { } device) return;
        var udid = device.Udid;
        var encrypted = await Task.Run(() => Core.WillEncrypt(udid));
        if (encrypted is null)
        {
            await Dialogs.Info(this, "Backup encryption", "Cannot read the encryption setting. Unlock the device and click Pair first.");
            return;
        }
        if (encrypted == false)
        {
            if (await Dialogs.AskPassword(this, "Turn on backup encryption",
                    $"{device.Label}\n\nEncrypted backups also include saved passwords, Wi-Fi settings and Health data.\n\n" +
                    "Keep this password safe: without it these backups cannot be restored. " +
                    "The device may ask for its passcode to confirm.\n\nNew backup password:", confirm: true) is { } password)
                await RunJob("encryption-on", ["idevicebackup2", "-u", udid, "encryption", "on"],
                             new() { ["BACKUP_PASSWORD_NEW"] = password, ["BACKUP_PASSWORD"] = password });
            return;
        }
        switch (await Dialogs.Choose(this, "Backup encryption", $"{device.Label}\n\nBackup encryption is on for this device.",
                                     "Turn off", "Change password"))
        {
            case 0:
                if (await Dialogs.AskPassword(this, "Turn off backup encryption", "Current backup password:", confirm: false) is { } current)
                    await RunJob("encryption-off", ["idevicebackup2", "-u", udid, "encryption", "off"],
                                 new() { ["BACKUP_PASSWORD"] = current });
                break;
            case 1:
                if (await Dialogs.AskPassword(this, "Change backup password", "Current backup password:", confirm: false) is { } old
                    && await Dialogs.AskPassword(this, "Change backup password", "New backup password:", confirm: true) is { } next)
                    await RunJob("encryption-changepw", ["idevicebackup2", "-u", udid, "changepw"],
                                 new() { ["BACKUP_PASSWORD"] = old, ["BACKUP_PASSWORD_NEW"] = next });
                break;
        }
    });

    async void OnRestore(object? sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (await RequireDevice() is not { } device) return;
        if (await PickFolder("Pick the backup folder (named after a device UDID)") is not { } folder) return;
        folder = Path.TrimEndingDirectorySeparator(folder);
        if (!Core.IsBackupDir(folder) || Path.GetDirectoryName(folder) is not { } parent)
        {
            await Dialogs.Info(this, "RottenApple", "That folder is not an idevicebackup2 backup.\n" +
                                                    "Pick the UDID-named folder inside your backup location.");
            return;
        }
        if (await Dialogs.Confirm(this, "Restore backup", $"Restore onto: {device.Label}\n\nThis overwrites data on the device.\n" +
                                  "Turn off Find My first. Do not unplug until it finishes.", "Restore"))
        {
            // -s lets a backup from one device restore onto another
            string[] cmd = ["idevicebackup2", "-u", device.Udid, "-s", Path.GetFileName(folder),
                            "restore", "--system", "--settings", "--reboot", parent];
            // idevicebackup2 itself reads Manifest.plist and stops before touching the device if a password is needed
            if ((await RunJob("restore", cmd)).Contains(Core.NeedsBackupPassword)
                && await Dialogs.AskPassword(this, "Encrypted backup", "This backup is encrypted.\n\nBackup password:", confirm: false) is { } password)
                await RunJob("restore", cmd, new() { ["BACKUP_PASSWORD"] = password });
        }
    });

    /// <summary>
    /// Target args for idevicerestore, always naming exactly one device (-u UDID, or -i ECID for recovery/DFU),
    /// plus a label for the confirmation; null means cancel.
    /// </summary>
    async Task<(string[] Args, string Label)?> RestoreTarget()
    {
        if (SelectedDevice() is { } device) return (["-u", device.Udid], device.Label);
        var recovery = await Task.Run(() => Core.ListRecoveryDevices());
        if (recovery.Count == 0)
        {
            await Dialogs.Info(this, "No device", "No device found in normal, recovery or DFU mode.");
            return null;
        }
        var choice = await Dialogs.Choose(this, "Device in recovery or DFU mode",
            "No device in normal mode was found. Pick the device to use:", recovery.Select(r => r.Label).ToArray());
        return choice < 0 ? null : (["-i", recovery[choice].Ecid], recovery[choice].Label);
    }

    async void OnUpdate(object? sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (await RestoreTarget() is not { } target) return;
        if (await Dialogs.Confirm(this, "Update iOS", $"Update: {target.Label}\n\nDownload and install the latest signed iOS.\n" +
                                  "Data is kept, but back up first. Do not unplug until it finishes.", "Update"))
            await RunJob("update", ["idevicerestore", .. target.Args, "--latest", "-y"]);
    });

    async void OnErase(object? sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (await RestoreTarget() is not { } target) return;
        var answer = await Dialogs.Ask(this, "Erase & reinstall",
            $"Erase: {target.Label}\n\nThis DELETES EVERYTHING on the device and installs the latest signed iOS.\n" +
            "Back up first. Do not unplug until it finishes.\n\nType ERASE to continue:");
        if (answer == "ERASE")
            await RunJob("erase", ["idevicerestore", .. target.Args, "--erase", "--latest", "-y"]);
    });

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (job is null || closeConfirmed) return;
        e.Cancel = true;
        if (await Dialogs.Confirm(this, "RottenApple",
                "A job is still running. Quitting stops it, which may leave the device half-written.", "Stop and quit"))
        {
            closeConfirmed = true;
            try
            {
                job?.Kill(entireProcessTree: true);  // do not leave an orphan writing to the device unseen
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
            Close();
        }
    }
}
