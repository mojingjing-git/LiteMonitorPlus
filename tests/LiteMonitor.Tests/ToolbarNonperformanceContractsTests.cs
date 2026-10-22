using System.Runtime.InteropServices;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Materials;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class ToolbarNonperformanceContractsTests
{
    [Fact]
    public void MaterialBufferMatchesHlslVectorSlots()
    {
        string[] slots = ["Viewport", "RootRect", "Panel0", "Panel1", "Panel2", "Panel3",
            "Panel4", "Panel5", "Panel6", "Panel7", "Panel8", "Panel9", "Panel10", "Panel11",
            "Theme0", "Theme1", "Theme2", "Theme3", "Theme4", "Theme5", "Motion", "Desktop", "Window", "State"];
        Assert.Equal(384, Marshal.SizeOf<MaterialFrameConstants>());
        for (int i = 0; i < slots.Length; i++)
            Assert.Equal(i * 16, Marshal.OffsetOf<MaterialFrameConstants>(slots[i]).ToInt32());
    }

    [Fact]
    public void FloatingDefaultsRetainV4MistAndEdgeUnits()
    {
        var config = Glass();
        config.Parameters["glassStyle"] = JsonSerializer.SerializeToElement("floatingToolbar");
        Assert.Empty(config.Validate());
        var settings = MaterialRuntimeSettings.Create(config, MaterialKind.LiquidGlass);
        Assert.Equal(GlassSurfaceStyle.FloatingToolbar, settings.GlassStyle);
        Assert.Equal(8, settings.Thickness);
        Assert.Equal(8, settings.BlurRadius);
        Assert.Equal(0.16f, settings.FogStrength);
        Assert.Equal(0.038f, settings.TintStrength);
        Assert.Equal(30, settings.CornerRadius);
    }

    [Fact]
    public void MissingStylePreservesStandardDefaults()
    {
        var settings = MaterialRuntimeSettings.Create(Glass(), MaterialKind.LiquidGlass);
        Assert.Equal(GlassSurfaceStyle.Standard, settings.GlassStyle);
        Assert.Equal(1, settings.Thickness);
        Assert.Equal(0.055f, settings.TintStrength);
        Assert.Equal("Segoe UI Variable", settings.ForegroundTheme.DisplayFontFamily);
        Assert.Equal("Cascadia Mono", settings.ForegroundTheme.CaptionFontFamily);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(7)]
    public void InvalidStyleIsRejected(object value)
    {
        var config = Glass();
        config.Parameters["glassStyle"] = JsonSerializer.SerializeToElement(value);
        Assert.NotEmpty(config.Validate());
    }

    [Fact]
    public void FixedInputOwnsCopyAndRejectsIncompletePixels()
    {
        byte[] bytes = [10, 20, 30, 255];
        Assert.True(FixedBackgroundInput.TryCreate(bytes, 1, 1, 4, 96,
            new PixelRectI(0, 0, 1, 1), "fixture-v1", out var input, out _, out _));
        bytes[0] = 99;
        Assert.Equal(10, input!.Bgra8Pixels.Span[0]);
        Assert.False(FixedBackgroundInput.TryCreate(bytes, 2, 1, 8, 96,
            new PixelRectI(0, 0, 2, 1), "fixture-v1", out _, out _, out _));
    }

    private static ThemeRenderingConfig Glass() => new()
    {
        Backend = "gpu", Material = "liquid_glass",
        Parameters = new() { ["liveBackground"] = JsonSerializer.SerializeToElement(false) }
    };
}
