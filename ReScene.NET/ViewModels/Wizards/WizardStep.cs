namespace ReScene.NET.ViewModels.Wizards;

/// <summary>One step in a wizard: a display title and a predicate gating advance to the next step.</summary>
public sealed class WizardStep
{
    public required string Title { get; init; }
    public Func<bool> CanAdvance { get; init; } = static () => true;
}
