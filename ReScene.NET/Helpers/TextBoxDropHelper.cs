using System.Windows;
using System.Windows.Controls;

namespace ReScene.NET.Helpers;

/// <summary>
/// Provides drag-and-drop file/folder support for TextBox controls.
/// </summary>
internal static class TextBoxDropHelper
{
    /// <summary>
    /// Configures a TextBox to accept dropped files and set the path via the provided setter.
    /// </summary>
    /// <param name="textBox">The TextBox to configure.</param>
    /// <param name="setter">Action to call with the dropped file path.</param>
    public static void SetupFileDrop(TextBox textBox, Action<string> setter)
    {
        textBox.AllowDrop = true;

        textBox.PreviewDragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        };

        textBox.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                setter(files[0]);
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// Configures a TextBox to accept dropped folders and set the path via the provided setter.
    /// </summary>
    /// <param name="textBox">The TextBox to configure.</param>
    /// <param name="setter">Action to call with the dropped folder path.</param>
    public static void SetupFolderDrop(TextBox textBox, Action<string> setter)
    {
        textBox.AllowDrop = true;

        textBox.PreviewDragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        };

        textBox.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                string path = files[0];

                if (Directory.Exists(path))
                {
                    setter(path);
                }
                else
                {
                    string? dir = Path.GetDirectoryName(path);

                    if (dir is not null)
                    {
                        setter(dir);
                    }
                }

                e.Handled = true;
            }
        };
    }
}
