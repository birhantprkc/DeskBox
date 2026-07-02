using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class MusicBarViewModel : ObservableObject
{
    private const int EqualizerSegmentCount = 10;

    private double _height;
    private double _opacity = 1;
    private double _dotSize = 4;
    private double _equalizerValue = 0.25;
    private Color _accentColor = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    private Color _glassStrokeColor = Color.FromArgb(0x66, 0x00, 0x78, 0xD4);
    private Color _glassStartColor = Color.FromArgb(0xCC, 0x00, 0x78, 0xD4);
    private Color _glassMidColor = Color.FromArgb(0x88, 0x00, 0x78, 0xD4);
    private Color _glassEndColor = Color.FromArgb(0x2E, 0x00, 0x78, 0xD4);

    public MusicBarViewModel(double height)
    {
        _height = height;
        for (int level = EqualizerSegmentCount - 1; level >= 0; level--)
        {
            EqualizerSegments.Add(new MusicEqualizerSegmentViewModel(level));
        }
    }

    public ObservableCollection<MusicEqualizerSegmentViewModel> EqualizerSegments { get; } = [];

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }

    public double DotSize
    {
        get => _dotSize;
        set => SetProperty(ref _dotSize, value);
    }

    public Color AccentColor
    {
        get => _accentColor;
        private set => SetProperty(ref _accentColor, value);
    }

    public Color GlassStrokeColor
    {
        get => _glassStrokeColor;
        private set => SetProperty(ref _glassStrokeColor, value);
    }

    public Color GlassStartColor
    {
        get => _glassStartColor;
        private set => SetProperty(ref _glassStartColor, value);
    }

    public Color GlassMidColor
    {
        get => _glassMidColor;
        private set => SetProperty(ref _glassMidColor, value);
    }

    public Color GlassEndColor
    {
        get => _glassEndColor;
        private set => SetProperty(ref _glassEndColor, value);
    }

    public void ApplyAccentColor(Color value)
    {
        AccentColor = value;
        GlassStrokeColor = Color.FromArgb(0x6A, value.R, value.G, value.B);
        GlassStartColor = Color.FromArgb(0xD6, value.R, value.G, value.B);
        GlassMidColor = Color.FromArgb(0x94, value.R, value.G, value.B);
        GlassEndColor = Color.FromArgb(0x34, value.R, value.G, value.B);
    }

    public void ApplyEqualizerFrame(
        double value,
        int columnIndex,
        int columnCount,
        Color accentColor,
        bool isPlaying)
    {
        _equalizerValue = Math.Clamp(value, 0.0, 1.0);
        Color columnColor = accentColor;
        Color inactiveColor = Blend(accentColor, Color.FromArgb(0xFF, 0x0B, 0x12, 0x1C), 0.44);
        Color peakColor = Lighten(accentColor, 0.24);

        int activeSegments = Math.Clamp((int)Math.Round(_equalizerValue * (EqualizerSegmentCount - 2)) + 1, 1, EqualizerSegmentCount - 1);
        int peakLevel = Math.Clamp(activeSegments, 1, EqualizerSegmentCount - 1);
        double playbackOpacity = isPlaying ? 1.0 : 0.58;

        foreach (var segment in EqualizerSegments)
        {
            bool isPeak = segment.Level == peakLevel;
            bool isActive = segment.Level < activeSegments;
            segment.Color = isPeak ? peakColor : isActive ? columnColor : inactiveColor;
            segment.Opacity = (isPeak ? 1.0 : isActive ? 0.74 : 0.18) * playbackOpacity;
        }
    }

    public void ReapplyEqualizerFrame(int columnIndex, int columnCount, Color accentColor, bool isPlaying)
    {
        ApplyEqualizerFrame(_equalizerValue, columnIndex, columnCount, accentColor, isPlaying);
    }

    private static Color Blend(Color first, Color second, double amount)
    {
        double normalizedAmount = Math.Clamp(amount, 0.0, 1.0);
        double inverse = 1 - normalizedAmount;
        return Color.FromArgb(
            0xFF,
            ToByte((first.R * inverse) + (second.R * normalizedAmount)),
            ToByte((first.G * inverse) + (second.G * normalizedAmount)),
            ToByte((first.B * inverse) + (second.B * normalizedAmount)));
    }

    private static Color Lighten(Color value, double amount)
    {
        double normalizedAmount = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            0xFF,
            ToByte(value.R + ((255 - value.R) * normalizedAmount)),
            ToByte(value.G + ((255 - value.G) * normalizedAmount)),
            ToByte(value.B + ((255 - value.B) * normalizedAmount)));
    }

    private static byte ToByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }
}

public sealed partial class MusicEqualizerSegmentViewModel : ObservableObject
{
    private Color _color = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    private double _opacity = 0.18;

    public MusicEqualizerSegmentViewModel(int level)
    {
        Level = level;
    }

    public int Level { get; }

    public Color Color
    {
        get => _color;
        set => SetProperty(ref _color, value);
    }

    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }
}
