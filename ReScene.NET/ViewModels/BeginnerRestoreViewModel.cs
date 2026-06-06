using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Beginner "Restore a sample" flow. One input file is routed by extension: an .srr drives the
/// bulk <see cref="SampleRestorerViewModel"/>; a standalone .srs drives the single
/// <see cref="SRSReconstructorViewModel"/>.
/// </summary>
public partial class BeginnerRestoreViewModel(IFileDialogService fileDialog) : ViewModelBase
{
    private SampleRestorerViewModel? _bulkRestorer;
    public SampleRestorerViewModel? BulkRestorer
    {
        get => _bulkRestorer;
        set
        {
            _bulkRestorer = value;
            if (value is not null)
            {
                // Surface sub-VM changes (e.g. MediaDirectoryPath) as our own, so a hosting
                // wizard — which only observes this facade — re-evaluates its step gating.
                value.PropertyChanged += (_, _) => OnPropertyChanged(nameof(BulkRestorer));
            }
        }
    }

    private SRSReconstructorViewModel? _singleRebuilder;
    public SRSReconstructorViewModel? SingleRebuilder
    {
        get => _singleRebuilder;
        set
        {
            _singleRebuilder = value;
            if (value is not null)
            {
                value.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SingleRebuilder));
            }
        }
    }

    [ObservableProperty]
    public partial string InputPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBulk))]
    [NotifyPropertyChangedFor(nameof(IsSingle))]
    [NotifyPropertyChangedFor(nameof(ShowFlow))]
    public partial SampleRestoreKind Kind { get; set; }

    [ObservableProperty]
    public partial FieldStatus InputStatus { get; set; } = FieldStatus.None;

    public bool IsBulk => Kind == SampleRestoreKind.Srr;
    public bool IsSingle => Kind == SampleRestoreKind.Srs;
    public bool ShowFlow => Kind != SampleRestoreKind.Unknown;

    partial void OnInputPathChanged(string value)
    {
        Kind = SampleRestoreRouter.Route(value);

        switch (Kind)
        {
            case SampleRestoreKind.Srr:
                if (BulkRestorer is not null)
                {
                    BulkRestorer.SRRFilePath = value;
                }
                InputStatus = FieldStatus.Ok("SRR — will restore every embedded sample.");
                break;
            case SampleRestoreKind.Srs:
                if (SingleRebuilder is not null)
                {
                    SingleRebuilder.SRSFilePath = value;
                }
                InputStatus = FieldStatus.Ok("SRS — will rebuild this one sample.");
                break;
            default:
                InputStatus = string.IsNullOrWhiteSpace(value)
                    ? FieldStatus.None
                    : FieldStatus.Warning("Pick an .srr (whole release) or an .srs (single sample) file.");
                break;
        }
    }

    /// <summary>
    /// Clears this facade's state and cascades a reset to the bulk and single sub-VMs so a
    /// Beginner "Restore a sample" wizard opens clean.
    /// </summary>
    public void Reset()
    {
        InputPath = string.Empty;
        Kind = SampleRestoreKind.Unknown;
        InputStatus = FieldStatus.None;

        BulkRestorer?.Reset();
        SingleRebuilder?.Reset();
    }

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        string? path = await fileDialog.OpenFileAsync(
            "Select an SRR or SRS file", FileDialogFilters.SrrAndSrs);
        if (path is not null)
        {
            InputPath = path;
        }
    }
}
