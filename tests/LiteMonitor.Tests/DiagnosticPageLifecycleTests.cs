using System;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.UI.SettingsPage;
using Xunit;

namespace LiteMonitor.Tests
{
    public sealed class DiagnosticPageLifecycleTests
    {
        [Fact]
        public void C06_真实摘页后停止刷新计时器()
        {
            Exception? captured = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var host = new Panel();
                    using var page = new DiagnosticPage();
                    host.Controls.Add(page);
                    page.OnShow();
                    Assert.True(page.IsRefreshTimerRunning);

                    // SettingsForm.SwitchPage 的真实路径：Controls.Clear 只摘 Parent，
                    // 不触发 VisibleChanged。
                    host.Controls.Clear();
                    Assert.False(page.IsRefreshTimerRunning);
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA 页面测试超时");
            if (captured != null)
                throw new Xunit.Sdk.XunitException(captured.ToString());
        }
    }
}
