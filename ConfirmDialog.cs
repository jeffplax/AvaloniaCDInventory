using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaloniaCDInventory;

// A small Yes/No dialog. Avalonia has no built-in message box, so this builds one in code.
public static class ConfirmDialog
{
    // Returns true only if the user clicks the confirm button. Enter, Escape and closing the window all cancel,
    // so a stray keypress (or a barcode scanner's Enter) can never confirm by accident.
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmText = "Delete")
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true, MinWidth = 80 };
        var confirm = new Button
        {
            Content = confirmText,
            MinWidth = 80,
            Background = new SolidColorBrush(Color.Parse("#C42B1C")),
            Foreground = Brushes.White
        };

        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, confirm }
                }
            }
        };

        dialog.Opened += (_, _) => cancel.Focus();

        bool? result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}
