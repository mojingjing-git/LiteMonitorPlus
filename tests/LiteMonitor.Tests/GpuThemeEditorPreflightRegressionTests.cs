using System;
using System.IO;
using System.Text;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// Independent N04 preflight regressions. This file intentionally does not
/// alter the shared theme-editor tests.
/// </summary>
[Collection("cfgfile")]
public sealed class GpuThemeEditorPreflightRegressionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;
    private readonly byte[]? _settingsBefore;

    public GpuThemeEditorPreflightRegressionTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Tests",
            "GpuThemeEditorPreflightRegressionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _settingsPath = SettingsHelper.FilePath;
        _settingsBefore = File.Exists(_settingsPath)
            ? File.ReadAllBytes(_settingsPath)
            : null;

        SettingsHelper.GlobalBlockSave = false;
    }

    public void Dispose()
    {
        try
        {
            SettingsHelper.GlobalBlockSave = false;

            if (_settingsBefore is null)
            {
                if (File.Exists(_settingsPath))
                {
                    File.Delete(_settingsPath);
                }
            }
            else
            {
                File.WriteAllBytes(_settingsPath, _settingsBefore);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
                // Temporary-file cleanup must not hide the test result.
            }
        }
    }

    [Fact]
    public void Unknown_fields_survive_load_save_preview_copy_and_nested_rendering_clone()
    {
        const string json = """
            {
              "name": "UnknownFields",
              "version": 5,
              "unknownRootKey": { "shape": "object" },
              "layout": {
                "rowHeight": 40,
                "unknownLayoutKey": [1, 2, 3]
              },
              "font": {
                "family": "Segoe UI Variable",
                "unknownFontKey": true
              },
              "thresholds": {
                "load": {
                  "warn": 65,
                  "crit": 85,
                  "unknownThresholdKey": null
                }
              },
              "color": {
                "background": "#101820",
                "unknownColorKey": "keep-color"
              },
              "backdrop": {
                "mode": "gradient",
                "unknownBackdropKey": 7
              },
              "rendering": {
                "version": 1,
                "backend": "gpu",
                "material": "liquid_glass",
                "parameters": {
                  "liveBackground": false,
                  "customParameter": "keep-parameter"
                },
                "motion": {
                  "targetFps": 30
                },
                "fallback": {
                  "backend": "legacy",
                  "material": "legacy"
                },
                "unknownRenderingKey": "keep-rendering"
              }
            }
            """;

        File.WriteAllText(
            Path.Combine(_dir, "UnknownFields.json"),
            json,
            new UTF8Encoding(false));

        Theme loaded = ThemeFileService.LoadTheme(_dir, "UnknownFields");
        Theme preview = ThemePreviewControl.CreatePreviewTheme(loaded, 1.0f);

        Assert.NotNull(loaded.Rendering);
        Assert.NotNull(preview.Rendering);
        Assert.NotSame(loaded.Rendering, preview.Rendering);
        Assert.Equal(
            "keep-rendering",
            preview.Rendering!.ExtensionData["unknownRenderingKey"].GetString());
        Assert.Equal(
            "keep-parameter",
            preview.Rendering.Parameters["customParameter"].GetString());

        preview.Rendering.Parameters["previewOnly"] =
            JsonSerializer.SerializeToElement(true);
        Assert.False(loaded.Rendering.Parameters.ContainsKey("previewOnly"));

        ThemeFileService.SaveTheme(_dir, "Saved", loaded);
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_dir, "Saved.json")));
        JsonElement root = document.RootElement;
        JsonElement rendering = Required(root, "Rendering");

        Assert.Equal(
            "object",
            Required(Required(root, "unknownRootKey"), "shape").GetString());
        Assert.Equal(
            3,
            Required(root, "Layout").GetProperty("unknownLayoutKey")[2].GetInt32());
        Assert.True(Required(root, "Font").GetProperty("unknownFontKey").GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            Required(Required(root, "Thresholds"), "Load")
                .GetProperty("unknownThresholdKey").ValueKind);
        Assert.Equal(
            "keep-color",
            Required(root, "Color").GetProperty("unknownColorKey").GetString());
        Assert.Equal(
            7,
            Required(root, "Backdrop").GetProperty("unknownBackdropKey").GetInt32());
        Assert.Equal(
            "keep-rendering",
            rendering.GetProperty("unknownRenderingKey").GetString());
        Assert.Equal(
            "keep-parameter",
            Required(rendering, "parameters").GetProperty("customParameter").GetString());
    }

    [Fact]
    public void Cancel_restores_rendering_and_extensions_without_touching_disk()
    {
        Theme theme = CreateRenderingTheme("Cancel");
        ThemeFileService.SaveTheme(_dir, "Cancel", theme);

        string path = Path.Combine(_dir, "Cancel.json");
        byte[] bytesBefore = File.ReadAllBytes(path);

        Theme loaded = ThemeFileService.LoadTheme(_dir, "Cancel");
        Theme pristine = AppearanceThemeConfig.CloneConfig(loaded);
        Theme rootReference = loaded;
        LayoutConfig layoutReference = loaded.Layout;
        ThemeRenderingConfig renderingReference = loaded.Rendering!;

        loaded.Name = "Changed";
        loaded.Layout.RowHeight = 99;
        loaded.Rendering!.Backend = ThemeRenderingConfig.LegacyBackend;
        loaded.Rendering.Material = ThemeRenderingConfig.LegacyMaterial;
        loaded.Rendering.Parameters["mutated"] =
            JsonSerializer.SerializeToElement(123);
        loaded.Rendering.ExtensionData["temporary"] =
            JsonSerializer.SerializeToElement("remove-me");
        loaded.ExtensionData["temporaryRoot"] =
            JsonSerializer.SerializeToElement(true);

        AppearanceThemeConfig.RestoreConfig(loaded, pristine);

        Assert.Same(rootReference, loaded);
        Assert.Same(layoutReference, loaded.Layout);
        Assert.Same(renderingReference, loaded.Rendering);
        Assert.Equal("Cancel", loaded.Name);
        Assert.Equal(40, loaded.Layout.RowHeight);
        Assert.Equal(ThemeRenderingConfig.GpuBackend, loaded.Rendering!.Backend);
        Assert.Equal(ThemeRenderingConfig.LiquidGlassMaterial, loaded.Rendering.Material);
        Assert.False(loaded.Rendering.Parameters.ContainsKey("mutated"));
        Assert.False(loaded.Rendering.ExtensionData.ContainsKey("temporary"));
        Assert.Equal(
            "keep-rendering",
            loaded.Rendering.ExtensionData["unknownRenderingKey"].GetString());
        Assert.False(loaded.ExtensionData.ContainsKey("temporaryRoot"));
        Assert.Equal(bytesBefore, File.ReadAllBytes(path));

        loaded.Rendering.Parameters["afterRestore"] =
            JsonSerializer.SerializeToElement(456);
        Assert.False(pristine.Rendering!.Parameters.ContainsKey("afterRestore"));
    }

    [Fact]
    public void Read_only_configuration_blocks_settings_save_without_changing_file()
    {
        byte[] original = Encoding.UTF8.GetBytes(
            """{"PreferredDisk":"ORIGINAL","Skin":"DarkFlat_Classic"}""");
        File.WriteAllBytes(_settingsPath, original);

        try
        {
            SettingsHelper.GlobalBlockSave = true;

            new Settings
            {
                PreferredDisk = "SHOULD-NOT-BE-WRITTEN",
                Skin = "LiquidGlass_GPU"
            }.Save();

            Assert.True(SettingsHelper.GlobalBlockSave);
            Assert.Equal(original, File.ReadAllBytes(_settingsPath));
        }
        finally
        {
            SettingsHelper.GlobalBlockSave = false;
        }
    }

    [Fact]
    public void Duplicate_theme_preserves_unknown_bytes_even_when_source_is_read_only()
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(
            """
            {
              "Name":"ReadOnlySource",
              "Rendering":{
                "version":1,
                "backend":"gpu",
                "material":"cyber_punk",
                "parameters":{"liveBackground":false,"unknownParameter":"kept"},
                "motion":{},
                "fallback":{"backend":"legacy","material":"legacy"},
                "unknownRenderingKey":"kept"
              },
              "unknownRootKey":"kept"
            }
            """);

        string sourcePath = Path.Combine(_dir, "ReadOnlySource.json");
        File.WriteAllBytes(sourcePath, sourceBytes);
        File.SetAttributes(sourcePath, FileAttributes.ReadOnly);

        try
        {
            ThemeFileService.DuplicateTheme(_dir, "ReadOnlySource", "Clone");
        }
        finally
        {
            File.SetAttributes(sourcePath, FileAttributes.Normal);
        }

        Assert.Equal(
            sourceBytes,
            File.ReadAllBytes(Path.Combine(_dir, "Clone.json")));
        Assert.DoesNotContain(
            Directory.GetFiles(_dir),
            path => path.EndsWith(
                ThemeFileService.TempFileSuffix,
                StringComparison.OrdinalIgnoreCase));
    }

    private static Theme CreateRenderingTheme(string name)
    {
        var theme = new Theme { Name = name, Version = 5 };
        theme.Layout.RowHeight = 40;
        theme.Rendering = new ThemeRenderingConfig
        {
            Version = ThemeRenderingConfig.CurrentVersion,
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial
        };
        theme.Rendering.Parameters["liveBackground"] =
            JsonSerializer.SerializeToElement(false);
        theme.Rendering.Parameters["surfaceOpacity"] =
            JsonSerializer.SerializeToElement(0.055);
        theme.Rendering.ExtensionData["unknownRenderingKey"] =
            JsonSerializer.SerializeToElement("keep-rendering");
        theme.ExtensionData["unknownRootKey"] =
            JsonSerializer.SerializeToElement("keep-root");
        return theme;
    }

    private static JsonElement Required(JsonElement parent, string name)
    {
        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected property '{name}' in {parent.GetRawText()}.");
    }
}
