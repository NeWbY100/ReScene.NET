using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

public class FilePreviewViewModelTests
{
    private static BitmapSource DummyImage()
        => BitmapSource.Create(2, 3, 96, 96, PixelFormats.Bgr24, null, new byte[2 * 3 * 3], 2 * 3);

    [Fact]
    public void NonImage_HasNoImageTab_AndDecodesText()
    {
        byte[] data = Encoding.ASCII.GetBytes("HELLO_NFO");
        var vm = new FilePreviewViewModel(data, "readme.nfo", image: null);

        Assert.False(vm.HasImageTab);
        Assert.Null(vm.Image);
        Assert.Equal(data.Length, vm.HexBlockLength);
        Assert.Equal("UTF-8", vm.SelectedEncoding.DisplayName);
        Assert.Equal("HELLO_NFO", vm.TextViewContent);
        Assert.False(vm.TextViewTruncated);
    }

    [Fact]
    public void Image_HasImageTab()
    {
        var vm = new FilePreviewViewModel([0x01, 0x02], "proof.jpg", image: DummyImage());

        Assert.True(vm.HasImageTab);
        Assert.NotNull(vm.Image);
    }

    [Fact]
    public void ChangingEncoding_Redecodes()
    {
        // 0xC9 → CP437 '╔' (U+2554) vs Latin-1 'É' (U+00C9).
        var vm = new FilePreviewViewModel([0xC9], "enc.bin", image: null);

        vm.SelectedEncoding = vm.TextEncodings.First(e => e.DisplayName == "CP437 (DOS)");
        Assert.Contains('╔', vm.TextViewContent);

        vm.SelectedEncoding = vm.TextEncodings.First(e => e.DisplayName == "ISO-8859-1 (Latin-1)");
        Assert.Contains('É', vm.TextViewContent);
        Assert.DoesNotContain('╔', vm.TextViewContent);
    }
}
