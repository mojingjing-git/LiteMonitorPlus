using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LiteMonitor;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// ★ **独立复核 · LM-069 / R05 · 旧客户端 → 新更新器协议桥接（主程序侧）**
/// <para>复核者自写用例。判据与实现者的 <c>UpdaterLegacyBridgeTests</c> **不同**：
/// 本文件额外用**自写 IL 读取器**独立证明"读标记排在任何网络请求**之前**"，
/// 并且用**跨进程**方式（直接跑更新器 exe）验证标记确实是更新器写出来的。</para>
/// </summary>
public sealed class VerifierLm069Tests
{
    private const string MarkerFileName = "update_legacy_bridge.json";

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "verifier-lm069-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteMarker(string dir, string code, string reason, string guidance, string url)
    {
        string json = "{" +
            "\"schema\":\"liteMonitor.legacyBridge/v1\"," +
            "\"code\":" + Quote(code) + "," +
            "\"reason\":" + Quote(reason) + "," +
            "\"guidance\":" + Quote(guidance) + "," +
            "\"manualDownloadUrl\":" + Quote(url) + "," +
            "\"updaterVersion\":\"1.2.3\"," +
            "\"timestamp\":\"2026-10-23T00:00:00.0000000+08:00\"" +
            "}";
        File.WriteAllText(Path.Combine(dir, MarkerFileName), json, new UTF8Encoding(false));
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ---------------------------------------------------------------- V1

    /// <summary>
    /// V1（**载荷用例**）：主程序启动时**读得到**标记，内容含原因与手动升级指引。
    /// </summary>
    [Fact]
    public void V1_主程序读得到标记且含原因与手动升级指引()
    {
        string dir = NewTempDir();
        try
        {
            WriteMarker(dir, "missing_digest_rejected", "调用方没有提供期望 SHA-256 摘要",
                "自动升级通道与当前版本不兼容，请手动下载新版本。",
                "https://github.com/mojingjing-git/LiteMonitorPlus/releases");

            LegacyBridgeNotice? notice = UpdateChecker.TryConsumeLegacyBridgeNotice(dir);

            Assert.True(notice != null, "主程序必须读得到标记。");
            Assert.Equal("missing_digest_rejected", notice!.Code);
            Assert.True(notice.Reason.Contains("SHA-256", StringComparison.Ordinal), "原因必须说明缺的是摘要。");
            Assert.True(notice.Guidance.Contains("手动下载", StringComparison.Ordinal), "指引必须让用户手动下载。");
            Assert.True(notice.ManualDownloadUrl.Contains("releases", StringComparison.Ordinal));
            Assert.NotEqual("", notice.Timestamp);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V2

    /// <summary>
    /// V2（**载荷用例**）：标记**消费后不重复提示** —— 文件被删除，第二次读不到。
    /// </summary>
    [Fact]
    public void V2_标记消费后不重复提示()
    {
        string dir = NewTempDir();
        try
        {
            WriteMarker(dir, "missing_digest_rejected", "原因", "手动下载指引", "https://example.invalid/releases");

            int presented = 0;
            Action<LegacyBridgeNotice> presenter = _ => presented++;

            UpdateChecker.ReportLegacyBridgeNoticeIfAny(dir, presenter);
            Assert.True(presented == 1, "第一次必须提示一次。");
            Assert.False(
                File.Exists(Path.Combine(dir, MarkerFileName)),
                "标记读过后必须被消费（删除）⇒ 不会每次启动重复弹。");

            UpdateChecker.ReportLegacyBridgeNoticeIfAny(dir, presenter);
            Assert.True(presented == 1, "消费后不得重复提示。");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V3

    /// <summary>V3：提示正文必须包含"手动下载"的指引与发布页地址。</summary>
    [Fact]
    public void V3_提示正文含手动升级指引与发布页()
    {
        string dir = NewTempDir();
        try
        {
            WriteMarker(dir, "missing_digest_rejected", "调用方没有提供期望摘要", "手动下载新版本", "https://example.invalid/releases");
            LegacyBridgeNotice? notice = UpdateChecker.TryConsumeLegacyBridgeNotice(dir);
            Assert.True(notice != null);

            string zh = LegacyBridgeNotice.BuildMessage(true, notice!);
            Assert.True(zh.Contains("手动下载", StringComparison.Ordinal), "中文提示必须给出手动下载指引：" + zh);
            Assert.True(zh.Contains("example.invalid", StringComparison.Ordinal));

            string en = LegacyBridgeNotice.BuildMessage(false, notice!);
            Assert.True(en.Contains("manually", StringComparison.OrdinalIgnoreCase), "英文提示必须给出手动下载指引：" + en);
            Assert.True(en.Contains("example.invalid", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V4

    /// <summary>V4：没有标记 / 标记损坏 ⇒ 不提示、不抛异常。</summary>
    [Fact]
    public void V4_没有标记或标记损坏时不提示也不抛异常()
    {
        string dir = NewTempDir();
        try
        {
            int presented = 0;
            Action<LegacyBridgeNotice> presenter = _ => presented++;

            UpdateChecker.ReportLegacyBridgeNoticeIfAny(dir, presenter);
            Assert.True(presented == 0, "没有标记时不得提示。");

            File.WriteAllText(Path.Combine(dir, MarkerFileName), "{ this is not json");
            LegacyBridgeNotice? broken = UpdateChecker.TryConsumeLegacyBridgeNotice(dir);
            Assert.True(broken == null, "损坏的标记必须被当作「没有标记」。");
            Assert.False(
                File.Exists(Path.Combine(dir, MarkerFileName)),
                "损坏的标记也应当被清掉，避免每次启动重复解析。");

            UpdateChecker.ReportLegacyBridgeNoticeIfAny(dir, presenter);
            Assert.True(presented == 0, "损坏标记不得触发提示。");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V5

    /// <summary>
    /// V5（**载荷用例**）：读标记必须**排在任何网络请求之前** ——
    /// 否则网络再慢，用户也看不到"自动升级通道不可用"这个结论。
    /// </summary>
    [Fact]
    public void V5_读标记排在任何网络请求之前()
    {
        MethodInfo check = typeof(UpdateChecker).GetMethod(
            "CheckAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo report = typeof(UpdateChecker).GetMethod(
            "ReportLegacyBridgeNoticeIfAny", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo getVersionInfo = typeof(UpdateChecker).GetMethod(
            "GetVersionInfo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

        Assert.True(check != null && report != null && getVersionInfo != null,
            "UpdateChecker 的相关方法改名了 —— 用例前提被破坏。");

        List<VerifierIlInstruction> il = VerifierIl.Decode(VerifierIl.EffectiveBody(check));

        int reportOffset = il.FirstOrDefault(i =>
            i.IsCallOrCallVirt && i.Method?.Name == report.Name)?.Offset ?? -1;
        int networkOffset = il.FirstOrDefault(i =>
            i.IsCallOrCallVirt && i.Method?.Name == getVersionInfo.Name)?.Offset ?? -1;

        Assert.True(reportOffset >= 0, "CheckAsync 里找不到读标记的调用 —— 用例前提被破坏。");
        Assert.True(networkOffset >= 0, "CheckAsync 里找不到取元数据的调用 —— 用例前提被破坏。");
        Assert.True(
            reportOffset < networkOffset,
            "读标记（偏移 " + reportOffset + "）必须排在取元数据（偏移 " + networkOffset + "）之前。");
    }

    // ---------------------------------------------------------------- V6

    /// <summary>
    /// V6（**载荷用例**）：跨进程验证 —— 更新器 exe 真的被单参数调用时，
    /// **仍然退出码 6**，并真的写出了这个标记文件。
    /// <para>这是"完整性校验不得被削弱"的端到端证据：不依赖更新器工程内部的任何测试接缝。</para>
    /// </summary>
    [Fact]
    public void V6_更新器单参数调用退出码为6且写出标记()
    {
        string repoRoot = FindRepoRoot();
        string updaterExe = Path.Combine(repoRoot, "LiteMonitor.Updater", "bin", "Debug", "net8.0-windows", "win-x64", "LiteMonitor++.Updater.exe");
        Assert.True(File.Exists(updaterExe), "更新器 exe 不存在：" + updaterExe);

        string dir = NewTempDir();
        try
        {
            string fakeZip = Path.Combine(dir, "update.zip");
            File.WriteAllBytes(fakeZip, new byte[] { 0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = updaterExe,
                WorkingDirectory = dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(fakeZip);

            using System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(psi);
            Assert.True(proc != null, "更新器进程没能启动。");
            proc!.WaitForExit(60000);

            Assert.Equal(6, proc.ExitCode);

            // ★ 标记写在"主程序目录"；便携安装里更新器与主程序同目录。
            //   开发构建里两者分属不同目录 ⇒ 逐个候选目录找（并把开发目录里留下的那份清掉）。
            string updaterDir = Path.GetDirectoryName(updaterExe)!;
            var candidates = new[] { dir, updaterDir, Path.GetDirectoryName(updaterDir)! };
            string? markerPath = candidates
                .Select(d => Path.Combine(d, MarkerFileName))
                .FirstOrDefault(File.Exists);

            Assert.True(
                markerPath != null,
                "单参数被拒时必须写出机器可读标记（找过：" + string.Join(", ", candidates) + "）：" + MarkerFileName);

            string json = File.ReadAllText(markerPath!);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("liteMonitor.legacyBridge/v1", root.GetProperty("schema").GetString());
            Assert.Equal("missing_digest_rejected", root.GetProperty("code").GetString());
            Assert.True(root.GetProperty("reason").GetString()!.Length > 0, "标记必须含原因。");
            Assert.True(
                root.GetProperty("guidance").GetString()!.Contains("手动下载", StringComparison.Ordinal),
                "标记必须含手动升级指引。");
            Assert.True(
                root.GetProperty("manualDownloadUrl").GetString()!.StartsWith("https://", StringComparison.Ordinal),
                "标记必须含发布页地址。");

            // ★ 标记里不得出现任何私钥材料
            Assert.False(json.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase));
            Assert.False(json.Contains("privateKey", StringComparison.OrdinalIgnoreCase));

            // 清理开发构建目录里留下的那份（生产里它落在主程序目录，属于预期行为）
            foreach (string leftover in candidates.Select(d => Path.Combine(d, MarkerFileName)))
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V7

    /// <summary>V7：主程序侧的标记文件名必须与更新器侧**逐字一致**（跨进程契约）。</summary>
    [Fact]
    public void V7_两侧标记文件名逐字一致()
    {
        Assert.Equal(MarkerFileName, LegacyBridgeNotice.MarkerFileName);
        Assert.Equal("liteMonitor.legacyBridge/v1", LegacyBridgeNotice.Schema);
    }

    // ---------------------------------------------------------------- 辅助

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LiteMonitor.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（LiteMonitor.sln）。");
    }
}