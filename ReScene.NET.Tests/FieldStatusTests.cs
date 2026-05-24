using ReScene.NET.Models;

namespace ReScene.NET.Tests;

public class FieldStatusTests
{
    [Fact]
    public void None_HasNoneStateAndEmptyMessage()
    {
        Assert.Equal(FieldState.None, FieldStatus.None.State);
        Assert.Equal(string.Empty, FieldStatus.None.Message);
    }

    [Fact]
    public void Ok_SetsStateAndMessage()
    {
        FieldStatus status = FieldStatus.Ok("Found 3 volumes");
        Assert.Equal(FieldState.Ok, status.State);
        Assert.Equal("Found 3 volumes", status.Message);
    }

    [Fact]
    public void Warning_And_Error_SetState()
    {
        Assert.Equal(FieldState.Warning, FieldStatus.Warning("hmm").State);
        Assert.Equal(FieldState.Error, FieldStatus.Error("nope").State);
        Assert.Equal(FieldState.Info, FieldStatus.Info("fyi").State);
    }
}
