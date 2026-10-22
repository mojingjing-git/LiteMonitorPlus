using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Geometry;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class PanelGeometryTests
{
    [Fact]
    public void RoundedMaskAndHitGeometryUseTheSameDistance()
    {
        PanelGeometry geometry = PanelGeometry.LiquidGlass(new LogicalRect(20, 30, 200, 100));

        Assert.Equal(1.0, geometry.MaskCoverage(120, 80, antialiasWidth: 0));
        Assert.True(geometry.ContainsHit(120, 80));
        Assert.Equal(0.0, geometry.MaskCoverage(20, 30, antialiasWidth: 0));
        Assert.False(geometry.ContainsHit(20, 30));
    }

    [Fact]
    public void ChamferCutsCornersFromMaskAndHitArea()
    {
        PanelGeometry geometry = PanelGeometry.CyberPunk(new LogicalRect(0, 0, 200, 100), cutLength: 14);

        Assert.False(geometry.ContainsHit(1, 1));
        Assert.True(geometry.ContainsHit(14, 1));
        Assert.True(geometry.ContainsHit(100, 50));
        Assert.False(geometry.ContainsHit(199, 99));
    }

    [Fact]
    public void SamplingMarginIncludesOpticInputsAndClampsAtTextureEdges()
    {
        PanelGeometry geometry = PanelGeometry.LiquidGlass(new LogicalRect(2, 3, 100, 60));

        SamplingBounds bounds = geometry.ClampSamplingBounds(120, 80);

        Assert.Equal(0, bounds.Left);
        Assert.Equal(0, bounds.Top);
        Assert.Equal(115, bounds.Right);
        Assert.Equal(76, bounds.Bottom);
        Assert.True(geometry.SamplingMargin >= geometry.BlurRadius);
        Assert.True(geometry.SamplingMargin >= geometry.RefractionRadius);
    }

    [Fact]
    public void VisualBoundsShareShapeButRespectClampedSafeSize()
    {
        PanelGeometry glass = PanelGeometry.LiquidGlass(new LogicalRect(10, 20, 200, 100));
        PanelGeometry cyber = PanelGeometry.CyberPunk(new LogicalRect(10, 20, 200, 100));

        Assert.Equal(-8, glass.VisualRectangle.X);
        Assert.Equal(4, cyber.VisualRectangle.X);
        Assert.Equal(14, cyber.CutLength);
        Assert.Equal(6, cyber.GlowRadius);
    }

    [Theory]
    [InlineData(PanelShapeKind.RoundedRectangle, 52, 0)]
    [InlineData(PanelShapeKind.ChamferedRectangle, 0, 52)]
    public void OversizedShapeParametersFailClosed(
        PanelShapeKind shape,
        double cornerRadius,
        double cutLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PanelGeometry(
                shape,
                new LogicalRect(0, 0, 100, 100),
                cornerRadius,
                cutLength,
                1,
                0,
                0,
                0,
                0));
    }

    [Fact]
    public void PremultipliedColorCannotExceedAlpha()
    {
        PremultipliedArgb color = PremultipliedArgb.FromStraight(0.5f, 1.0f, 0.25f, 0.0f);

        Assert.True(color.IsValid);
        Assert.Equal(128, color.Alpha);
        Assert.Equal(128, color.Red);
        Assert.Equal(32, color.Green);
        Assert.Equal(0, color.Blue);
    }
}
