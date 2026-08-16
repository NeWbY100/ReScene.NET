namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>The active set/attempt label for progress messages: <c>Set X/N &#183; &lt;stage&gt;</c> (#24).</summary>
/// <remarks>
/// Written by the run loop on its await continuation and read by the view-model's progress handler on
/// the engine's callback thread, which is why the field holding it is volatile. Promoted from a
/// nested type so both sides of the runner seam can name it.
/// </remarks>
internal sealed record SetStageLabel(int SetIndex, int SetCount, string Stage)
{
    public string Format() => $"Set {SetIndex}/{SetCount} · {Stage}";
}
