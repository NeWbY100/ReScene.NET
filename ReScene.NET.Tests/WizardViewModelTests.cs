using ReScene.NET.ViewModels.Wizards;

namespace ReScene.NET.Tests;

public class WizardViewModelTests
{
    private static WizardViewModel Make(params bool[] canAdvance)
    {
        var steps = new List<WizardStep>();
        for (int i = 0; i < canAdvance.Length; i++)
        {
            bool v = canAdvance[i];
            steps.Add(new WizardStep { Title = $"Step {i}", CanAdvance = () => v });
        }
        return new WizardViewModel("Test", new object(), steps);
    }

    [Fact]
    public void NewWizard_StartsAtFirstStep()
    {
        var w = Make(true, true, true);
        Assert.Equal(0, w.CurrentStepIndex);
        Assert.True(w.IsFirstStep);
        Assert.False(w.IsLastStep);
        Assert.Equal(3, w.StepCount);
        Assert.Equal(1, w.CurrentStepNumber);
        Assert.False(w.BackCommand.CanExecute(null));
    }

    [Fact]
    public void Next_AdvancesWhenStepValid_AndStopsAtLast()
    {
        var w = Make(true, true);
        Assert.True(w.NextCommand.CanExecute(null));
        w.NextCommand.Execute(null);
        Assert.Equal(1, w.CurrentStepIndex);
        Assert.True(w.IsLastStep);
        Assert.False(w.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Next_BlockedWhenStepInvalid()
    {
        var w = Make(false, true);
        Assert.False(w.NextCommand.CanExecute(null));
        w.NextCommand.Execute(null);
        Assert.Equal(0, w.CurrentStepIndex);
    }

    [Fact]
    public void Back_ReturnsToPreviousStep()
    {
        var w = Make(true, true);
        w.NextCommand.Execute(null);
        Assert.True(w.BackCommand.CanExecute(null));
        w.BackCommand.Execute(null);
        Assert.Equal(0, w.CurrentStepIndex);
        Assert.True(w.IsFirstStep);
    }

    [Fact]
    public void Next_RunsLeavingStepOnLeave_AndUsesItsNextLabel()
    {
        bool left = false;
        var steps = new List<WizardStep>
        {
            new() { Title = "A", NextLabel = "Create", OnLeave = () => left = true },
            new() { Title = "B" },
        };
        var w = new WizardViewModel("T", new object(), steps);

        Assert.Equal("Create", w.NextButtonText);

        w.NextCommand.Execute(null);

        Assert.True(left);
        Assert.Equal(1, w.CurrentStepIndex);
        Assert.Equal("Next ›", w.NextButtonText);
    }
}
