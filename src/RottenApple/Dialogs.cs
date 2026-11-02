using Avalonia.Controls;
using Avalonia.Layout;

namespace RottenApple;

/// <summary>Avalonia has no built-in message box, so these build small modal windows.</summary>
static class Dialogs
{
    public static async Task<bool> Confirm(Window owner, string title, string message, string yes = "Continue")
    {
        var confirmed = false;
        var (dialog, buttons) = Make(title, message, null);
        AddButton(dialog, buttons, "Cancel", isCancel: true, () => { });
        AddButton(dialog, buttons, yes, isCancel: false, () => confirmed = true);
        await dialog.ShowDialog(owner);
        return confirmed;
    }

    public static async Task Info(Window owner, string title, string message)
    {
        var (dialog, buttons) = Make(title, message, null);
        AddButton(dialog, buttons, "OK", isCancel: true, () => { });
        await dialog.ShowDialog(owner);
    }

    /// <summary>Text the user typed, or null if they cancelled.</summary>
    public static async Task<string?> Ask(Window owner, string title, string message)
    {
        string? answer = null;
        var input = new TextBox();
        var (dialog, buttons) = Make(title, message, input);
        AddButton(dialog, buttons, "Cancel", isCancel: true, () => { });
        AddButton(dialog, buttons, "OK", isCancel: false, () => answer = input.Text);
        dialog.Opened += (_, _) => input.Focus();
        await dialog.ShowDialog(owner);
        return answer;
    }

    static (Window, StackPanel) Make(string title, string message, Control? extra)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var body = new StackPanel { Margin = new(16), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (extra is not null) body.Children.Add(extra);
        body.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Content = body, Width = 440, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        return (dialog, buttons);
    }

    static void AddButton(Window dialog, StackPanel buttons, string text, bool isCancel, Action onClick)
    {
        var button = new Button { Content = text, IsCancel = isCancel, IsDefault = !isCancel };
        button.Click += (_, _) => { onClick(); dialog.Close(); };
        buttons.Children.Add(button);
    }
}
