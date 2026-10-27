using System;
using System.IO;
using System.Text;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// TM01：客户端安装路径与仓库 CLI 共用同一套内容校验。
/// 这些用例锁住“严格 UTF-8 / 转义远程引用 / 实际大小 / route 互验”四条红线。
/// </summary>
public class ThemeMarketValidatorTests
{
    private static string FixtureRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "theme-repo");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return "";
        }
    }

    [Fact]
    public void 拒收_非法UTF8()
    {
        byte[] raw = { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' };

        ThemeMarketValidationResult result = ThemeMarketValidator.Validate(raw, "legacy", "legacy");

        Assert.False(result.IsValid);
        Assert.Equal("theme_utf8_invalid", result.ErrorCode);
    }

    [Fact]
    public void 拒收_转义后的远程引用()
    {
        const string json = "{\"name\":\"http:\\/\\/evil.example\\/x\",\"rendering\":{\"backend\":\"gpu\",\"material\":\"liquid_glass\"}}";
        byte[] raw = Encoding.UTF8.GetBytes(json);

        ThemeMarketValidationResult result = ThemeMarketValidator.Validate(raw, "gpu", "liquid_glass");

        Assert.False(result.IsValid);
        Assert.Equal("theme_remote_reference", result.ErrorCode);
    }

    [Fact]
    public void 拒收_超限载荷()
    {
        byte[] raw = new byte[ThemeMarketValidator.MaxThemeBytes + 1];
        Array.Fill(raw, (byte)' ');

        ThemeMarketValidationResult result = ThemeMarketValidator.Validate(raw, "legacy", "legacy");

        Assert.False(result.IsValid);
        Assert.Equal("theme_too_large", result.ErrorCode);
    }

    [Fact]
    public void 拒收_声明大小与实际不一致()
    {
        byte[] raw = Encoding.UTF8.GetBytes("{}");

        ThemeMarketValidationResult result = ThemeMarketValidator.Validate(raw, "legacy", "legacy", declaredSize: raw.Length + 1);

        Assert.False(result.IsValid);
        Assert.Equal("theme_size_mismatch", result.ErrorCode);
    }

    [Fact]
    public void 接受_合法GPU主题()
    {
        string path = Path.Combine(FixtureRoot, "themes", "market-aurora-gpu.json");
        byte[] raw = File.ReadAllBytes(path);

        ThemeMarketValidationResult result = ThemeMarketValidator.Validate(raw, "gpu", "liquid_glass", raw.Length);

        Assert.True(result.IsValid, result.ErrorCode + ": " + result.Message);
    }

    [Fact]
    public void 接受_无rendering的legacy主题()
    {
        string path = Path.Combine(FixtureRoot, "themes", "market-paper-legacy.json");
        byte[] raw = File.ReadAllBytes(path);

        ThemeMarketValidationResult result = ThemeMarketValidator.Validate(raw, "legacy", "legacy", raw.Length);

        Assert.True(result.IsValid, result.ErrorCode + ": " + result.Message);
    }

    [Fact]
    public void 拒收_route与内容不一致()
    {
        string path = Path.Combine(FixtureRoot, "themes", "market-aurora-gpu.json");
        byte[] raw = File.ReadAllBytes(path);

        ThemeMarketValidationResult result = ThemeMarketValidator.Validate(raw, "legacy", "legacy", raw.Length);

        Assert.False(result.IsValid);
        Assert.Equal("theme_route_mismatch", result.ErrorCode);
    }
}
