using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
using System.Xml.Linq; // 新增：用于 XDocument

namespace LiteMonitor.src.SystemServices
{
    public static class AutoStart
    {
        // 当前品牌的计划任务名
        private const string TaskName = "LiteMonitor++_AutoStart";
        // 品牌迁移前创建遗留的计划任务名。首次启用自启时需清理，
        // 否则老用户升级后会同时存在新旧两个任务，导致开机启动两次。
        private const string LegacyTaskName = "LiteMonitor_AutoStart";

        /// <summary>
        /// 删除旧品牌名（LiteMonitor_AutoStart）遗留的计划任务。
        /// 任务不存在时 schtasks 返回非 0 退出码，属正常情况，静默忽略。
        /// 清理失败不阻断自启设置。
        /// </summary>
        private static void DeleteLegacyTask()
        {
            if (string.IsNullOrEmpty(LegacyTaskName) ||
                string.Equals(LegacyTaskName, TaskName, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{LegacyTaskName}\" /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
                // 退出码非 0 = 任务不存在，属正常情况，无需提示
            }
            catch
            {
                // 清理失败不应阻断自启动设置
            }
        }

        public static void Set(bool enabled)
        {
            string exePath = Process.GetCurrentProcess().MainModule!.FileName!;

            // 1. 网络路径拦截 (保留你的原始逻辑)
            try
            {
                string root = Path.GetPathRoot(exePath)!;
                if ((!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network) || new Uri(exePath).IsUnc)
                {
                    MessageBox.Show("Windows 计划任务不支持在网络路径下运行。\n请移动到本地硬盘。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch { }

            if (enabled)
            {
                if (IsEnabledForCurrentExe(exePath)) return;

                // 清理旧品牌名遗留的计划任务（仅首次；任务不存在时 schtasks 返回非 0，忽略即可）
                DeleteLegacyTask();

                // 使用 XML 方案，这是唯一能同时满足 [不报PowerShell错误] + [实现电池启动] 的方案
                string tempXmlPath = Path.Combine(Path.GetTempPath(), $"LiteMonitor++_Task_{Guid.NewGuid()}.xml");

                try
                {
                    // 生成 XML 内容 (修改为获取 XDocument 对象)
                    var doc = GetTaskXml(exePath);
                    
                    // 写入临时文件 (修改为 doc.Save，它会自动处理 UTF-16 编码)
                    doc.Save(tempXmlPath);

                    // 调用 schtasks 导入 XML
                    // /F: 强制覆盖
                    // /TN: 任务名
                    // /XML: 指定配置文件
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Create /TN \"{TaskName}\" /XML \"{tempXmlPath}\" /F",
                        CreateNoWindow = true,
                        UseShellExecute = false // 必须为 false 才能配合 CreateNoWindow 隐藏窗口
                    };
                    
                    using (var p = Process.Start(startInfo))
                    {
                        p?.WaitForExit();
                        
                        // 可选：检查退出码，如果非0则记录日志
                        // if (p.ExitCode != 0) { ... }
                    }
                }
                catch (Exception ex)
                {
                    // 捕获所有 IO 或 进程异常
                    MessageBox.Show($"设置失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    // 确保无论是否出错，都尝试清理临时文件
                    try
                    {
                        if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath);
                    }
                    catch { /* 忽略删除失败，临时文件残留无害 */ }
                }
            }
            else
            {
                // 删除任务 (逻辑保持不变)
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Delete /TN \"{TaskName}\" /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (var p = Process.Start(startInfo))
                {
                    p?.WaitForExit();
                }
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static bool IsEnabledForCurrentExe(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\" /XML")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;

                    string xml = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(xml)) return false;

                    var doc = XDocument.Parse(xml);
                    var command = doc.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "Command")
                        ?.Value;
                    var enabled = doc.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "Enabled" &&
                                             x.Parent?.Name.LocalName == "Settings")
                        ?.Value;

                    // 如果任务被用户或系统禁用，即使命令路径一致，也不能跳过重建/启用。
                    bool taskEnabled = !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
                    return taskEnabled && string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 生成 XML 配置：完美复刻原始逻辑 + 增加高级电池/延迟设置
        /// (已重构为 XDocument 方式)
        /// </summary>
        private static XDocument GetTaskXml(string exePath)
        {
            // 细节保留：获取工作目录，对应你原始代码的 /STRTIN
            string exeDir = Path.GetDirectoryName(exePath)!;

            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            // 使用 XDocument 构建 XML
            // 自动处理特殊字符转义（如路径中的 & ' 等）
            // 自动处理编码声明 (UTF-16)
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

        // 原 EscapeXml 方法已移除，因 XDocument 会自动处理转义
    }
}
