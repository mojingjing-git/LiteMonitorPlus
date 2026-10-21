using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace LiteMonitor.src.SystemServices
{
    internal interface IAutoStartProcessRunner
    {
        AutoStartProcessResult Run(string fileName, string arguments);
    }

    internal sealed class AutoStartProcessResult
    {
        internal bool Started { get; init; }
        internal bool TimedOut { get; init; }
        internal int ExitCode { get; init; }
        internal string StandardOutput { get; init; } = "";
        internal string StandardError { get; init; } = "";

        internal bool Succeeded => Started && !TimedOut && ExitCode == 0;
    }

    internal sealed class SystemAutoStartProcessRunner : IAutoStartProcessRunner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        internal static SystemAutoStartProcessRunner Instance { get; } = new();

        public AutoStartProcessResult Run(string fileName, string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo(fileName, arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using Process process = new() { StartInfo = startInfo };
                if (!process.Start())
                {
                    return new AutoStartProcessResult();
                }

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { process.WaitForExit(1000); } catch { }

                    return new AutoStartProcessResult
                    {
                        Started = true,
                        TimedOut = true,
                        ExitCode = -1
                    };
                }

                string output = outputTask.GetAwaiter().GetResult();
                string error = errorTask.GetAwaiter().GetResult();
                return new AutoStartProcessResult
                {
                    Started = true,
                    ExitCode = process.ExitCode,
                    StandardOutput = output,
                    StandardError = error
                };
            }
            catch (Exception ex)
            {
                return new AutoStartProcessResult
                {
                    ExitCode = -1,
                    StandardError = ex.Message
                };
            }
        }
    }

    internal static class AutoStartCore
    {
        private static readonly HashSet<string> KnownInstallationExecutables =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "LiteMonitor++.exe",
                "LiteMonitor.exe"
            };

        internal static AutoStartResult SetTask(
            bool enabled,
            string exePath,
            string taskName,
            string legacyTaskName,
            IAutoStartProcessRunner runner,
            string tempDirectory)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            if (!TryNormalizeFile(exePath, out string normalizedExe))
            {
                return AutoStartResult.Failure(
                    "应用程序路径不是绝对路径。");
            }

            if (string.IsNullOrWhiteSpace(taskName))
            {
                return AutoStartResult.Failure("任务名为空。");
            }

            if (!enabled)
                return Disable(normalizedExe, taskName, legacyTaskName, runner);

            return Enable(
                normalizedExe,
                taskName,
                legacyTaskName,
                runner,
                tempDirectory);
        }

        internal static bool IsCurrentTaskCorrect(
            string exePath,
            string taskName,
            IAutoStartProcessRunner runner)
        {
            if (!TryNormalizeFile(exePath, out string normalizedExe) ||
                string.IsNullOrWhiteSpace(taskName) ||
                !TryQueryTaskXml(runner, taskName, out string xml))
            {
                return false;
            }

            return TryGetTaskExecCommand(xml, out string command) &&
                   TryNormalizeFile(command, out string normalizedCommand) &&
                   string.Equals(
                       normalizedCommand,
                       normalizedExe,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static AutoStartResult Enable(
            string exePath,
            string taskName,
            string legacyTaskName,
            IAutoStartProcessRunner runner,
            string tempDirectory)
        {
            string? xmlPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(tempDirectory))
                {
                    return AutoStartResult.Failure(
                        "临时目录为空。");
                }

                Directory.CreateDirectory(tempDirectory);
                xmlPath = Path.Combine(
                    tempDirectory,
                    $"LiteMonitor++_Task_{Guid.NewGuid():N}.xml");
                CreateTaskXml(exePath).Save(xmlPath);

                AutoStartProcessResult createResult = runner.Run(
                    "schtasks.exe",
                    $"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F");
                if (!createResult.Succeeded)
                {
                    return AutoStartResult.Failure(
                        DescribeProcessFailure(
                            "无法创建新的自启任务。",
                            createResult));
                }

                if (!TryQueryTaskXml(runner, taskName, out string newTaskXml) ||
                    !TryGetTaskExecCommand(newTaskXml, out string newCommand) ||
                    !TryNormalizeFile(newCommand, out string normalizedNewCommand) ||
                    !string.Equals(
                        normalizedNewCommand,
                        exePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return AutoStartResult.Failure(
                        "新自启任务核验失败，旧任务已保留。");
                }

                LegacyCleanupResult legacy = CleanupOwnedLegacyTask(
                    exePath,
                    legacyTaskName,
                    runner);
                if (legacy.DeleteFailed)
                {
                    return AutoStartResult.SuccessWithWarning(
                        "新自启任务已生效，但旧任务清理失败。");
                }

                return AutoStartResult.Success();
            }
            catch (Exception ex)
            {
                return AutoStartResult.Failure(
                    "无法创建新的自启任务: " + ex.Message);
            }
            finally
            {
                if (xmlPath != null)
                {
                    try { File.Delete(xmlPath); } catch { }
                }
            }
        }

        private static AutoStartResult Disable(
            string exePath,
            string taskName,
            string legacyTaskName,
            IAutoStartProcessRunner runner)
        {
            if (TryQueryTaskXml(runner, taskName, out _))
            {
                AutoStartProcessResult deleteResult = runner.Run(
                    "schtasks.exe",
                    $"/Delete /TN \"{taskName}\" /F");
                if (!deleteResult.Succeeded)
                {
                    return AutoStartResult.Failure(
                        DescribeProcessFailure(
                            "无法移除当前自启任务。",
                            deleteResult));
                }
            }

            LegacyCleanupResult legacy = CleanupOwnedLegacyTask(
                exePath,
                legacyTaskName,
                runner);
            return legacy.DeleteFailed
                ? AutoStartResult.SuccessWithWarning(
                    "旧自启任务无法移除。")
                : AutoStartResult.Success();
        }

        private static LegacyCleanupResult CleanupOwnedLegacyTask(
            string currentExePath,
            string legacyTaskName,
            IAutoStartProcessRunner runner)
        {
            if (string.IsNullOrWhiteSpace(legacyTaskName) ||
                !TryQueryTaskXml(runner, legacyTaskName, out string legacyXml) ||
                !IsOwnedBySameInstallation(legacyXml, currentExePath))
            {
                return new LegacyCleanupResult(false);
            }

            AutoStartProcessResult deleteResult = runner.Run(
                "schtasks.exe",
                $"/Delete /TN \"{legacyTaskName}\" /F");
            return new LegacyCleanupResult(!deleteResult.Succeeded);
        }

        internal static bool IsOwnedBySameInstallation(
            string taskXml,
            string currentExePath)
        {
            if (!TryGetTaskExecCommand(taskXml, out string legacyCommand) ||
                !TryNormalizeFile(legacyCommand, out string normalizedLegacy) ||
                !TryNormalizeFile(currentExePath, out string normalizedCurrent))
            {
                return false;
            }

            if (string.Equals(
                    normalizedLegacy,
                    normalizedCurrent,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? legacyDirectory = Path.GetDirectoryName(normalizedLegacy);
            string? currentDirectory = Path.GetDirectoryName(normalizedCurrent);
            string legacyName = Path.GetFileName(normalizedLegacy);
            return legacyDirectory != null &&
                   currentDirectory != null &&
                   string.Equals(
                       legacyDirectory,
                       currentDirectory,
                       StringComparison.OrdinalIgnoreCase) &&
                   KnownInstallationExecutables.Contains(legacyName);
        }

        internal static XDocument CreateTaskXml(string exePath)
            => AutoStart.GetTaskXmlForCore(exePath);

        private static bool TryQueryTaskXml(
            IAutoStartProcessRunner runner,
            string taskName,
            out string xml)
        {
            xml = "";
            AutoStartProcessResult result = runner.Run(
                "schtasks.exe",
                $"/Query /TN \"{taskName}\" /XML");
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
                return false;

            xml = result.StandardOutput;
            return true;
        }

        private static bool TryGetTaskExecCommand(
            string taskXml,
            out string command)
        {
            command = "";
            if (string.IsNullOrWhiteSpace(taskXml)) return false;

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                using var textReader = new StringReader(taskXml);
                using XmlReader xmlReader = XmlReader.Create(textReader, settings);
                XDocument document = XDocument.Load(xmlReader, LoadOptions.None);

                List<XElement> actionsElements = document
                    .Descendants()
                    .Where(element => element.Name.LocalName == "Actions")
                    .ToList();
                if (actionsElements.Count != 1) return false;

                List<XElement> actions = actionsElements[0].Elements().ToList();
                if (actions.Count != 1 ||
                    actions[0].Name.LocalName != "Exec")
                {
                    return false;
                }

                List<XElement> commands = actions[0]
                    .Elements()
                    .Where(element => element.Name.LocalName == "Command")
                    .ToList();
                if (commands.Count != 1) return false;

                List<XElement> enabledSettings = document
                    .Descendants()
                    .Where(element =>
                        element.Name.LocalName == "Enabled" &&
                        element.Parent?.Name.LocalName == "Settings")
                    .ToList();
                if (enabledSettings.Count != 1 ||
                    !string.Equals(
                        enabledSettings[0].Value.Trim(),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                command = commands[0].Value.Trim();
                return command.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryNormalizeFile(string path, out string normalized)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(path)) return false;

            string candidate = path.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(candidate)) return false;

            try
            {
                normalized = Path.GetFullPath(candidate);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeProcessFailure(
            string prefix,
            AutoStartProcessResult result)
        {
            if (!result.Started) return prefix + " " + result.StandardError;
            if (result.TimedOut) return prefix + " 进程等待超时。";

            string details = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            return string.IsNullOrWhiteSpace(details)
                ? $"{prefix} 退出码: {result.ExitCode}。"
                : $"{prefix} 退出码: {result.ExitCode}。{details.Trim()}";
        }
    }

    internal sealed class AutoStartResult
    {
        private AutoStartResult(
            bool succeeded,
            string message,
            string warning)
        {
            Succeeded = succeeded;
            Message = message;
            Warning = warning;
        }

        internal bool Succeeded { get; }
        internal string Message { get; }
        internal string Warning { get; }

        internal static AutoStartResult Success()
            => new(true, "", "");

        internal static AutoStartResult SuccessWithWarning(string warning)
            => new(true, "", warning);

        internal static AutoStartResult Failure(string message)
            => new(false, message, "");
    }

    internal readonly struct LegacyCleanupResult
    {
        internal LegacyCleanupResult(bool deleteFailed)
        {
            DeleteFailed = deleteFailed;
        }

        internal bool DeleteFailed { get; }
    }
}
