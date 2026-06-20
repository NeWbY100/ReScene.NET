using ReScene.NET.Helpers;

namespace ReScene.NET.Tests;

public class ImagePreviewSupportTests
{
    [Theory]
    [InlineData("proof.jpg")]
    [InlineData("proof.jpeg")]
    [InlineData("cover.png")]
    [InlineData("anim.gif")]
    [InlineData("pic.bmp")]
    [InlineData("PROOF.JPG")]
    [InlineData("Cover.PnG")]
    [InlineData("folder/sub/extraproof-2011.jpg")]
    public void IsSupported_ImageExtensions_True(string name)
        => Assert.True(ImagePreviewSupport.IsSupported(name));

    [Theory]
    [InlineData("readme.nfo")]
    [InlineData("files.sfv")]
    [InlineData("notes.txt")]
    [InlineData("playlist.m3u")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData("trailingdot.")]
    public void IsSupported_NonImage_False(string name)
        => Assert.False(ImagePreviewSupport.IsSupported(name));
}
