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
    public void HintFor_MatchesKnownErrors()
    {
        Assert.Contains("recovery mode", Core.HintFor("Device did not disconnect. Possibly invalid iBEC. Reset device and try again."));
        Assert.Contains("cable", Core.HintFor("Unable to send data to ASR. Sent 0 of 512 bytes."));
        Assert.Null(Core.HintFor("Downloading firmware"));
    }
}
