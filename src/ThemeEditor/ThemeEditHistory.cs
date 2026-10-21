using System.Text.Json;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor;

/// <summary>Bounded configuration-only undo. Snapshots borrow no owned GPU resources.</summary>
public sealed class ThemeEditHistory
{
    private readonly List<Theme> _undo = [];
    private Theme? _last;
    public int Count => _undo.Count;
    public static string Signature(Theme theme) => JsonSerializer.Serialize(theme, ThemeManager.CreateSerializerOptions());
    public void Reset(Theme theme) { _undo.Clear(); _last = AppearanceThemeConfig.CloneConfig(theme); }
    public void Observe(Theme theme)
    {
        if (_last is not null && Signature(_last) != Signature(theme))
        {
            _undo.Add(_last);
            if (_undo.Count > 32) _undo.RemoveAt(0);
        }
        _last = AppearanceThemeConfig.CloneConfig(theme);
    }
    public Theme? Undo()
    {
        if (_undo.Count == 0) return null;
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _last = AppearanceThemeConfig.CloneConfig(previous);
        return previous;
    }
}
