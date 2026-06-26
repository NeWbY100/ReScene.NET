using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

public class VersionEntryTests
{
    [Fact]
    public void NewRow_HasStartText_AndBlankEndAndDuration()
    {
        var row = new ReconstructorViewModel.VersionEntry();
        Assert.Equal(8, row.StartText.Length); // HH:mm:ss
        Assert.Equal(string.Empty, row.EndText);
        Assert.Equal(string.Empty, row.DurationText);
    }

    [Fact]
    public void Complete_StampsEnd_AndDuration()
    {
        var row = new ReconstructorViewModel.VersionEntry();
        row.Status = "Complete";
        Assert.NotNull(row.EndedAt);
        Assert.False(string.IsNullOrEmpty(row.EndText));
        Assert.False(string.IsNullOrEmpty(row.DurationText));
    }

    [Fact]
    public void TerminalStatus_IsIdempotent_DoesNotMoveEnd()
    {
        var row = new ReconstructorViewModel.VersionEntry();
        row.Status = "Complete";
        DateTime? first = row.EndedAt;
        row.Status = "Error";
        Assert.Equal(first, row.EndedAt);
    }

    [Fact]
    public void WhileTesting_EndAndDuration_AreBlank()
    {
        var row = new ReconstructorViewModel.VersionEntry();
        row.Status = "Testing"; // no-op vs the default; must not stamp an end
        Assert.Null(row.EndedAt);
        Assert.Equal(string.Empty, row.EndText);
        Assert.Equal(string.Empty, row.DurationText);
    }
}
