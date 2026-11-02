using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI;

internal static class SettingsWindowLauncher
{
    // UI-thread owned; all entry points share one draft and one lifecycle owner.
    public static SettingsForm Open(Settings cfg, UIController ui, MainForm owner, string? page = null)
    {
        var window = Application.OpenForms.OfType<SettingsForm>().FirstOrDefault(x => !x.IsDisposed);
        if (window == null)
        {
            window = new SettingsForm(cfg, ui, owner);
            if (!string.IsNullOrEmpty(page)) window.SwitchPage(page);
            window.Show(owner);
        }
        else if (!string.IsNullOrEmpty(page)) window.SwitchPage(page);
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
        return window;
    }
}
