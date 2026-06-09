namespace ReScene.NET.ViewModels.Wizards;

/// <summary>One step in a wizard: a display title and a predicate gating advance to the next step.</summary>
public sealed class WizardStep
{
    public required string Title { get; init; }
    public Func<bool> CanAdvance { get; init; } = static () => true;

    /// <summary>Optional override for the Next button's label while on this step (e.g. "Create").</summary>
    public string? NextLabel { get; init; }

    /// <summary>Optional dynamic Next-button label, evaluated each time this step is shown (takes
    /// precedence over <see cref="NextLabel"/>). Use when the label depends on runtime state.</summary>
    public Func<string>? NextLabelFunc { get; init; }

    /// <summary>Optional action run when advancing FROM this step (e.g. start the operation).</summary>
    public Action? OnLeave { get; init; }

    /// <summary>Optional gate run before advancing FROM this step; return false to stay on it
    /// (e.g. the user declined an overwrite). Runs before <see cref="OnLeave"/>.</summary>
    public Func<bool>? ConfirmLeave { get; init; }
}
