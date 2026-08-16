namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>The active set/attempt label for progress messages: <c>Set X/N &#183; &lt;stage&gt;</c> (#24).</summary>
/// <remarks>
/// Written by the run loop on its await continuation and read by the view-model's progress handler
/// — which reads it INSIDE <c>IUiDispatcher.Invoke</c>, so under the production dispatcher both
/// accesses are on the UI thread. The field stays <c>volatile</c> because the dispatcher is an
/// abstraction: an implementation that invokes inline (the test double does) runs that read on
/// whichever thread the engine raised its callback from. Promoted from a nested type so both sides
/// of the runner seam can name it.
/// </remarks>
internal sealed record SetStageLabel(int SetIndex, int SetCount, string Stage)
{
    public string Format() => $"Set {SetIndex}/{SetCount} · {Stage}";
}
