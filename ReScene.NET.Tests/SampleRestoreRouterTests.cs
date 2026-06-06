using ReScene.NET.Helpers;

namespace ReScene.NET.Tests;

public class SampleRestoreRouterTests
{
    [Theory]
    [InlineData(@"C:\rel\movie.srr", SampleRestoreKind.Srr)]
    [InlineData(@"C:\rel\movie.SRR", SampleRestoreKind.Srr)]
    [InlineData(@"C:\rel\movie.sample.srs", SampleRestoreKind.Srs)]
    [InlineData(@"C:\rel\movie.SRS", SampleRestoreKind.Srs)]
    [InlineData(@"C:\rel\movie.mkv", SampleRestoreKind.Unknown)]
    [InlineData("", SampleRestoreKind.Unknown)]
    [InlineData(null, SampleRestoreKind.Unknown)]
    public void Route_ClassifiesByExtension(string? path, SampleRestoreKind expected)
        => Assert.Equal(expected, SampleRestoreRouter.Route(path));
}
