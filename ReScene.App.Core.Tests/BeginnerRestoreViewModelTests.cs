using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;
using ReScene.App.Core.ViewModels;
namespace ReScene.App.Core.Tests;

public class BeginnerRestoreViewModelTests
{
    [Fact]
    public void NewVm_HasNoFlowSelected()
    {
        var vm = new BeginnerRestoreViewModel(fileDialog: null!);
        Assert.Equal(SampleRestoreKind.Unknown, vm.Kind);
        Assert.False(vm.ShowFlow);
        Assert.False(vm.IsBulk);
        Assert.False(vm.IsSingle);
    }

    [Fact]
    public void SettingSRRInput_SelectsBulkFlow()
    {
        var vm = new BeginnerRestoreViewModel(fileDialog: null!) { InputPath = @"C:\rel\movie.srr" };
        Assert.Equal(SampleRestoreKind.SRR, vm.Kind);
        Assert.True(vm.IsBulk);
        Assert.False(vm.IsSingle);
        Assert.True(vm.ShowFlow);
        Assert.Equal(FieldState.Ok, vm.InputStatus.State);
    }

    [Fact]
    public void SettingSRSInput_SelectsSingleFlow()
    {
        var vm = new BeginnerRestoreViewModel(fileDialog: null!) { InputPath = @"C:\rel\movie.srs" };
        Assert.Equal(SampleRestoreKind.SRS, vm.Kind);
        Assert.True(vm.IsSingle);
        Assert.False(vm.IsBulk);
    }

    [Fact]
    public void SettingUnknownInput_WarnsAndHidesFlow()
    {
        var vm = new BeginnerRestoreViewModel(fileDialog: null!) { InputPath = @"C:\rel\movie.mkv" };
        Assert.Equal(SampleRestoreKind.Unknown, vm.Kind);
        Assert.False(vm.ShowFlow);
        Assert.Equal(FieldState.Warning, vm.InputStatus.State);
    }
}
