using System;
using System.Diagnostics;
using System.IO;

namespace AppearanceBackdropProbe.Evidence
{
    internal sealed class ExternalCaptureOutcome
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public int ExitCode { get; init; }
        public long WallMs { get; init; }
    }

    /// <summary>
    /// 外部观察者：用一个**独立进程**（pwsh.exe + System.Drawing）抓取屏幕矩形。
    ///
    /// 为什么要独立进程：
    ///  - 探针自己的捕获必然包含自己的窗口（除非后端能排除自身），无法充当"旁观者"；
    ///  - 外部观察者同时代表"用户录屏软件"的视角（OBS / 游戏栏 / 截图工具都走桌面 DC 捕获），
    ///    因此 EXCLUDEFROMCAPTURE 对录屏的影响可以直接用它来判定。
    /// </summary>
    internal sealed class ExternalCapture
    {
        private readonly string _pwsh;
        private readonly string _script;

        public bool Available { get; }
        public string Info { get; }

        public ExternalCapture(string pwshPath, string scriptPath)
        {
            _script = scriptPath;
            _pwsh = ResolvePwsh(pwshPath);
            Available = !string.IsNullOrEmpty(_pwsh) && File.Exists(scriptPath);
            Info = Available
                ? $"外部观察者可用: {_pwsh} / {scriptPath}"
                : $"外部观察者不可用: pwsh='{_pwsh}' scriptExists={File.Exists(scriptPath)}";
        }

        private static string ResolvePwsh(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
            string p7 = @"C:\Program Files\PowerShell\7\pwsh.exe";
            if (File.Exists(p7)) return p7;
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            foreach (var d in dirs)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                string candidate = Path.Combine(d.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return "";
        }

        public ExternalCaptureOutcome Capture(int x, int y, int w, int h, string outPng, int delayMs = 0)
        {
            if (!Available) return new ExternalCaptureOutcome { Ok = false, Message = Info };

            var dir = Path.GetDirectoryName(outPng);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var psi = new ProcessStartInfo
            {
                FileName = _pwsh,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(_script);
            psi.ArgumentList.Add("-X"); psi.ArgumentList.Add(x.ToString());
            psi.ArgumentList.Add("-Y"); psi.ArgumentList.Add(y.ToString());
            psi.ArgumentList.Add("-W"); psi.ArgumentList.Add(w.ToString());
            psi.ArgumentList.Add("-H"); psi.ArgumentList.Add(h.ToString());
            psi.ArgumentList.Add("-Out"); psi.ArgumentList.Add(outPng);
            if (delayMs > 0) { psi.ArgumentList.Add("-DelayMs"); psi.ArgumentList.Add(delayMs.ToString()); }

            long t0 = Environment.TickCount64;
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return new ExternalCaptureOutcome { Ok = false, Message = "Process.Start 返回 null" };
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                int code = p.HasExited ? p.ExitCode : -1;
                return new ExternalCaptureOutcome
                {
                    Ok = code == 0 && File.Exists(outPng),
                    ExitCode = code,
                    Message = (stdout + " " + stderr).Trim(),
                    WallMs = Environment.TickCount64 - t0,
                };
            }
            catch (Exception ex)
            {
                return new ExternalCaptureOutcome { Ok = false, Message = ex.Message, WallMs = Environment.TickCount64 - t0 };
            }
        }
    }
}
