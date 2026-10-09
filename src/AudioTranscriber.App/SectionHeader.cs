using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace AudioTranscriber.App;

/// <summary>A small-caps section title followed by a fading gold rule.</summary>
public sealed class SectionHeader : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Text));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}

public static class UiFonts
{
    public static FontFeatureCollection SmallCaps { get; } = [FontFeature.Parse("+smcp")];
}
