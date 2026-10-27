using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using LiteMonitor.Diagnostics;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class DiagnosticPackageServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Preview_And_Zip_Use_Allowlisted_Model()
    {
        var snapshot = new DiagnosticSnapshot(Now, new[]
        {
            new DiagnosticModuleSnapshot(
                "hardware",
                DiagnosticModuleState.Ok,
                "ready",
                @"C:\Users\alice\secret.txt",
                Now,
                new Dictionary<string, string>
                {
                    ["state"] = "Ready",
                    ["path"] = @"C:\Users\alice\secret.txt",
                    ["maxSampleAgeMs"] = "120",
                }),
        });
        var events = new[]
        {
            new DiagnosticEvent(
                Now,
                "apply",
                DiagnosticSeverity.Warning,
                "apply-not-applied",
                "op-1",
                "session-1",
                new Dictionary<string, string>
                {
                    ["status"] = "Partial",
                    ["token"] = "secret-token",
                    ["path"] = @"C:\Users\alice\secret.txt",
                }),
        };

        var service = new DiagnosticPackageService();
        var input = new DiagnosticPackageInput(snapshot, events, "1.0-test", 2, 1, Now);
        DiagnosticPackagePreview preview = service.CreatePreview(input);

        Assert.Equal(1, preview.EventCount);
        Assert.Equal(1, preview.ModuleCount);
        Assert.Contains("events-dropped:2", preview.Warnings);
        Assert.Contains("events-rejected:1", preview.Warnings);

        string path = Path.Combine(Path.GetTempPath(), "lm-d04-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DiagnosticPackageExportResult result = service.Export(path, input);
            Assert.True(result.Success, result.ErrorCode);
            Assert.True(File.Exists(path));

            using ZipArchive zip = ZipFile.OpenRead(path);
            Assert.NotNull(zip.GetEntry("diagnostic.json"));
            Assert.NotNull(zip.GetEntry("summary.txt"));

            string json = ReadEntry(zip, "diagnostic.json");
            Assert.Contains("apply-not-applied", json);
            Assert.Contains("Partial", json);
            Assert.DoesNotContain("secret-token", json);
            Assert.DoesNotContain("Users", json);
            Assert.DoesNotContain(@"C:\\", json);

            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.Equal("litemonitor.diagnostics/1", doc.RootElement.GetProperty("schema").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("events").GetArrayLength());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Export_Leaves_No_Temp_File_On_Success()
    {
        var snapshot = new DiagnosticSnapshot(Now, Array.Empty<DiagnosticModuleSnapshot>());
        var input = new DiagnosticPackageInput(snapshot, Array.Empty<DiagnosticEvent>(), "test", 0, 0, Now);
        var service = new DiagnosticPackageService();
        string path = Path.Combine(Path.GetTempPath(), "lm-d04-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DiagnosticPackageExportResult result = service.Export(path, input);
            Assert.True(result.Success, result.ErrorCode);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-*"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Export_Rejects_Package_Over_10MiB()
    {
        var snapshot = new DiagnosticSnapshot(Now, Array.Empty<DiagnosticModuleSnapshot>());
        var events = new[]
        {
            new DiagnosticEvent(Now, "huge", DiagnosticSeverity.Info, "huge", null, "s",
                new Dictionary<string, string> { ["status"] = new string('x', 11 * 1024 * 1024) }),
        };
        var input = new DiagnosticPackageInput(snapshot, events, "test", 0, 0, Now);
        var service = new DiagnosticPackageService();
        string path = Path.Combine(Path.GetTempPath(), "lm-d04-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DiagnosticPackageExportResult result = service.Export(path, input);
            Assert.False(result.Success);
            Assert.Equal("package-too-large", result.ErrorCode);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        ZipArchiveEntry entry = zip.GetEntry(name) ?? throw new InvalidDataException(name);
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
