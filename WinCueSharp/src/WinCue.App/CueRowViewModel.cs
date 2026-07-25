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

    /// <summary>Icono + tipo, coloreado (escaneable de reojo).</summary>
    public string TypeLabel => _cue.Type switch
    {
        CueType.Audio => $"{Icons.Audio} AUDIO",
        CueType.Video => $"{Icons.Video} {Loc.T("VÍDEO", "VIDEO")}",
        CueType.Wait => $"{Icons.Wait} {Loc.T("ESPERA", "WAIT")}",
        CueType.Fade => $"{Icons.Fade} {Loc.T("FUNDIDO", "FADE")}",
        CueType.Start => $"{Icons.Start} {Loc.T("INICIAR", "START")}",
        CueType.Stop => $"{Icons.Stop} {Loc.T("PARAR", "STOP")}",
        CueType.Script => $"{Icons.Script} SCRIPT",
        CueType.Memo => $"{Icons.Memo} {Loc.T("NOTA", "MEMO")}",
        CueType.Group => $"{Icons.Group} GRUPO",
        _ => TypeText,
    };

    public Brush TypeBrush => Theme.BrushForCueType(_cue.Type);

    // ── Cuenta atrás en la fila (caja en la columna Duración, estilo QLab) ─
    private double? _liveRemaining;        // s restantes mientras suena
    private double? _preWaitRemaining;     // s restantes de pre_wait (viene)

    /// <summary>La vista fija los tiempos en vivo en cada tick del controlador.
    /// remaining/preWaitRemaining = null cuando el cue no está en curso.</summary>
    public void SetLive(double? remaining, double? preWaitRemaining)
    {
        // Redondeo a 0,1 s: la lectura muestra una décima, así no notificamos
        // en cada tick de 80 ms sin cambio visible.
        double? rq = remaining is double r ? Math.Round(Math.Max(0.0, r), 1) : null;
        double? pq = preWaitRemaining is double p ? Math.Round(Math.Max(0.0, p), 1) : null;
        if (rq != _liveRemaining || pq != _preWaitRemaining)
        {
            _liveRemaining = rq;
            _preWaitRemaining = pq;
            OnChanged(nameof(DurationText));
            OnChanged(nameof(DurationBoxBrush));
            OnChanged(nameof(DurationTextBrush));
        }
    }

    public string Name => _cue.Type == CueType.Group
        ? $"{(_cue.Collapsed ? Icons.Collapsed : Icons.Expanded)} {_cue.Name}"
        : _cue.Name;

    /// <summary>Sangría por profundidad de anidamiento en grupos (+8 px de
    /// canalón fijo donde vive la banda del grupo).</summary>
    public System.Windows.Thickness NameIndent => new(_model.Depth(_cue) * 18 + 8, 0, 0, 0);

    // ── Recuadro del grupo: banda vertical continua (cabecera + hijos) ──

    /// <summary>El GROUP cuyo "recuadro" pinta esta fila: el propio cue si es
    /// un grupo, o su grupo padre directo si está dentro de uno.</summary>
    private Cue? BandGroup() => _cue.Type == CueType.Group
        ? _cue
        : _cue.ParentId is int pid ? _model.GetById(pid) : null;

    /// <summary>Color de la banda: el color del grupo (si tiene) o el ámbar
    /// de GRUPO. Transparente en filas de nivel superior sin grupo.</summary>
    public Brush GroupBandBrush
    {
        get
        {
            var g = BandGroup();
            if (g is null)
                return Brushes.Transparent;
            return g.Color.Length > 0
                && CueConstants.CueColors.TryGetValue(g.Color, out var h) && h.Length > 0
                ? BrushFrom(h) : Theme.StandbyBrush;
        }
    }

    /// <summary>La banda se alinea con la sangría del GRUPO (la cabecera y
    /// todos sus descendientes comparten X → corchete continuo).</summary>
    public System.Windows.Thickness GroupBandMargin
    {
        get
        {
            var g = BandGroup();
            return new(g is null ? 0 : _model.Depth(g) * 18, 0, 0, 0);
        }
    }

    /// <summary>En reposo: duración fija. Sonando: cuenta atrás (verde). En
    /// pre_wait: cuenta atrás de la espera (ámbar). El color lo da DurationBrush.</summary>
    public string DurationText
    {
        get
        {
            if (_preWaitRemaining is double p)
                return $"-{p:0.0}s";
            if (_liveRemaining is double r)
                return $"-{TimeSpan.FromSeconds(r):m\\:ss\\.f}";
            return _cue.Duration > 0 ? FormatTime(_cue.Duration) : "—";
        }
    }

    private static readonly Brush DurBoxNone = Brushes.Transparent;
    private static readonly Brush DurBoxPlaying = Theme.GoBrush;
    private static readonly Brush DurBoxPreWait = Theme.StandbyBrush;
    private static readonly Brush DurTextIdle = Theme.TextPrimaryBrush;
    private static readonly Brush DurTextBoxed = Theme.TextOnAccentBrush;

    /// <summary>Fondo de la caja de la columna Duración: verde sonando, ámbar en
    /// pre_wait, transparente (sin caja) en reposo.</summary>
    public Brush DurationBoxBrush =>
        _preWaitRemaining is not null ? DurBoxPreWait
        : _liveRemaining is not null ? DurBoxPlaying
        : DurBoxNone;

    /// <summary>Texto de la columna Duración: oscuro sobre la caja de color en
    /// vivo (contraste), gris normal en reposo.</summary>
    public Brush DurationTextBrush =>
        _preWaitRemaining is not null || _liveRemaining is not null
            ? DurTextBoxed : DurTextIdle;

    public string PreWaitText => _cue.PreWait > 0 ? FormatTime(_cue.PreWait) : "";
    public string PostWaitText => _cue.PostWait > 0 ? FormatTime(_cue.PostWait) : "";

    public string ContinueText => _cue.ContinueMode switch
    {
        ContinueMode.AutoContinue => $"{Icons.FollowInto} auto",
        ContinueMode.AutoFollow => $"{Icons.FollowInto} follow",
        _ => "",
    };

    public string LatencyText => _cue.LatencyMs is double ms ? $"{ms:F0} ms" : "";

    public string ArmedText => _cue.Armed ? "" : Icons.Cross;

    public string FlagText => _cue.Flagged ? Icons.Flag : "";

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

    // ── Filtro de búsqueda (Ctrl+F) ────────────────────────────────────
    private bool _filterHidden;

    /// <summary>true = la fila no coincide con la búsqueda activa.</summary>
    public bool FilterHidden
    {
        get => _filterHidden;
        set
        {
            if (_filterHidden == value) return;
            _filterHidden = value;
            OnChanged(nameof(RowVisibility));
        }
    }

    /// <summary>Oculto si un ancestro está colapsado o el filtro lo excluye.</summary>
    public System.Windows.Visibility RowVisibility =>
        _filterHidden || _model.IsAncestorCollapsed(_cue)
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
