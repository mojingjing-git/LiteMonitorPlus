using System;
using System.IO;
using System.Threading; // 必须引用：用于 Mutex
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor
{
    internal static class Program
    {
        internal static bool SafeMode { get; } =
            Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--safe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--safe-mode", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--disable-gpu", StringComparison.OrdinalIgnoreCase));

        // 保持 Mutex 引用，防止被 GC 回收
        private static Mutex? _mutex = null;

        [STAThread]
        static void Main()
        {
            // =================================================================
            // ★★★ 1. 单实例互斥锁 (基于文件路径的版本) - 修正版 ★★★
            // =================================================================
            bool createNew;
            string mutexName;

            try
            {
                // [修正] 使用 Process 获取真实路径，解决单文件发布路径为空的问题
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

                if (string.IsNullOrEmpty(exePath))
                {
                    mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";
                }
                else
                {
                    string appFolderPath = Path.GetDirectoryName(exePath);

                    string sanitizedPath = appFolderPath?.ToLower()
                                                        .Replace('\\', '_')
                                                        .Replace(':', '_')
                                                        .Replace('/', '_')
                                                        .Replace(' ', '_');

                    // [建议] 增加哈希或长度截断，防止路径过长导致 Mutex 名称超过系统限制 (260字符) 从而抛出异常进入 catch
                    // 这里简单处理：如果生成的名称太长，就取路径的 HashCode 混淆一下
                    string baseName = $"Global\\LiteMonitor_SingleInstance_{sanitizedPath}_Mutex";
                    if (baseName.Length > 250) 
                    {
                         // 如果路径太长，使用路径的哈希值来保证唯一性且不超长
                         // [Fix] 用 SHA-256 摘要前 16 位十六进制替代 string.GetHashCode()。
                         // .NET Core 的字符串哈希按进程随机化，同一路径在两个进程里会算出不同的值，
                         // 长路径时 Mutex 名就不一致，单实例保护失效（两个实例可共存）。
                         // SHA-256 是确定性摘要，跨进程稳定，且长度固定不会顶到 260 字符上限。
                         string pathDigest = Convert.ToHexString(
                             System.Security.Cryptography.SHA256.HashData(
                                 System.Text.Encoding.UTF8.GetBytes(sanitizedPath ?? string.Empty)));
                         baseName = $"Global\\LiteMonitor_SingleInstance_{pathDigest.Substring(0, 16)}_Mutex";
                    }
                    
                    mutexName = baseName;
                }

                _mutex = new Mutex(true, mutexName, out createNew);
            }
            catch (Exception ex)
            {
                // 记录一下异常（可选），方便调试为什么会创建失败
                // LogCrash(ex, "Mutex_Creation_Failed"); 
                
                // 回退策略
                mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";
                _mutex = new Mutex(true, mutexName, out createNew);
            }

            if (!createNew)
            {
                return; 
            }

            // =================================================================
            // ★★★ 2. 注册全局异常捕获事件 (保留你的原始逻辑) ★★★
            // =================================================================
            // 捕获 UI 线程的异常
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            
            // 捕获非 UI 线程（后台线程）的异常
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // =================================================================
            // ★★★ 3. 启动应用 ★★★
            // =================================================================
            try
            {
                // ★★★ 3. 启动应用 ★★★
                ApplicationConfiguration.Initialize();
                if (!DriverInstaller.CheckPawnIOBeforeHardware(Settings.Load()))
                {
                    return;
                }

                Application.Run(new MainForm());
            }
            finally
            {
                // =================================================================
                // ★★★ [新增] 退出时的终极清理 ★★★
                // 无论程序是正常关闭、崩溃还是被强制结束(部分情况)，这里都会尝试执行
                // 确保 FPS 进程被杀掉，且 ETW 会话被停止，防止系统卡顿
                // =================================================================
                try 
                {
                    FpsCounter.ForceKillZombies(); 
                }
                catch { }

                // 显式释放锁
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                }
            }
        }

        // --- 异常处理委托 ---
        static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            LogCrash(e.Exception, "UI_Thread");
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash(e.ExceptionObject as Exception, "Background_Thread");
        }

        // --- 写入 crash.log 的核心方法 ---
        static void LogCrash(Exception? ex, string source)
        {
            if (ex == null) return;

            try
            {
                // 日志文件保存在程序运行目录下
                string logPath = Path.Combine(AppContext.BaseDirectory, "LiteMonitor++_Error.log");
                
                string errorMsg = "==================================================\n" +
                                  $"[Time]: {DateTime.Now}\n" +
                                  $"[Source]: {source}\n" +
                                  $"[Message]: {ex.Message}\n" +
                                  $"[Stack]:\n{ex.StackTrace}\n" +
                                  "==================================================\n\n";

                File.AppendAllText(logPath, errorMsg);

                // 只有真的崩了才弹窗提示用户
                MessageBox.Show($"程序遇到致命错误！\n错误日志已保存至：{logPath}\n\n原因：{ex.Message}", 
                                "LiteMonitor++ Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch 
            {
                // 如果日志都写不进去，通常是磁盘满了或权限极度受限，只能忽略
            }
        }
    }
}
