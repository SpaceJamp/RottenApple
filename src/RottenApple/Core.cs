using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RottenApple;

/// <summary>Device logic with no UI: tool lookup, device listing, output parsing.</summary>
static partial class Core
{
    public static readonly string[] Tools = ["idevice_id", "ideviceinfo", "idevicepair", "idevicebackup2", "idevicerestore"];

    public static readonly string LogDir = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state
            ? state
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
        "rottenapple");

    // install.sh puts a source-built idevicerestore here
    static readonly string LocalBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    // Both tools draw "[prefix ][=====     ]  45.2%" bars, 50 cells wide, redrawn with \r.
    [GeneratedRegex(@"^(.*?)\s*\[[= ]{50}\]\s*(\d+(?:\.\d+)?)%")]
    private static partial Regex ProgressRegex();

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

    /// <summary>Full path of a tool on PATH or in ~/.local/bin, else null.</summary>
    public static string? FindTool(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(LocalBin);
        return dirs.Where(d => d.Length > 0).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }

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
            if (!proc.WaitForExit(15_000)) { proc.Kill(); return ""; }
            return output.Result.Trim();
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
            var name = run(["ideviceinfo", "-u", udid, "-k", "DeviceName"]) is { Length: > 0 } n ? n : "Unknown (pair first)";
            var version = run(["ideviceinfo", "-u", udid, "-k", "ProductVersion"]) is { Length: > 0 } v ? v : "?";
            devices.Add(new Device(udid, $"{name}  iOS {version}  [{udid}]"));
        }
        return devices;
    }

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
