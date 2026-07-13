using DeskBox.Controls.WidgetContents;

namespace DeskBox.Tests;

public sealed class MusicWidgetContentLayoutTests
{
    [Theory]
    [InlineData(150, 150)]
    [InlineData(179.9, 240)]
    [InlineData(320, 179.9)]
    public void ShouldUseMinimalLayout_UsesCoverLayoutBelow180(double width, double height)
    {
        Assert.True(MusicWidgetContent.ShouldUseMinimalLayout(width, height));
    }

    [Theory]
    [InlineData(180, 180)]
    [InlineData(320, 190)]
    [InlineData(400, 260)]
    public void ShouldUseMinimalLayout_UsesFullControlsFrom180(double width, double height)
    {
        Assert.False(MusicWidgetContent.ShouldUseMinimalLayout(width, height));
    }
}
