namespace ReScene.Manager.Behaviors;

/// <summary>
/// One RowDefinition's per-mode sizing for <see cref="CompactHeightBehavior"/>.
/// </summary>
/// <param name="ExpandedMinHeight">
/// Declares this row GIVABLE in expanded mode, and says how much of it the design insists on
/// keeping: the height the row contributes to the view's expanded floor
/// (<see cref="CompactHeightBehavior"/>'s derived switch point) INSTEAD of its content height.
/// NaN — the default — means the row is not givable and its measured content height is what the
/// floor owes it.
/// <para>
/// Only meaningful for a row whose content can genuinely scroll away the difference at expanded
/// size: the three-band views' config band does so via a ScrollViewer whose MaxHeight the view
/// caps to the space actually left over (see CreatorView/SampleRestorerView), and a Star row does
/// so by construction — Star rows need no declaration here, since the behavior already owes them
/// their <c>MinHeight</c>. Declaring it on a row that cannot scroll would move the switch point
/// below the height the row's content actually needs, which is precisely the band of clipped
/// expanded layout the derived model exists to make impossible.
/// </para>
/// <para>
/// This is an AUTHORED design value, not a measurement: it answers "how little of this band is
/// still worth staying expanded for", which no amount of measuring the content can decide.
/// </para>
/// </param>
/// <param name="RowIndex">The Grid row index this entry governs.</param>
/// <param name="NormalHeight">The row's expanded-mode height in DIPs.</param>
/// <param name="CompactMinHeight">
/// The row's minimum height in compact mode.
/// <para>
/// This is the value the row previously used only while the Help body was OPEN. Help used to be
/// collapsible in compact mode, so each row carried two compact minimums and the behavior picked
/// between them depending on whether Help happened to be showing. Help is now a flat,
/// always-visible section in every mode, so the open case is the only case — the two collapsed
/// into this one, keeping the layout that already satisfied the compact floor bound with Help
/// showing.
/// </para>
/// </param>
/// <param name="Mode">How the row participates in compact mode.</param>
internal sealed record CompactRowSize(
    int RowIndex,
    double NormalHeight,
    double CompactMinHeight,
    CompactRowMode Mode,
    double ExpandedMinHeight = double.NaN);
