using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AppearanceBackdropProbe.Evidence
{
    /// <summary>证据文件写出：JSON（自动断言结果）+ PNG（截图观察）。</summary>
    internal static class EvidenceWriter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static void WriteJson<T>(string path, T value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        public static string SavePng(Bitmap bmp, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bmp.Save(path, ImageFormat.Png);
            return path;
        }

        /// <summary>把多帧拼成一张联络表，便于人工一眼看完整段观察过程。</summary>
        public static Bitmap ContactSheet(IReadOnlyList<Bitmap> frames, int cols, int thumbW, string title)
        {
            if (frames.Count == 0) return new Bitmap(1, 1);
            int rows = (frames.Count + cols - 1) / cols;
            int thumbH = Math.Max(1, (int)Math.Round(frames[0].Height * (double)thumbW / frames[0].Width));
            int headerH = 24;
            var sheet = new Bitmap(cols * (thumbW + 6) + 6, rows * (thumbH + 18) + 6 + headerH, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(sheet);
            g.Clear(Color.FromArgb(255, 24, 24, 28));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var f = new Font("Segoe UI", 10f))
            using (var b = new SolidBrush(Color.White))
                g.DrawString(title, f, b, 6, 4);

            for (int i = 0; i < frames.Count; i++)
            {
                int cx = 6 + (i % cols) * (thumbW + 6);
                int cy = headerH + 6 + (i / cols) * (thumbH + 18);
                g.DrawImage(frames[i], new Rectangle(cx, cy, thumbW, thumbH));
                using var pen = new Pen(Color.FromArgb(255, 90, 90, 100));
                g.DrawRectangle(pen, cx, cy, thumbW, thumbH);
                using var f = new Font("Segoe UI", 8f);
                using var b = new SolidBrush(Color.FromArgb(255, 200, 200, 210));
                g.DrawString($"#{i}", f, b, cx + 2, cy + thumbH + 2);
            }
            return sheet;
        }
    }

    /// <summary>运行日志（写入自有输出目录，不污染其它目录）。</summary>
    internal sealed class LogWriter
    {
        private readonly string _path;
        private readonly object _gate = new();

        public LogWriter(string path)
        {
            _path = path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        public void Info(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            lock (_gate)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
                Console.WriteLine(line);
            }
        }
    }
}
