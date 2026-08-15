using Avalonia.Automation.Peers;

namespace ReScene.Manager.Controls;

/// <summary>
/// Reports the <see cref="HelpDisclosure"/> region as a plain structural GROUP: a container that
/// holds the Help content and has no expand/collapse semantics of any kind. The single actionable
/// route in the region is the header ToggleButton, which carries the Toggle pattern, is the only
/// keyboard-focusable peer, and exists only in compact mode — exactly where a disclosure affordance
/// is real.
/// <para>
/// WHY IT DERIVES FROM <see cref="ControlAutomationPeer"/> RATHER THAN <c>ExpanderAutomationPeer</c>.
/// Withholding the ExpandCollapse PROVIDER stopped an assistive technology INVOKING the pattern, but
/// left the inherited machinery that RELAYS <c>IsExpanded</c> changes as ExpandCollapse property
/// events — and that relay is not overridable (<c>OwnerPropertyChanged</c> is non-virtual). MEASURED
/// against the derived version: a programmatic collapse at normal size, which the behavior's
/// invariant guard immediately reverts, emitted <c>Expanded -&gt; Collapsed</c> while the region
/// ended up expanded and visible — because the guard's revert re-enters the property notification
/// and completes before the peer's own subscription runs on the outer one. A subscribed AT was told
/// the region was collapsed while it sat open. Deriving from the plain control peer removes both the
/// interface implementation and that event relay at the source: there is no expand/collapse
/// semantics left to contradict anything, in either mode.
/// </para>
/// <para>
/// Nothing is reimplemented beyond the control type, because nothing else was being added by the
/// expander peer: name, children, parent, bounds, enablement, offscreen state, focusability and the
/// content/control-element flags all resolve identically from <see cref="ControlAutomationPeer"/> —
/// verified by capturing every one of them before and after this rebase, not assumed.
/// </para>
/// </summary>
public class HelpDisclosureAutomationPeer(HelpDisclosure owner) : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
}
