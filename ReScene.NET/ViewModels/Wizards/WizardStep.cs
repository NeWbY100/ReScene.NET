namespace ReScene.NET.ViewModels.Wizards;

/// <summary>One step in a wizard: a display title and a predicate gating advance to the next step.</summary>
public sealed class WizardStep
{
    public required string Title { get; init; }
    public Func<bool> CanAdvance { get; init; } = static () => true;

    /// <summary>Optional override for the Next button's label while on this step (e.g. "Create").</summary>
    public string? NextLabel { get; init; }

    /// <summary>Optional action run when advancing FROM this step (e.g. start the operation).</summary>
    public Action? OnLeave { get; init; }
}
