using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Xml.Linq;

namespace LiteMonitor.src.SystemServices
{
    public static class AutoStart
    {
        // 当前品牌的计划任务名。
        private const string TaskName = "LiteMonitor++_AutoStart";

        // 品牌迁移前的计划任务名，仅用于确认归属后清理。
        private const string LegacyTaskName = "LiteMonitor_AutoStart";

        public static void Set(bool enabled)
        {
            string exePath = Process.GetCurrentProcess().MainModule!.FileName!;

            try
            {
                string root = Path.GetPathRoot(exePath)!;
                if ((!string.IsNullOrEmpty(root) &&
                     new DriveInfo(root).DriveType == DriveType.Network) ||
                    new Uri(exePath).IsUnc)
                {
                    MessageBox.Show(
                        "Windows 计划任务不支持在网络路径下运行。\n请移动到本地硬盘。",
                        "错误",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
            }
            catch { }

            AutoStartResult result = AutoStartCore.SetTask(
                enabled,
                exePath,
                TaskName,
                LegacyTaskName,
                SystemAutoStartProcessRunner.Instance,
                Path.GetTempPath());

            if (!result.Succeeded)
            {
                MessageBox.Show(
                    $"设置失败: {result.Message}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            else if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                MessageBox.Show(
                    "自启已启用，但旧任务清理未完成",
                    "自启",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule!.FileName!;
                return AutoStartCore.IsCurrentTaskCorrect(
                    exePath,
                    TaskName,
                    SystemAutoStartProcessRunner.Instance);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 构造 XML 配置，沿用原发布版的登录触发、延迟启动和最高权限设置。
        /// 使用 XDocument 形式由运行时处理特殊字符转义和 XML 编码。
        /// </summary>
        private static XDocument GetTaskXml(string exePath)
        {
            // 读取程序目录，保持工作目录与可执行文件所在安装目录一致。
            string exeDir = Path.GetDirectoryName(exePath)!;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-16", null),
                new XElement(ns + "Task",
                    new XAttribute("version", "1.2"),
                    new XElement(ns + "RegistrationInfo",
                        new XElement(ns + "Description", "LiteMonitor++ Auto Start")
                    ),
                    new XElement(ns + "Triggers",
                        new XElement(ns + "LogonTrigger",
                            new XElement(ns + "Enabled", "true"),
                            new XElement(ns + "Delay", "PT5S")
                        )
                    ),
                    new XElement(ns + "Principals",
                        new XElement(ns + "Principal",
                            new XAttribute("id", "Author"),
                            new XElement(ns + "LogonType", "InteractiveToken"),
                            new XElement(ns + "RunLevel", "HighestAvailable")
                        )
                    ),
                    new XElement(ns + "Settings",
                        new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                        new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                        new XElement(ns + "StopIfGoingOnBatteries", "false"),
                        new XElement(ns + "AllowHardTerminate", "true"),
                        new XElement(ns + "StartWhenAvailable", "false"),
                        new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                        new XElement(ns + "IdleSettings",
                            new XElement(ns + "StopOnIdleEnd", "true"),
                            new XElement(ns + "RestartOnIdle", "false")
                        ),
                        new XElement(ns + "AllowStartOnDemand", "true"),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "Hidden", "false"),
                        new XElement(ns + "RunOnlyIfIdle", "false"),
                        new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                        new XElement(ns + "Priority", "7")
                    ),
                    new XElement(ns + "Actions",
                        new XAttribute("Context", "Author"),
                        new XElement(ns + "Exec",
                            new XElement(ns + "Command", exePath),
                            new XElement(ns + "WorkingDirectory", exeDir)
                        )
                    )
                )
            );

            return doc;
        }

        internal static XDocument GetTaskXmlForCore(string exePath)
            => GetTaskXml(exePath);
    }
}
