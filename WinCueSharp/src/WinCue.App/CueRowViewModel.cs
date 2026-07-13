// =============================================================================
// CueRowViewModel.cs — Fila de la tabla de cues (binding de WPF)
// =============================================================================

using System.ComponentModel;
using System.Windows.Media;
using WinCue.Core;

namespace WinCue.App;

public sealed class CueRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Cue _cue;
    private readonly CueModel _model;
    private bool _isPlayhead;

    public CueRowViewModel(Cue cue, CueModel model)
    {
        _cue = cue;
        _model = model;
    }

    public int CueId => _cue.Id;

    public string Number => _cue.DisplayNumber;

    public string TypeText => _cue.Type.ToString().ToUpperInvariant();

    public string Name => _cue.Type == CueType.Group
        ? $"{(_cue.Collapsed ? "▸" : "▾")} {_cue.Name}"
        : _cue.Name;

    /// <summary>Sangría por profundidad de anidamiento en grupos.</summary>
    public System.Windows.Thickness NameIndent => new(_model.Depth(_cue) * 18, 0, 0, 0);

    public string DurationText => _cue.Duration > 0 ? FormatTime(_cue.Duration) : "—";
    public string PreWaitText => _cue.PreWait > 0 ? FormatTime(_cue.PreWait) : "";
    public string PostWaitText => _cue.PostWait > 0 ? FormatTime(_cue.PostWait) : "";

    public string ContinueText => _cue.ContinueMode switch
    {
        ContinueMode.AutoContinue => "⤵ auto",
        ContinueMode.AutoFollow => "⤵ follow",
        _ => "",
    };

    public string LatencyText => _cue.LatencyMs is double ms ? $"{ms:F0} ms" : "";

    public string ArmedText => _cue.Armed ? "" : "✕";

    public string FlagText => _cue.Flagged ? "⚑" : "";

    /// <summary>Color del punto de estado (colores compartidos con Python).</summary>
    public Brush StateBrush => BrushFrom(CueConstants.StateColors[ToCueState(_cue.State)]);

    /// <summary>Color de etiqueta del cue (columna Nº).</summary>
    public Brush LabelBrush =>
        _cue.Color.Length > 0 && CueConstants.CueColors.TryGetValue(_cue.Color, out var hex)
        && hex.Length > 0
            ? BrushFrom(hex)
            : Brushes.Transparent;

    public bool IsPlayhead
    {
        get => _isPlayhead;
        set
        {
            if (_isPlayhead == value) return;
            _isPlayhead = value;
            OnChanged(nameof(IsPlayhead));
        }
    }

    /// <summary>Oculto si un ancestro está colapsado.</summary>
    public System.Windows.Visibility RowVisibility =>
        _model.IsAncestorCollapsed(_cue)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    /// <summary>Notifica el rebind completo de la fila (tras editar el cue).</summary>
    public void RefreshAll() => OnChanged(string.Empty);

    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static CueState ToCueState(CueState s) => s;

    private static string FormatTime(double seconds) =>
        seconds >= 60
            ? $"{(int)(seconds / 60)}:{seconds % 60:00.0}"
            : $"{seconds:0.0}s";

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
