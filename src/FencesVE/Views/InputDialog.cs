using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FencesVE.Views;

/// <summary>
/// Male okienko z jednym polem tekstowym. WPF nie ma wbudowanego odpowiednika,
/// a dociaganie calej biblioteki dla jednego promptu nie ma sensu.
/// </summary>
internal sealed class InputDialog : Window
{
    private readonly TextBox _input;

    private InputDialog(string prompt, string title, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        _input = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 8, 0, 16),
            Padding = new Thickness(4, 3, 4, 3),
        };

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 88,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ok.Click += (_, _) => { DialogResult = true; };

        var cancel = new Button
        {
            Content = "Anuluj",
            IsCancel = true,
            Width = 88,
            Height = 26,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_input);
        root.Children.Add(buttons);

        Content = root;

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
            Keyboard.Focus(_input);
        };
    }

    /// <summary>Zwraca wpisany tekst albo null, gdy uzytkownik anulowal.</summary>
    public static string? Ask(string prompt, string title, string initial = "")
    {
        var dialog = new InputDialog(prompt, title, initial);
        return dialog.ShowDialog() == true ? dialog._input.Text : null;
    }
}
