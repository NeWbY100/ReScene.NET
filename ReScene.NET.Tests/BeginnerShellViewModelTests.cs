using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

public class BeginnerShellViewModelTests
{
    [Fact]
    public void NewShell_StartsOnHub()
    {
        var vm = new BeginnerShellViewModel();
        Assert.True(vm.IsHubVisible);
        Assert.Null(vm.CurrentCard);
        Assert.False(vm.ShowCreateSrr);
    }

    [Fact]
    public void OpenCard_LeavesHub_AndSetsTheMatchingShowFlag()
    {
        var vm = new BeginnerShellViewModel();
        vm.OpenCardCommand.Execute(BeginnerCard.Reconstruct);

        Assert.False(vm.IsHubVisible);
        Assert.Equal(BeginnerCard.Reconstruct, vm.CurrentCard);
        Assert.True(vm.ShowReconstruct);
        Assert.False(vm.ShowCreateSrr);
    }

    [Fact]
    public void BackToHub_ReturnsToHub()
    {
        var vm = new BeginnerShellViewModel();
        vm.OpenCardCommand.Execute(BeginnerCard.CreateSrs);
        vm.BackToHubCommand.Execute(null);

        Assert.True(vm.IsHubVisible);
        Assert.Null(vm.CurrentCard);
    }

    [Fact]
    public void OpenInAdvanced_InvokesCallbackWithCard()
    {
        BeginnerCard? captured = null;
        var vm = new BeginnerShellViewModel { OpenInAdvancedAction = c => captured = c };
        vm.OpenInAdvancedCommand.Execute(BeginnerCard.Restore);

        Assert.Equal(BeginnerCard.Restore, captured);
    }
}
