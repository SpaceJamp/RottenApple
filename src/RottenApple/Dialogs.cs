using Avalonia.Controls;
using Avalonia.Layout;

namespace RottenApple;

/// <summary>Avalonia has no built-in message box, so these build small modal windows.</summary>
static class Dialogs
{
    public static async Task<bool> Confirm(Window owner, string title, string message, string yes = "Continue") =>
        await Choose(owner, title, message, yes) == 0;

    /// <summary>
    /// Index of the option the user clicked, or -1 for Cancel. Cancel is the default button, so Enter and
    /// Escape never confirm a destructive action; the user has to click an option.
    /// </summary>
    public static async Task<int> Choose(Window owner, string title, string message, params string[] options)
    {
        var chosen = -1;
        var (dialog, buttons) = Make(title, message, null);
        AddButton(dialog, buttons, "Cancel", () => { }, isCancel: true, isDefault: true);
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            AddButton(dialog, buttons, options[i], () => chosen = index, isCancel: false, isDefault: false);
        }
        await dialog.ShowDialog(owner);
        return chosen;
    }

    public static async Task Info(Window owner, string title, string message)
    {
        var (dialog, buttons) = Make(title, message, null);
        AddButton(dialog, buttons, "OK", () => { }, isCancel: true, isDefault: true);
        await dialog.ShowDialog(owner);
    }

    /// <summary>Text the user typed, or null if they cancelled.</summary>
    public static async Task<string?> Ask(Window owner, string title, string message)
    {
        string? answer = null;
        var input = new TextBox();
        var (dialog, buttons) = Make(title, message, input);
        AddButton(dialog, buttons, "Cancel", () => { }, isCancel: true, isDefault: false);
        AddButton(dialog, buttons, "OK", () => answer = input.Text, isCancel: false, isDefault: true);
        dialog.Opened += (_, _) => input.Focus();
        await dialog.ShowDialog(owner);
        return answer;
    }

    /// <summary>A non-empty password, or null if the user cancelled. confirm asks twice and checks both match.</summary>
    public static async Task<string?> AskPassword(Window owner, string title, string message, bool confirm)
    {
        string? answer = null;
        var first = new TextBox { PasswordChar = '•' };
        var second = new TextBox { PasswordChar = '•' };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, IsVisible = false };
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(first);
        if (confirm)
        {
            fields.Children.Add(new TextBlock { Text = "Type it again:" });
            fields.Children.Add(second);
        }
        fields.Children.Add(error);
        var (dialog, buttons) = Make(title, message, fields);
        AddButton(dialog, buttons, "Cancel", () => { }, isCancel: true, isDefault: false);
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            var problem = string.IsNullOrEmpty(first.Text) ? "Enter a password."
                : confirm && first.Text != second.Text ? "The passwords do not match." : null;
            if (problem is not null)
            {
                error.Text = problem;
                error.IsVisible = true;
                return;
            }
            answer = first.Text;
            dialog.Close();
        };
        buttons.Children.Add(ok);
        dialog.Opened += (_, _) => first.Focus();
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

    static void AddButton(Window dialog, StackPanel buttons, string text, Action onClick, bool isCancel, bool isDefault)
    {
        var button = new Button { Content = text, IsCancel = isCancel, IsDefault = isDefault };
        button.Click += (_, _) => { onClick(); dialog.Close(); };
        buttons.Children.Add(button);
    }
}
