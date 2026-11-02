using Avalonia;

namespace RottenApple;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Console.Error.WriteLine("RottenApple needs a Wayland session (WAYLAND_DISPLAY is not set). X11 is not supported.");
            return 1;
        }
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Avalonia.Wayland.AvaloniaWaylandException e)
        {
            // e.g. WSLg's Weston lacks xdg_wm_base >= 3; GNOME and KDE have it
            Console.Error.WriteLine($"Your Wayland compositor is not supported: {e.Message}");
            return 1;
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseStandardRuntimePlatformSubsystem().UseWayland().UseSkia().UseHarfBuzz().LogToTrace();
}
