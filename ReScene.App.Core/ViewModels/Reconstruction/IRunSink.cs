namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// The view-model-owned state <see cref="ReconstructionRunner"/> writes back. Six of the seven are
/// bound properties; <see cref="SetStageLabel"/> targets a private field.
/// </summary>
/// <remarks>
/// Derived from what the moved code actually assigns, not designed up front: the six progress
/// properties are every statement-level write in <c>ReportSetSummary</c>, and
/// <see cref="SetStageLabel"/> is the one field the loop writes that the view-model must keep owning.
/// A method nothing calls does not belong here.
/// </remarks>
internal interface IRunSink
{
    /// <summary>
    /// Sets the active set/attempt label. It stays a view-model field rather than moving into the
    /// runner because the progress handler reads it LIVE while the run writes it on its await
    /// continuation. Both accesses land on the UI thread under the production dispatcher - the
    /// read happens inside <c>IUiDispatcher.Invoke</c> - but the field is <c>volatile</c> there
    /// because a dispatcher that invokes inline runs that read on the engine's callback thread.
    /// </summary>
    public void SetStageLabel(SetStageLabel? label);

    public void SetProgressPercent(double value);

    public void SetProgressPercentText(string value);

    public void SetTestCountText(string value);

    public void SetProgressMessage(string value);

    public void SetPhaseDescription(string value);

    public void SetLastRunSucceeded(bool value);
}
