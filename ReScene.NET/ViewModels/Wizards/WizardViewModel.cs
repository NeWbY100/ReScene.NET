using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReScene.NET.ViewModels.Wizards;

/// <summary>
/// Drives a multi-step wizard: ordered steps, current index, and Back/Next navigation gated by
/// per-step validity. The task ViewModel that owns the real data/commands is exposed as
/// <see cref="Content"/> (used as the DataContext for the step body); this VM only navigates.
/// </summary>
public partial class WizardViewModel : ViewModelBase
{
    public string Title { get; }
    public IReadOnlyList<WizardStep> Steps { get; }
    public object Content { get; }

    public WizardViewModel(string title, object content, IReadOnlyList<WizardStep> steps)
    {
        Title = title;
        Content = content;
        Steps = steps;

        // Step validity often depends on fields of the content VM; re-evaluate Next when it changes.
        if (content is INotifyPropertyChanged notifier)
        {
            notifier.PropertyChanged += (_, _) => NextCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(CurrentStepNumber))]
    [NotifyPropertyChangedFor(nameof(StepHeader))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial int CurrentStepIndex { get; set; }

    public int StepCount => Steps.Count;
    public int CurrentStepNumber => CurrentStepIndex + 1;
    public bool IsFirstStep => CurrentStepIndex == 0;
    public bool IsLastStep => CurrentStepIndex == Steps.Count - 1;
    public string StepHeader => $"{Steps[CurrentStepIndex].Title}  —  Step {CurrentStepNumber} of {StepCount}";

    private bool CanGoNext() => !IsLastStep && Steps[CurrentStepIndex].CanAdvance();
    private bool CanGoBack() => !IsFirstStep;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (CanGoNext())
        {
            CurrentStepIndex++;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (CanGoBack())
        {
            CurrentStepIndex--;
        }
    }
}
