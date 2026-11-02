using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace RottenApple;

public partial class MainWindow : Window
{
    List<Core.Device> devices = [];
    bool busy, closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        var missing = Core.Tools.Where(t => Core.FindTool(t) is null).ToList();
        if (missing.Count > 0)
            Write($"Missing tools: {string.Join(", ", missing)}\nRun ./install.sh to install them.\n");
        Closing += OnClosing;
        _ = Refresh();
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

    void SetProgress(string label, double percent)
    {
        Progress.Value = percent;
        Stage.Text = $"{label} {percent:F0}%".Trim();
    }

    async Task Refresh()
    {
        var found = await Task.Run(() => Core.ListDevices());
        devices = found;
        DeviceBox.ItemsSource = found.Select(d => d.Label).ToList();
        if (found.Count > 0)
        {
            DeviceBox.SelectedIndex = 0;
            return;
        }
        DeviceBox.PlaceholderText = "No device found. Plug in, unlock, tap Trust.";
        if (!await Task.Run(Core.UsbmuxdRunning))
            Write("usbmuxd is not running. Plug the device in, or run: sudo systemctl start usbmuxd\n");
    }

    async Task<string?> Udid(bool quiet = false)
    {
        var i = DeviceBox.SelectedIndex;
        if (i >= 0 && i < devices.Count) return devices[i].Udid;
        if (!quiet) await Dialogs.Info(this, "RottenApple", "No device selected.");
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
        SetBusy(true);
        SetProgress("", 0);
        Write($"\n$ {string.Join(" ", cmd)}\n");
        var output = "";
        try
        {
            output = await Task.Run(() => Execute(action, cmd, env));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Write($"[error] cannot write log file: {e.Message}\n");
        }
        SetBusy(false);
        await Refresh();
        return output;
    }

    // Runs on a worker thread; all UI changes go through Post.
    string Execute(string action, string[] cmd, Dictionary<string, string>? env)
    {
        var output = new System.Text.StringBuilder();
        Directory.CreateDirectory(Core.LogDir);
        var path = Path.Combine(Core.LogDir, $"{action}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Post(() => Write($"Log file: {path}\n"));
        using var log = new StreamWriter(path) { AutoFlush = true };
        var gate = new object();
        void Record(string text)
        {
            lock (gate) log.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");
        }

        Record("$ " + string.Join(" ", cmd));
        var shown = new HashSet<string>();
        (string, int)? lastStep = null;

        // stdout and stderr arrive on separate threads; the lock keeps line handling in order.
        void Handle(string? line)
        {
            if (line is null) return;
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
                output.AppendLine(line);
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
        Record(result);
        Post(() => Write(result + "\n"));
        return output.ToString();
    }

    async void OnRefresh(object? sender, RoutedEventArgs e) => await Refresh();

    async void OnPair(object? sender, RoutedEventArgs e)
    {
        if (await Udid() is { } udid)
            await RunJob("pair", ["idevicepair", "-u", udid, "pair"]);
    }

    async void OnBackup(object? sender, RoutedEventArgs e)
    {
        if (await Udid() is not { } udid) return;
        // an encrypted backup needs no password here: the device encrypts with the one it already has
        if (await PickFolder("Folder to store backups in") is { } folder)
            await RunJob("backup", ["idevicebackup2", "-u", udid, "backup", "--full", folder]);
    }

    async void OnEncryption(object? sender, RoutedEventArgs e)
    {
        if (await Udid() is not { } udid) return;
        var encrypted = await Task.Run(() => Core.WillEncrypt(udid));
        if (encrypted is null)
        {
            await Dialogs.Info(this, "Backup encryption", "Cannot read the encryption setting. Unlock the device and click Pair first.");
            return;
        }
        if (encrypted == false)
        {
            if (await Dialogs.AskPassword(this, "Turn on backup encryption",
                    "Encrypted backups also include saved passwords, Wi-Fi settings and Health data.\n\n" +
                    "Keep this password safe: without it these backups cannot be restored. " +
                    "The device may ask for its passcode to confirm.\n\nNew backup password:", confirm: true) is { } password)
                await RunJob("encryption-on", ["idevicebackup2", "-u", udid, "encryption", "on"],
                             new() { ["BACKUP_PASSWORD_NEW"] = password, ["BACKUP_PASSWORD"] = password });
            return;
        }
        switch (await Dialogs.Choose(this, "Backup encryption", "Backup encryption is on for this device.",
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
    }

    async void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (await Udid() is not { } udid) return;
        if (await PickFolder("Pick the backup folder (named after a device UDID)") is not { } folder) return;
        folder = Path.TrimEndingDirectorySeparator(folder);
        if (!Core.IsBackupDir(folder))
        {
            await Dialogs.Info(this, "RottenApple", "That folder is not an idevicebackup2 backup.\n" +
                                                    "Pick the UDID-named folder inside your backup location.");
            return;
        }
        if (await Dialogs.Confirm(this, "Restore backup", "This overwrites data on the device.\n" +
                                  "Turn off Find My first. Do not unplug until it finishes."))
        {
            // -s lets a backup from one device restore onto another
            string[] cmd = ["idevicebackup2", "-u", udid, "-s", Path.GetFileName(folder),
                            "restore", "--system", "--settings", "--reboot", Path.GetDirectoryName(folder)!];
            // idevicebackup2 itself reads Manifest.plist and stops before touching the device if a password is needed
            if ((await RunJob("restore", cmd)).Contains(Core.NeedsBackupPassword)
                && await Dialogs.AskPassword(this, "Encrypted backup", "This backup is encrypted.\n\nBackup password:", confirm: false) is { } password)
                await RunJob("restore", cmd, new() { ["BACKUP_PASSWORD"] = password });
        }
    }

    /// <summary>UDID args for idevicerestore; empty means "whatever device is in recovery/DFU mode"; null means cancel.</summary>
    async Task<string[]?> RestoreTarget()
    {
        if (await Udid(quiet: true) is { } udid) return ["-u", udid];
        return await Dialogs.Confirm(this, "No device selected",
            "No device in normal mode was found.\n\nUse a device that is in recovery or DFU mode?") ? [] : null;
    }

    async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (await RestoreTarget() is not { } target) return;
        if (await Dialogs.Confirm(this, "Update iOS", "Download and install the latest signed iOS.\n" +
                                  "Data is kept, but back up first. Do not unplug until it finishes."))
            await RunJob("update", ["idevicerestore", .. target, "--latest", "-y"]);
    }

    async void OnErase(object? sender, RoutedEventArgs e)
    {
        if (await RestoreTarget() is not { } target) return;
        var answer = await Dialogs.Ask(this, "Erase & reinstall",
            "This DELETES EVERYTHING on the device and installs the latest signed iOS.\n" +
            "Back up first. Do not unplug until it finishes.\n\nType ERASE to continue:");
        if (answer == "ERASE")
            await RunJob("erase", ["idevicerestore", .. target, "--erase", "--latest", "-y"]);
    }

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!busy || closeConfirmed) return;
        e.Cancel = true;
        if (await Dialogs.Confirm(this, "RottenApple",
                "A job is still running. Closing may leave the device half-written.", "Quit anyway"))
        {
            closeConfirmed = true;
            Close();
        }
    }
}
