using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReScene.NET.ViewModels.Wizards;

/// <summary>
/// Drives a multi-step wizard: ordered steps, current index, and Back/Next navigation gated by
/// per-step validity. The task ViewModel that owns the real data/commands is exposed as
/// <see cref="Content"/> (used as the DataContext for the step body); this VM only navigates.
/// </summary>
public partial class WizardViewModel : ViewModelBase, IDisposable
{
    public string Title { get; }
    public IReadOnlyList<WizardStep> Steps { get; }
    public object Content { get; }

    private INotifyPropertyChanged? _contentNotifier;

    public WizardViewModel(string title, object content, IReadOnlyList<WizardStep> steps)
    {
        Title = title;
        Content = content;
        Steps = steps;

        // Step validity often depends on fields of the content VM; re-evaluate Next when it changes.
        if (content is INotifyPropertyChanged notifier)
        {
            _contentNotifier = notifier;
            notifier.PropertyChanged += OnContentPropertyChanged;
        }
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsBackVisible));
    }

    /// <summary>Unsubscribes from the content VM so a closed wizard can be garbage-collected.</summary>
    public void Dispose()
    {
        if (_contentNotifier is not null)
        {
            _contentNotifier.PropertyChanged -= OnContentPropertyChanged;
            _contentNotifier = null;
        }

        GC.SuppressFinalize(this);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(CurrentStepNumber))]
    [NotifyPropertyChangedFor(nameof(StepHeader))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    [NotifyPropertyChangedFor(nameof(IsBackVisible))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial int CurrentStepIndex { get; set; }

    public int StepCount => Steps.Count;
    public int CurrentStepNumber => CurrentStepIndex + 1;
    public bool IsFirstStep => CurrentStepIndex == 0;
    public bool IsLastStep => CurrentStepIndex == Steps.Count - 1;
    public string StepHeader => $"{Steps[CurrentStepIndex].Title}  —  Step {CurrentStepNumber} of {StepCount}";
    public string NextButtonText => Steps[CurrentStepIndex].NextLabelFunc?.Invoke()
        ?? Steps[CurrentStepIndex].NextLabel ?? "Next ›";

    /// <summary>
    /// Whether the Back button is shown: hidden on the first step (nothing to go back to) and
    /// when the current step's <see cref="WizardStep.CanGoBack"/> forbids going back.
    /// </summary>
    public bool IsBackVisible => !IsFirstStep && Steps[CurrentStepIndex].CanGoBack?.Invoke() != false;

    private bool CanGoNext() => !IsLastStep && Steps[CurrentStepIndex].CanAdvance();
    private bool CanGoBack() => !IsFirstStep && Steps[CurrentStepIndex].CanGoBack?.Invoke() != false;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (!CanGoNext())
        {
            return;
        }

        WizardStep leaving = Steps[CurrentStepIndex];
        if (leaving.ConfirmLeave is not null && !leaving.ConfirmLeave())
        {
            return;
        }

        CurrentStepIndex++;
        leaving.OnLeave?.Invoke();
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
