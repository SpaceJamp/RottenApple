using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RottenApple;

/// <summary>Device logic with no UI: tool lookup, device listing, output parsing.</summary>
static partial class Core
{
    public static readonly string[] Tools = ["idevice_id", "ideviceinfo", "idevicepair", "idevicebackup2", "idevicerestore"];

    public static readonly string LogDir = Path.Combine(
        // XDG spec: a relative XDG_STATE_HOME is invalid and must be ignored
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { } state && Path.IsPathRooted(state)
            ? state
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
        "rottenapple");

    // install.sh puts a source-built idevicerestore here
    static readonly string LocalBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    // Both tools draw "[prefix ][=====     ]  45.2%" bars, 50 cells wide, redrawn with \r.
    // [0-9], not \d: \d also matches non-ASCII digits that double.Parse rejects.
    [GeneratedRegex(@"^(.*?)\s*\[[= ]{50}\]\s*([0-9]+(?:\.[0-9]+)?)%")]
    private static partial Regex ProgressRegex();

    // The iBoot USB serial string of a recovery/DFU device contains "ECID:<hex>".
    [GeneratedRegex(@"ECID:([0-9A-Fa-f]+)")]
    private static partial Regex EcidRegex();

    // Apple USB product IDs for recovery, WTF and DFU modes (libirecovery.h enum irecv_mode).
    static readonly Dictionary<string, string> RecoveryModes = new()
    {
        ["1280"] = "recovery", ["1281"] = "recovery", ["1282"] = "recovery", ["1283"] = "recovery",
        ["1222"] = "WTF", ["1227"] = "DFU", ["f014"] = "port DFU",
    };

    // Known failure messages (lowercase substrings) with plain-English advice.
    static readonly (string Key, string Hint)[] Hints =
    [
        ("did not disconnect",
         "The device did not restart into the next mode. Force-restart it into recovery mode, " +
         "plug it straight into the computer (no hub), then try again."),
        ("unable to send data to asr",
         "The transfer to the device broke off. Try another cable and a USB port directly on the computer."),
        ("no firmwares are currently being signed",
         "Apple does not sign any iOS version for this device right now, so it cannot be restored."),
        ("lockdownd",
         "Cannot talk to the device. Unlock it, tap Trust, then click Pair."),
        ("no device found",
         "No device detected. Check the cable and that usbmuxd runs: sudo systemctl start usbmuxd"),
        ("unable to find device",
         "No device detected. Check the cable and that usbmuxd runs: sudo systemctl start usbmuxd"),
        ("unable to place device",
         "The device would not switch modes. Put it into recovery mode by hand, " +
         "then use Update iOS or Erase with no device selected."),
        ("device refused to start",
         "The device refused. Unlock it. For a restore, also turn off Find My."),
        ("install splat",
         "Known upstream failure on some iOS 26 updates. Try Erase & reinstall instead."),
    ];

    /// <summary>Full path of an executable tool on PATH or in ~/.local/bin, else null.</summary>
    public static string? FindTool(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(LocalBin);
        // Only absolute PATH entries: "." or "bin" would run whatever file sits in the current directory,
        // and that process may get a backup password in its environment.
        return dirs.Where(Path.IsPathRooted).Select(d => Path.Combine(d, name)).FirstOrDefault(IsExecutable);
    }

    static bool IsExecutable(string path) =>
        File.Exists(path)
        && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    /// <summary>Drop control characters (escape sequences, stray \r) from tool or device text, keeping tabs.</summary>
    public static string Clean(string text) => new(text.Where(c => c == '\t' || !char.IsControl(c)).ToArray());

    static string FirstLine(string text) => Clean(text.Split('\n')[0]).Trim();

    /// <summary>Tool path for running; falls back to the bare name so the error names it.</summary>
    public static string Tool(string name) => FindTool(name) ?? name;

    /// <summary>Stdout of a short command, or "" if it fails, is missing or times out.</summary>
    public static string RunQuiet(params string[] cmd)
    {
        try
        {
            var psi = new ProcessStartInfo(Tool(cmd[0])) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in cmd.Skip(1)) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            var output = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(15_000)) { proc.Kill(entireProcessTree: true); return ""; }
            // a grandchild holding stdout open must not hang the caller
            return output.Wait(5_000) ? output.Result.Trim() : "";
        }
        catch (System.ComponentModel.Win32Exception) { return ""; }
    }

    public record Device(string Udid, string Label);

    /// <summary>Connected devices in normal mode. run is injectable for tests.</summary>
    public static List<Device> ListDevices(Func<string[], string>? run = null)
    {
        run ??= RunQuiet;
        var devices = new List<Device>();
        foreach (var line in run(["idevice_id", "-l"]).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var udid = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            // the device name is user-chosen on the phone: keep one clean line
            var name = FirstLine(run(["ideviceinfo", "-u", udid, "-k", "DeviceName"])) is { Length: > 0 } n ? n : "Unknown (pair first)";
            var version = FirstLine(run(["ideviceinfo", "-u", udid, "-k", "ProductVersion"])) is { Length: > 0 } v ? v : "?";
            devices.Add(new Device(udid, $"{name}  iOS {version}  [{udid}]"));
        }
        return devices;
    }

    /// <summary>Whether the device encrypts its backups; null if the device cannot be read (locked or not paired).</summary>
    public static bool? WillEncrypt(string udid, Func<string[], string>? run = null)
    {
        run ??= RunQuiet;
        return run(["ideviceinfo", "-u", udid, "-q", "com.apple.mobile.backup", "-k", "WillEncrypt"]) switch
        {
            "true" => true,
            "false" => false,
            // the key is missing until encryption is first turned on; idevicebackup2 treats that as off too
            _ => run(["ideviceinfo", "-u", udid, "-k", "DeviceName"]).Length > 0 ? false : null,
        };
    }

    public record RecoveryDevice(string Ecid, string Mode)
    {
        public string Label => $"Device in {Mode} mode (ECID {Ecid})";
    }

    /// <summary>
    /// Devices in recovery/DFU mode, read from sysfs. These do not show up in idevice_id, and idevicerestore
    /// without -u or -i takes whichever one it finds first, so callers target one by ECID instead.
    /// Devices whose ECID cannot be read are skipped, since they cannot be targeted safely.
    /// </summary>
    public static List<RecoveryDevice> ListRecoveryDevices(string sysfs = "/sys/bus/usb/devices")
    {
        var found = new List<RecoveryDevice>();
        if (!Directory.Exists(sysfs)) return found;
        foreach (var dev in Directory.GetDirectories(sysfs))
        {
            string Read(string name)
            {
                try { return File.ReadAllText(Path.Combine(dev, name)).Trim(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
            }
            if (Read("idVendor") != "05ac" || !RecoveryModes.TryGetValue(Read("idProduct"), out var mode)) continue;
            // idevicerestore parses --ecid with strtoull(..., 0), so hex needs the 0x prefix
            if (EcidRegex().Match(Read("serial")) is { Success: true } m)
                found.Add(new RecoveryDevice("0x" + m.Groups[1].Value.ToUpperInvariant(), mode));
        }
        return found;
    }

    /// <summary>idevicebackup2 prints this when a restore needs the backup password.</summary>
    public const string NeedsBackupPassword = "a backup password is required to restore an encrypted backup";

    /// <summary>idevicebackup2 writes one folder per device UDID holding Info.plist and Manifest.plist.</summary>
    public static bool IsBackupDir(string path) =>
        File.Exists(Path.Combine(path, "Info.plist")) && File.Exists(Path.Combine(path, "Manifest.plist"));

    /// <summary>(label, percent) for a progress-bar line, else null.</summary>
    public static (string Label, double Percent)? ParseProgress(string line)
    {
        var m = ProgressRegex().Match(line);
        return m.Success ? (m.Groups[1].Value.Trim(), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    public static string? HintFor(string line)
    {
        var low = line.ToLowerInvariant();
        return Hints.Where(h => low.Contains(h.Key)).Select(h => h.Hint).FirstOrDefault();
    }

    public static bool UsbmuxdRunning() =>
        FindTool("pgrep") is null || RunQuiet("pgrep", "-x", "usbmuxd").Length > 0;
}
