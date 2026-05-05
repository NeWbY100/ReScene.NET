using System.Windows;
using ReScene.NET.Helpers;

namespace ReScene.NET.Views;

public partial class PromptWindow : Window
{
    public string ResultText { get; private set; } = string.Empty;

    public PromptWindow(string title, string message, string initialValue)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);

        Title = title;
        MessageBlock.Text = message;
        InputBox.Text = initialValue;
        InputBox.SelectAll();

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };

        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    private void OnOkClick(object _, RoutedEventArgs e)
    {
        ResultText = InputBox.Text ?? string.Empty;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object _, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
