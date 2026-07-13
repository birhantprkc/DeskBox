using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class WeatherWidgetViewModelTests
{
    [Theory]
    [InlineData(150, 104)]
    [InlineData(168, 116)]
    public void DetermineLayoutMode_KeepsSmallWidgetsInMini(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Mini");

        Assert.Equal("Mini", layout);
    }

    [Theory]
    [InlineData(180, 134)]
    [InlineData(200, 154)]
    public void DetermineLayoutMode_UsesCompactLayoutFromMediumWidgetSize(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Mini");

        Assert.Equal("Compact", layout);
    }

    [Fact]
    public void DetermineLayoutMode_UsesHysteresisNearMediumBoundary()
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(174, 122, "Compact");

        Assert.Equal("Compact", layout);
    }
}
