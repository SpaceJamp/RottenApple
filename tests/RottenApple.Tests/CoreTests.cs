using Xunit;

namespace RottenApple.Tests;

public class CoreTests
{
    [Fact]
    public void ListDevices_ParsesUdidAndFillsGaps()
    {
        var replies = new Dictionary<string, string>
        {
            ["idevice_id -l"] = "abc123 (USB)\n\n",
            ["ideviceinfo -u abc123 -k DeviceName"] = "Jo's iPhone",
            ["ideviceinfo -u abc123 -k ProductVersion"] = "",
        };
        var devices = Core.ListDevices(cmd => replies.GetValueOrDefault(string.Join(" ", cmd), ""));
        Assert.Equal([new Core.Device("abc123", "Jo's iPhone  iOS ?  [abc123]")], devices);
    }

    [Theory]
    [InlineData("true", "Jo's iPhone", true)]
    [InlineData("false", "Jo's iPhone", false)]
    [InlineData("", "Jo's iPhone", false)]  // key missing until encryption is first enabled
    [InlineData("", "", null)]              // device locked or not paired
    public void WillEncrypt_ReadsLockdownKey(string willEncrypt, string deviceName, bool? expected)
    {
        var replies = new Dictionary<string, string>
        {
            ["ideviceinfo -u abc -q com.apple.mobile.backup -k WillEncrypt"] = willEncrypt,
            ["ideviceinfo -u abc -k DeviceName"] = deviceName,
        };
        Assert.Equal(expected, Core.WillEncrypt("abc", cmd => replies.GetValueOrDefault(string.Join(" ", cmd), "")));
    }

    [Fact]
    public void IsBackupDir_NeedsInfoAndManifest()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.False(Core.IsBackupDir(dir));
            File.WriteAllText(Path.Combine(dir, "Info.plist"), "");
            File.WriteAllText(Path.Combine(dir, "Manifest.plist"), "");
            Assert.True(Core.IsBackupDir(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseProgress_ReadsBothToolFormats()
    {
        // formats copied from idevicerestore common.c and idevicebackup2.c
        var bar = new string('=', 20) + new string(' ', 30);
        Assert.Equal(("Sending filesystem now", 40.0), Core.ParseProgress($"Sending filesystem now [{bar}]  40.0% "));
        Assert.Equal(("", 40.0), Core.ParseProgress($"[{bar}]  40%"));
        Assert.Null(Core.ParseProgress("Extracting filesystem [done]"));
    }

    [Fact]
    public void ParseProgress_IgnoresNonAsciiDigits()
    {
        var bar = new string('=', 50);
        Assert.Null(Core.ParseProgress($"[{bar}] ٤٠%"));  // Arabic-Indic digits used to crash double.Parse
    }

    [Fact]
    public void Clean_StripsControlCharacters()
    {
        Assert.Equal("[31mred[0m\tok", Core.Clean("\u001b[31mred\u001b[0m\tok\r"));
    }

    [Fact]
    public void ListRecoveryDevices_ReadsEcidFromSysfs()
    {
        var sysfs = Directory.CreateTempSubdirectory().FullName;
        try
        {
            void Usb(string name, string vendor, string product, string serial)
            {
                var dir = Directory.CreateDirectory(Path.Combine(sysfs, name)).FullName;
                File.WriteAllText(Path.Combine(dir, "idVendor"), vendor + "\n");
                File.WriteAllText(Path.Combine(dir, "idProduct"), product + "\n");
                File.WriteAllText(Path.Combine(dir, "serial"), serial + "\n");
            }
            Usb("1-1", "05ac", "1281", "SDOM:01 CPID:8101 CPRV:11 ECID:001a2b3c4d5e6f70 IBFL:3C SRTG:[iBoot-8419]");
            Usb("1-2", "05ac", "1227", "CPID:8030 ECID:0000aabbccddeeff");
            Usb("1-3", "05ac", "12a8", "phone in normal mode");       // not recovery/DFU
            Usb("1-4", "046d", "1281", "ECID:1234");                  // not Apple
            Usb("1-5", "05ac", "1281", "no ecid here");               // cannot be targeted safely
            var found = Core.ListRecoveryDevices(sysfs).OrderBy(d => d.Ecid).ToList();
            Assert.Equal([new("0x0000AABBCCDDEEFF", "DFU"), new Core.RecoveryDevice("0x001A2B3C4D5E6F70", "recovery")], found);
        }
        finally
        {
            Directory.Delete(sysfs, recursive: true);
        }
    }

    [Fact]
    public void FindTool_SkipsRelativePathEntriesAndNonExecutables()
    {
        if (OperatingSystem.IsWindows()) return;  // Unix file modes only
        var cwd = Directory.CreateTempSubdirectory().FullName;
        var bin = Directory.CreateTempSubdirectory().FullName;
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldCwd = Environment.CurrentDirectory;
        try
        {
            const string name = "rottenapple-test-tool";
            File.WriteAllText(Path.Combine(cwd, name), "#!/bin/sh\n");
            File.SetUnixFileMode(Path.Combine(cwd, name), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(bin, name), "not executable");
            Environment.CurrentDirectory = cwd;
            Environment.SetEnvironmentVariable("PATH", $".:{bin}");
            Assert.Null(Core.FindTool(name));
            File.SetUnixFileMode(Path.Combine(bin, name), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Equal(Path.Combine(bin, name), Core.FindTool(name));
        }
        finally
        {
            Environment.CurrentDirectory = oldCwd;
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(cwd, recursive: true);
            Directory.Delete(bin, recursive: true);
        }
    }

    [Fact]
    public void HintFor_MatchesKnownErrors()
    {
        Assert.Contains("recovery mode", Core.HintFor("Device did not disconnect. Possibly invalid iBEC. Reset device and try again."));
        Assert.Contains("cable", Core.HintFor("Unable to send data to ASR. Sent 0 of 512 bytes."));
        Assert.Null(Core.HintFor("Downloading firmware"));
    }
}
