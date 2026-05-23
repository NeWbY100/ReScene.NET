using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReScene.NET.ViewModels;

public partial class TreeNodeViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDifferent { get; set; }

    public object? Tag
    {
        get; set;
    }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (Text.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (TreeNodeViewModel child in Children)
        {
            if (child.MatchesFilter(filter))
            {
                return true;
            }
        }

        return false;
    }

    public void ApplyFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            IsVisible = true;
            foreach (TreeNodeViewModel child in Children)
            {
                child.ApplyFilter(filter);
            }

            return;
        }

        bool matches = MatchesFilter(filter);
        IsVisible = matches;

        if (matches)
        {
            foreach (TreeNodeViewModel child in Children)
            {
                child.ApplyFilter(filter);
            }

            if (Children.Count > 0)
            {
                IsExpanded = true;
            }
        }
    }
}
