using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReScene.NET.ViewModels;

public partial class TreeNodeViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isDifferent;

    public object? Tag { get; set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        if (Text.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var child in Children)
        {
            if (child.MatchesFilter(filter))
                return true;
        }

        return false;
    }

    public void ApplyFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            IsVisible = true;
            foreach (var child in Children)
                child.ApplyFilter(filter);
            return;
        }

        bool matches = MatchesFilter(filter);
        IsVisible = matches;

        if (matches)
        {
            foreach (var child in Children)
                child.ApplyFilter(filter);

            if (Children.Count > 0)
                IsExpanded = true;
        }
    }
}
