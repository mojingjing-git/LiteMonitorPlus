using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    public class AutoStartMigrationTests
    {
        private const string TaskName = "LiteMonitor++_AutoStart";
        private const string LegacyTaskName = "LiteMonitor_AutoStart";

        private static readonly XNamespace TaskNs =
            "http://schemas.microsoft.com/windows/2004/02/mit/task";

        [Fact]
        public void Enable_CreatesVerifiesThenDeletesOwnedLegacyTask()
        {
            using var temp = new TemporaryDirectory("lm-autostart-success-");
            string installDir = Path.Combine(temp.Path, "Install");
            string currentExe = Path.Combine(installDir, "LiteMonitor++.exe");
            string legacyExe = Path.Combine(temp.Path, "install", "LiteMonitor.exe");

            var runner = new FakeTaskRunner
            {
                NewTaskXml = BuildTaskXml(currentExe),
                LegacyTaskXml = BuildTaskXml(legacyExe)
            };

            AutoStartResult result = AutoStartCore.SetTask(
                true,
                currentExe,
                TaskName,
                LegacyTaskName,
                runner,
                temp.Path);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(
                new[]
                {
                    "create-new",
                    "query-new",
                    "query-legacy",
                    "delete-legacy"
                },
                runner.Calls.Select(call => call.Kind));
            Assert.NotNull(runner.CreatedXml);
            XDocument created = XDocument.Parse(runner.CreatedXml!);
            Assert.Equal(TaskNs + "Task", created.Root!.Name);
            Assert.Equal("utf-16", created.Declaration!.Encoding, ignoreCase: true);
            Assert.Single(
                created.Descendants(),
                element => element.Name.LocalName == "Exec");
            Assert.Equal(
                Path.GetFullPath(currentExe),
                created.Descendants().Single(
                    element => element.Name.LocalName == "Command").Value);
            Assert.Equal("PT5S", created.Descendants().Single(
                element => element.Name.LocalName == "Delay").Value);
            Assert.Equal("InteractiveToken", created.Descendants().Single(
                element => element.Name.LocalName == "LogonType").Value);
            Assert.Equal("HighestAvailable", created.Descendants().Single(
                element => element.Name.LocalName == "RunLevel").Value);
            Assert.Equal("false", created.Descendants().Single(
                element => element.Name.LocalName ==
                    "DisallowStartIfOnBatteries").Value);
            Assert.Equal("false", created.Descendants().Single(
                element => element.Name.LocalName ==
                    "StopIfGoingOnBatteries").Value);
            Assert.False(File.Exists(runner.CreatedXmlPath));
        }

        [Fact]
        public void Enable_CreateExitFailure_KeepsLegacyTaskAndDoesNotQueryIt()
        {
            using var temp = new TemporaryDirectory("lm-autostart-create-fail-");
            string currentExe = Path.Combine(
                temp.Path, "LiteMonitor++.exe");
            var runner = new FakeTaskRunner
            {
                CreateResult = ProcessResult(exitCode: 5),
                NewTaskXml = BuildTaskXml(currentExe),
                LegacyTaskXml = BuildTaskXml(
                    Path.Combine(temp.Path, "LiteMonitor.exe"))
            };

            AutoStartResult result = AutoStartCore.SetTask(
                true,
                currentExe,
                TaskName,
                LegacyTaskName,
                runner,
                temp.Path);

            Assert.False(result.Succeeded);
            Assert.Equal(new[] { "create-new" }, runner.Calls.Select(
                call => call.Kind));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Kind == "delete-legacy");
        }

        [Fact]
        public void Enable_NewTaskVerificationFailure_KeepsLegacyTask()
        {
            using var temp = new TemporaryDirectory("lm-autostart-verify-fail-");
            string currentExe = Path.Combine(
                temp.Path, "Install", "LiteMonitor++.exe");
            var runner = new FakeTaskRunner
            {
                NewTaskXml = BuildTaskXml(
                    Path.Combine(temp.Path, "Other", "LiteMonitor++.exe")),
                LegacyTaskXml = BuildTaskXml(
                    Path.Combine(temp.Path, "Install", "LiteMonitor.exe"))
            };

            AutoStartResult result = AutoStartCore.SetTask(
                true,
                currentExe,
                TaskName,
                LegacyTaskName,
                runner,
                temp.Path);

            Assert.False(result.Succeeded);
            Assert.Equal(
                new[] { "create-new", "query-new" },
                runner.Calls.Select(call => call.Kind));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Kind == "delete-legacy");
        }

        [Theory]
        [InlineData("other-directory")]
        [InlineData("unknown-executable")]
        [InlineData("disabled-task")]
        [InlineData("query-failure")]
        [InlineData("malformed-xml")]
        [InlineData("multiple-actions")]
        public void Enable_UnknownLegacyOwnership_IsPreserved(
            string legacyScenario)
        {
            using var temp = new TemporaryDirectory("lm-autostart-unknown-");
            string installDir = Path.Combine(temp.Path, "Install");
            string currentExe = Path.Combine(installDir, "LiteMonitor++.exe");
            string legacyExe;
            string legacyXml;

            switch (legacyScenario)
            {
                case "other-directory":
                    legacyExe = Path.Combine(
                        temp.Path, "Other", "LiteMonitor.exe");
                    legacyXml = BuildTaskXml(legacyExe);
                    break;
                case "unknown-executable":
                    legacyExe = Path.Combine(installDir, "other.exe");
                    legacyXml = BuildTaskXml(legacyExe);
                    break;
                case "disabled-task":
                    legacyXml = BuildTaskXml(
                        Path.Combine(installDir, "LiteMonitor.exe"),
                        settingsEnabled: false);
                    break;
                case "query-failure":
                    legacyXml = "";
                    break;
                case "malformed-xml":
                    legacyXml = "<Task><Actions>";
                    break;
                default:
                    legacyXml = BuildTaskXml(
                        Path.Combine(installDir, "LiteMonitor.exe"),
                        secondCommand: Path.Combine(installDir, "other.exe"));
                    break;
            }

            var runner = new FakeTaskRunner
            {
                NewTaskXml = BuildTaskXml(currentExe),
                LegacyTaskXml = legacyXml
            };

            AutoStartResult result = AutoStartCore.SetTask(
                true,
                currentExe,
                TaskName,
                LegacyTaskName,
                runner,
                temp.Path);

            Assert.True(result.Succeeded, result.Message);
            Assert.Contains(runner.Calls, call => call.Kind == "query-legacy");
            Assert.DoesNotContain(runner.Calls, call =>
                call.Kind == "delete-legacy");
        }

        [Fact]
        public void Enable_LegacyCommandFromXmlIsNeverRun()
        {
            using var temp = new TemporaryDirectory("lm-autostart-no-execute-");
            string installDir = Path.Combine(temp.Path, "Install");
            string currentExe = Path.Combine(installDir, "LiteMonitor++.exe");
            string markerPath = Path.Combine(temp.Path, "marker.txt");
            var runner = new FakeTaskRunner
            {
                NewTaskXml = BuildTaskXml(currentExe),
                LegacyTaskXml = BuildTaskXml(
                    Path.Combine(installDir, "powershell.exe"),
                    arguments: $"-NoProfile -Command \"Set-Content '{markerPath}'\"")
            };

            AutoStartResult result = AutoStartCore.SetTask(
                true,
                currentExe,
                TaskName,
                LegacyTaskName,
                runner,
                temp.Path);

            Assert.True(result.Succeeded, result.Message);
            Assert.False(File.Exists(markerPath));
            Assert.All(runner.Calls, call =>
                Assert.Equal("schtasks.exe", call.FileName));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Kind == "delete-legacy");
        }

        [Fact]
        public void Enable_LegacyDeleteFailure_ReportsWarningButKeepsNewTask()
        {
            using var temp = new TemporaryDirectory("lm-autostart-delete-fail-");
            string installDir = Path.Combine(temp.Path, "Install");
            string currentExe = Path.Combine(installDir, "LiteMonitor++.exe");
            var runner = new FakeTaskRunner
            {
                NewTaskXml = BuildTaskXml(currentExe),
                LegacyTaskXml = BuildTaskXml(
                    Path.Combine(installDir, "LiteMonitor.exe")),
                LegacyDeleteResult = ProcessResult(exitCode: 2)
            };

            AutoStartResult result = AutoStartCore.SetTask(
                true,
                currentExe,
                TaskName,
                LegacyTaskName,
                runner,
                temp.Path);

            Assert.True(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.Warning));
            Assert.Contains(runner.Calls, call => call.Kind == "delete-legacy");
        }

        [Fact]
        public void Disable_CurrentTaskMissing_CleansOnlyOwnedLegacyTask()
        {
            using var temp = new TemporaryDirectory("lm-autostart-disable-");
            string installDir = Path.Combine(temp.Path, "Install");
            string currentExe = Path.Combine(installDir, "LiteMonitor++.exe");
            var runner = new FakeTaskRunner
            {
                NewTaskXml = "",
                LegacyTaskXml = BuildTaskXml(
                    Path.Combine(installDir, "LiteMonitor.exe"))
            };

            AutoStartResult result = AutoStartCore.SetTask(
                false,
                currentExe,
                TaskName,
                LegacyTaskName,
                runner,
                temp.Path);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(
                new[]
                {
                    "query-new",
                    "query-legacy",
                    "delete-legacy"
                },
                runner.Calls.Select(call => call.Kind));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Kind == "delete-current");
        }

        private static string BuildTaskXml(
            string command,
            bool settingsEnabled = true,
            string? secondCommand = null,
            string? arguments = null)
        {
            var exec = new XElement(
                TaskNs + "Exec",
                new XElement(TaskNs + "Command", command));
            if (arguments != null)
                exec.Add(new XElement(TaskNs + "Arguments", arguments));

            var actions = new List<XElement>
            {
                exec
            };
            if (secondCommand != null)
            {
                actions.Add(new XElement(
                    TaskNs + "Exec",
                    new XElement(TaskNs + "Command", secondCommand)));
            }

            return new XDocument(
                new XElement(
                    TaskNs + "Task",
                    new XElement(
                        TaskNs + "Settings",
                        new XElement(
                            TaskNs + "Enabled",
                            settingsEnabled ? "true" : "false")),
                    new XElement(TaskNs + "Actions", actions)))
                .ToString(SaveOptions.DisableFormatting);
        }

        private static AutoStartProcessResult ProcessResult(
            int exitCode,
            string output = "")
            => new()
            {
                Started = true,
                ExitCode = exitCode,
                StandardOutput = output
            };

        private sealed class FakeTaskRunner : IAutoStartProcessRunner
        {
            internal List<RunnerCall> Calls { get; } = new();
            internal string NewTaskXml { get; set; } = "";
            internal string? LegacyTaskXml { get; set; }
            internal AutoStartProcessResult CreateResult { get; set; } =
                ProcessResult(exitCode: 0);
            internal AutoStartProcessResult LegacyDeleteResult { get; set; } =
                ProcessResult(exitCode: 0);
            internal string? CreatedXml { get; private set; }
            internal string CreatedXmlPath { get; private set; } = "";

            public AutoStartProcessResult Run(string fileName, string arguments)
            {
                string kind = GetKind(arguments);
                Calls.Add(new RunnerCall(fileName, arguments, kind));

                switch (kind)
                {
                    case "create-new":
                        string xmlPath = ExtractXmlPath(arguments);
                        CreatedXmlPath = xmlPath;
                        CreatedXml = File.ReadAllText(xmlPath);
                        return CreateResult;
                    case "query-new":
                        return ProcessResult(exitCode: 0, output: NewTaskXml);
                    case "query-legacy":
                        return LegacyTaskXml == null
                            ? ProcessResult(exitCode: 1)
                            : ProcessResult(exitCode: 0, output: LegacyTaskXml);
                    case "delete-legacy":
                        return LegacyDeleteResult;
                    default:
                        return ProcessResult(exitCode: 0);
                }
            }

            private static string GetKind(string arguments)
            {
                if (arguments.StartsWith("/Create", StringComparison.Ordinal))
                    return "create-new";
                if (arguments.StartsWith("/Query", StringComparison.Ordinal) &&
                    arguments.Contains(
                        $"\"{TaskName}\"",
                        StringComparison.Ordinal))
                {
                    return "query-new";
                }
                if (arguments.StartsWith("/Query", StringComparison.Ordinal) &&
                    arguments.Contains(
                        $"\"{LegacyTaskName}\"",
                        StringComparison.Ordinal))
                {
                    return "query-legacy";
                }
                if (arguments.StartsWith("/Delete", StringComparison.Ordinal) &&
                    arguments.Contains(
                        $"\"{LegacyTaskName}\"",
                        StringComparison.Ordinal))
                {
                    return "delete-legacy";
                }
                if (arguments.StartsWith("/Delete", StringComparison.Ordinal))
                    return "delete-current";

                return "unknown";
            }

            private static string ExtractXmlPath(string arguments)
            {
                const string marker = "/XML \"";
                int start = arguments.IndexOf(marker, StringComparison.Ordinal) +
                            marker.Length;
                int end = arguments.IndexOf('"', start);
                Assert.True(start >= marker.Length && end > start);
                return arguments.Substring(start, end - start);
            }
        }

        private sealed record RunnerCall(
            string FileName,
            string Arguments,
            string Kind);

        private sealed class TemporaryDirectory : IDisposable
        {
            internal TemporaryDirectory(string prefix)
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    prefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch { }
            }
        }
    }
}
