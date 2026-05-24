namespace ReScene.NET.Models;

/// <summary>
/// Severity of a field's detection/validation feedback.
/// </summary>
public enum FieldState
{
    /// <summary>No feedback to show; the status line is hidden.</summary>
    None,
    /// <summary>The value looks correct.</summary>
    Ok,
    /// <summary>Neutral information about the value.</summary>
    Info,
    /// <summary>The value is usable but something looks off.</summary>
    Warning,
    /// <summary>The value is missing or invalid.</summary>
    Error
}

/// <summary>
/// Per-field guidance shown beneath an input: a severity plus a short message.
/// </summary>
public sealed record FieldStatus(FieldState State, string Message)
{
    /// <summary>A hidden, empty status.</summary>
    public static readonly FieldStatus None = new(FieldState.None, string.Empty);

    public static FieldStatus Ok(string message) => new(FieldState.Ok, message);
    public static FieldStatus Info(string message) => new(FieldState.Info, message);
    public static FieldStatus Warning(string message) => new(FieldState.Warning, message);
    public static FieldStatus Error(string message) => new(FieldState.Error, message);
}
