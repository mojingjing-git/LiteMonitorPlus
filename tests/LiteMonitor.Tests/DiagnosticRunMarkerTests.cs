using System;
using System.IO;
using LiteMonitor.Diagnostics;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class DiagnosticRunMarkerTests
{
    [Fact]
    public void FirstRunHasNoMarker_AndCleanShutdownIsReportedNextTime()
    {
        string path = Path.Combine(Path.GetTempPath(), "litemonitor-diag-" + Guid.NewGuid().ToString("N"), "last-run.json");
        try
        {
            DiagnosticRunMarker.PreviousRun first = DiagnosticRunMarker.ReadAndBegin(path);
            Assert.False(first.HadMarker);

            DiagnosticRunMarker.MarkCleanShutdown(path);
            DiagnosticRunMarker.PreviousRun second = DiagnosticRunMarker.ReadAndBegin(path);

            Assert.True(second.HadMarker);
            Assert.True(second.CleanShutdown);
        }
        finally
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UncleanRun_IsReportedAsNotClean()
    {
        string path = Path.Combine(Path.GetTempPath(), "litemonitor-diag-" + Guid.NewGuid().ToString("N"), "last-run.json");
        try
        {
            DiagnosticRunMarker.ReadAndBegin(path);
            DiagnosticRunMarker.PreviousRun next = DiagnosticRunMarker.ReadAndBegin(path);

            Assert.True(next.HadMarker);
            Assert.False(next.CleanShutdown);
        }
        finally
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
