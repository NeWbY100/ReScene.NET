using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Core;

namespace ReScene.App.Core.Tests;

public class ReconstructorViewModelSolidTests
{
    /// <summary>No-op dispatcher: runs everything inline on the calling thread.</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    /// <summary>Brute-force service that is never invoked in these mutual-exclusion tests.</summary>
    private sealed class InertBruteForceService : IBruteForceService
    {
        public event EventHandler<BruteForceProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<LogEventArgs>? LogMessage { add { } remove { } }
        public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress { add { } remove { } }
        public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress { add { } remove { } }
        public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed { add { } remove { } }

        public Task<BruteForceRunResult> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new BruteForceRunResult(false, null));
    }

    private static ReconstructorViewModel CreateVm() =>
        new(new InertBruteForceService(), new NoOpFileDialogService(), new InlineUiDispatcher(), new TestUiTimerFactory(), settingsService: null);

    [Fact]
    public void SwitchS_True_ClearsSwitchSDash()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.SwitchSDash = true;
        vm.SwitchS = true;
        Assert.True(vm.SwitchS);
        Assert.False(vm.SwitchSDash);
    }

    [Fact]
    public void SwitchSDash_True_ClearsSwitchS()
    {
        ReconstructorViewModel vm = CreateVm();
        vm.SwitchS = true;
        vm.SwitchSDash = true;
        Assert.True(vm.SwitchSDash);
        Assert.False(vm.SwitchS);
    }
}
