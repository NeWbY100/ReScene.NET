using ReScene.App.Core.Helpers;
using ReScene.App.Core.Models;
namespace ReScene.App.Core.Tests;

public class FieldGuidanceTests
{
    [Fact]
    public void SuggestSiblingPath_ReplacesExtension_NextToInput()
    {
        string result = FieldGuidance.SuggestSiblingPath(@"C:\rel\movie.sample.mkv", ".srs");
        Assert.Equal(@"C:\rel\movie.sample.srs", result);
    }

    [Fact]
    public void SuggestSiblingPath_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, FieldGuidance.SuggestSiblingPath("", ".srr"));

    [Fact]
    public void EvaluateMediaAgainstSample_LargerMedia_IsOk()
    {
        FieldStatus status = FieldGuidance.EvaluateMediaAgainstSample(mediaSize: 700_000_000, sampleSize: 20_000_000);
        Assert.Equal(FieldState.Ok, status.State);
    }

    [Fact]
    public void EvaluateMediaAgainstSample_SmallerMedia_WarnsWrongFile()
    {
        FieldStatus status = FieldGuidance.EvaluateMediaAgainstSample(mediaSize: 10_000_000, sampleSize: 20_000_000);
        Assert.Equal(FieldState.Warning, status.State);
        Assert.Contains("smaller", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".mkv", FieldState.Ok)]
    [InlineData(".avi", FieldState.Ok)]
    [InlineData(".mp4", FieldState.Ok)]
    [InlineData(".mp3", FieldState.Ok)]
    [InlineData(".txt", FieldState.Warning)]
    public void DescribeSample_ClassifiesByExtension(string ext, FieldState expected)
    {
        FieldStatus status = FieldGuidance.DescribeSample(ext, sizeBytes: 24_000_000);
        Assert.Equal(expected, status.State);
    }

    [Fact]
    public void CountReleaseArchives_CountsRARAndOldStyleVolumes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.rar"), "");
            File.WriteAllText(Path.Combine(dir, "x.r00"), "");
            File.WriteAllText(Path.Combine(dir, "x.r01"), "");
            File.WriteAllText(Path.Combine(dir, "x.nfo"), "");
            Assert.Equal(3, FieldGuidance.CountReleaseArchives(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EvaluateMediaAgainstSample_ZeroSample_ReturnsNone()
    {
        FieldStatus status = FieldGuidance.EvaluateMediaAgainstSample(mediaSize: 700_000_000, sampleSize: 0);
        Assert.Equal(FieldState.None, status.State);
    }

    [Fact]
    public void SuggestSaveFileName_OutputIsFilePath_ReturnsIt()
    {
        string? result = FieldGuidance.SuggestSaveFileName(
            @"C:\out\custom.srs", @"C:\rel\movie.sample.mkv", ".srs");
        Assert.Equal(@"C:\out\custom.srs", result);
    }

    [Fact]
    public void SuggestSaveFileName_EmptyOutput_DerivesSiblingFromInput()
    {
        string? result = FieldGuidance.SuggestSaveFileName(
            "", @"C:\rel\movie.sample.mkv", ".srs");
        Assert.Equal(@"C:\rel\movie.sample.srs", result);
    }

    [Fact]
    public void SuggestSaveFileName_OutputIsDirectory_CombinesWithInputName()
    {
        // A configured default output directory leaves OutputPath holding a folder;
        // the dialog must still get a real file name.
        string dir = Path.Combine(Path.GetTempPath(), "fg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The input path must use this OS's separator: the suggestion takes its file name, and
            // on POSIX a backslash is an ordinary name character, not a directory boundary.
            string input = Path.Combine(Path.GetTempPath(), "rel", "movie.sample.mkv");
            string? result = FieldGuidance.SuggestSaveFileName(dir, input, ".srs");
            Assert.Equal(Path.Combine(dir, "movie.sample.srs"), result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SuggestSaveFileName_NothingToSuggest_ReturnsNull() => Assert.Null(FieldGuidance.SuggestSaveFileName("", "", ".srs"));
}
