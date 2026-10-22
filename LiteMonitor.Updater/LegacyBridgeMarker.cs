using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteMonitor.Updater
{
    /// <summary>
    /// "升级通道与当前版本不兼容"的机器可读标记。
    /// </summary>
    /// <remarks>
    /// ★ 为什么要留这个文件：旧客户端用单参数调用更新器，新更新器按契约拒绝没有摘要的调用。
    ///   旧客户端随即 <c>Application.Exit()</c>，于是用户看到的是"应用自己关掉了，什么提示都没有"。
    ///   这个标记把那次失败的**原因**和**下一步该做什么**（手动下载新版本）落到磁盘上，
    ///   让后来的（能读这个标记的）新客户端在启动时明确告诉用户，而不是让用户反复重试。
    /// ★ 标记写在**主程序目录**（与 update_success / update_error.log 同级），
    ///   因为那里的内容最容易被主程序自己找到，也最容易被人手工看到。
    /// ★ 字段名是跨进程契约：主程序侧读同名字段
    ///   （<c>src/System/UpdateChecker.cs</c> 的 <c>LegacyBridgeNotice</c>）。
    ///   两边各有一份实现，用例靠"更新器写 → 主程序读"的端到端用例盯着漂移。
    /// ★ 标记里**只有诊断信息与公开的发布页地址**，绝不写入任何私钥材料。
    /// </remarks>
    internal static class LegacyBridgeMarker
    {
        /// <summary>标记文件名（放在主程序目录下）。</summary>
        internal const string MarkerFileName = "update_legacy_bridge.json";

        /// <summary>标记的 schema 版本。</summary>
        internal const string Schema = "liteMonitor.legacyBridge/v1";

        /// <summary>原因码：调用方没有给出期望摘要（LM-016）。</summary>
        internal const string CodeMissingDigest = "missing_digest_rejected";

        /// <summary>给用户的下一步指引（中文）。</summary>
        internal const string Guidance =
            "自动升级通道与当前版本不兼容，请手动下载新版本。";

        /// <summary>手工下载入口（与主程序 <c>UpdateChecker.ReleasePageUrl</c> 保持一致）。</summary>
        internal const string ManualDownloadUrl =
            "https://github.com/mojingjing-git/LiteMonitorPlus/releases";

        /// <summary>标记的 JSON 载荷。</summary>
        internal sealed class Payload
        {
            [JsonPropertyName("schema")]
            public string Schema { get; set; } = LegacyBridgeMarker.Schema;

            [JsonPropertyName("code")]
            public string Code { get; set; } = string.Empty;

            [JsonPropertyName("reason")]
            public string Reason { get; set; } = string.Empty;

            [JsonPropertyName("guidance")]
            public string Guidance { get; set; } = string.Empty;

            [JsonPropertyName("manualDownloadUrl")]
            public string ManualDownloadUrl { get; set; } = string.Empty;

            [JsonPropertyName("updaterVersion")]
            public string UpdaterVersion { get; set; } = string.Empty;

            [JsonPropertyName("timestamp")]
            public string Timestamp { get; set; } = string.Empty;
        }

        /// <summary>
        /// 写出标记文件。失败只返回 false，**不抛异常** —— 写标记失败不能改变"拒绝执行"的结论。
        /// </summary>
        /// <param name="baseDir">主程序目录（标记写在这里）。</param>
        /// <param name="code">原因码。</param>
        /// <param name="reason">人可读的失败原因。</param>
        /// <param name="updaterVersion">写标记的更新器版本。</param>
        /// <param name="path">成功时输出标记的完整路径。</param>
        internal static bool TryWrite(
            string baseDir, string code, string reason, string updaterVersion, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(baseDir)) return false;

            try
            {
                var payload = new Payload
                {
                    Code = code,
                    Reason = reason,
                    Guidance = Guidance,
                    ManualDownloadUrl = ManualDownloadUrl,
                    UpdaterVersion = updaterVersion,
                    Timestamp = DateTime.Now.ToString("O")
                };

                string json = JsonSerializer.Serialize(
                    payload, new JsonSerializerOptions { WriteIndented = true });

                path = Path.Combine(baseDir, MarkerFileName);
                File.WriteAllText(path, json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
