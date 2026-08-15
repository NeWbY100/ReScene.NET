using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ReScene.App.Core.ViewModels.Wizards;

namespace ReScene.Manager.Views.Wizards;

/// <summary>
/// The pop-up shell that hosts a Beginner wizard, ported from the WPF
/// <c>ReScene.NET.Views.Wizards.WizardWindow</c>. Its <see cref="Avalonia.StyledElement.DataContext"/> is the
/// navigation <see cref="WizardViewModel"/> (header + Back/Next footer); the injected body
/// <see cref="Control"/> is placed in <c>BodyHost</c> with its own DataContext set to the wizard's
/// <see cref="WizardViewModel.Content"/> (the task VM the step panels bind to). The WPF
/// <c>DarkTitleBar.Enable</c> call and <c>ShowInTaskbar="False"</c> are dropped — the app-wide dark
/// theme already covers this window's chrome.
/// </summary>
public partial class WizardWindow : Window
{
    /// <summary>Parameterless constructor for the XAML designer / loader only.</summary>
    public WizardWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public WizardWindow(WizardViewModel viewModel, Control body)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(body);

        // Set the window's DataContext (the navigation VM the header/footer bind to) BEFORE parenting
        // the body, so the body's step-panel bindings that reach the window via $parent[Window] never
        // observe a transient null DataContext (the T5.1a note).
        DataContext = viewModel;
        body.DataContext = viewModel.Content; // step fields bind to the task VM
        this.FindControl<ContentControl>("BodyHost")!.Content = body;

        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
