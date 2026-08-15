using ReScene.App.Core.ViewModels.Reconstruction;

namespace ReScene.App.Core.Tests;

public sealed class VersionSelectionReconcilerTests
{
    private static readonly IReadOnlyList<InstalledRARVersion> Installed =
    [
        new(500, "winrar-500", "p500"),
        new(560, "winrar-560", "p560"),
        new(624, "winrar-624", "p624"),
    ];

    [Fact]
    public void ExplicitSelection_TicksListedInstalled_DropsMissing()
    {
        HashSet<int> ticked = VersionSelectionReconciler.ComputeTicked(
            Installed, pendingExplicit: [560, 999], enabledMajors: new HashSet<int>());

        int[] expectedTicked = [560];
        Assert.Equal(expectedTicked, ticked.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void NoExplicit_TicksAllInstalledInEnabledMajors()
    {
        HashSet<int> ticked = VersionSelectionReconciler.ComputeTicked(
            Installed, pendingExplicit: null, enabledMajors: new HashSet<int> { 5 });

        int[] expectedTicked = [500, 560];
        Assert.Equal(expectedTicked, ticked.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void NoExplicit_NoEnabledMajors_TicksNothing()
    {
        HashSet<int> ticked = VersionSelectionReconciler.ComputeTicked(
            Installed, pendingExplicit: null, enabledMajors: new HashSet<int>());

        Assert.Empty(ticked);
    }

    [Fact]
    public void EmptyExplicit_TicksNothing()
    {
        HashSet<int> ticked = VersionSelectionReconciler.ComputeTicked(
            Installed, pendingExplicit: [], enabledMajors: new HashSet<int> { 5, 6 });

        Assert.Empty(ticked);  // an explicit (non-null) empty list wins over majors
    }
}
