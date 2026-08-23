// =============================================================================
// CueRowViewModel.cs — Fila de la tabla de cues (binding de WPF)
// =============================================================================

using System.ComponentModel;
using System.Windows.Media;
using WinCue.Core;
using WinCue.Presentation;

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

    /// <summary>Icono del tipo. Va en su PROPIA celda de ancho fijo: pegado al
    /// nombre en una sola cadena, el glifo se comía el hueco cuando la etiqueta
    /// era larga y las etiquetas de tipo no alineaban entre filas.</summary>
    public string TypeIcon => _cue.Type switch
    {
        CueType.Audio => Icons.Audio,
        CueType.Video => Icons.Video,
        CueType.Wait => Icons.Wait,
        CueType.Fade => Icons.Fade,
        CueType.Start => Icons.Start,
        CueType.Stop => Icons.Stop,
        CueType.Script => Icons.Script,
        CueType.Memo => Icons.Memo,
        CueType.Group => Icons.Group,
        _ => "",
    };

    /// <summary>Nombre del tipo, sin icono (escaneable de reojo).</summary>
    public string TypeName => _cue.Type switch
    {
        CueType.Audio => "AUDIO",
        CueType.Video => Loc.T("VÍDEO", "VIDEO"),
        CueType.Wait => Loc.T("ESPERA", "WAIT"),
        CueType.Fade => Loc.T("FUNDIDO", "FADE"),
        CueType.Start => Loc.T("INICIAR", "START"),
        CueType.Stop => Loc.T("PARAR", "STOP"),
        CueType.Script => "SCRIPT",
        CueType.Memo => Loc.T("NOTA", "MEMO"),
        CueType.Group => Loc.T("GRUPO", "GROUP"),
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

    /// <summary>Sangría de los cues que van dentro de un grupo.</summary>
    public System.Windows.Thickness NameIndent =>
        new(_model.Depth(_cue) * 18 + 4, 0, 0, 0);

    // ══════════════════════════════════════════════════════════════════
    // RECUADRO DEL GRUPO (contorno que abraza cabecera + hijos)
    // ══════════════════════════════════════════════════════════════════
    // Mismo lenguaje que el timeline (TimelinePanel.SpanGeometry): color del
    // grupo, relleno tenue debajo y marco encima. Aquí no se puede pintar de
    // una vez —cada fila es un control aparte y la tabla está virtualizada—,
    // así que cada fila aporta SU TRAMO del marco: la cabecera pone el lado de
    // arriba, el último hijo el de abajo y todas ponen los laterales. Sale un
    // rectángulo continuo que viaja con el scroll, y al plegar el grupo la
    // cabecera se queda sola y cierra la caja entera.
    //
    // El marco se pinta en una CAPA SUPERPUESTA (ver MainWindow.xaml), no como
    // borde de la fila: si ocupara sitio, las filas de grupo desplazarían sus
    // celdas 2 px y las columnas dejarían de alinear con las filas normales.

    /// <summary>El GROUP al que pertenece esta fila: el propio cue si lo es, o
    /// su grupo padre. null en filas sueltas.</summary>
    private Cue? OwnerGroup() => _cue.Type == CueType.Group
        ? _cue
        : _cue.ParentId is int pid && _model.GetById(pid) is { Type: CueType.Group } g
            ? g
            : null;

    /// <summary>Color del grupo: el elegido por el operador (misma paleta que
    /// el resto de cues) o el ámbar de GRUPO si no eligió ninguno.</summary>
    private static Color ColorOf(Cue group) =>
        group.Color.Length > 0
        && CueConstants.CueColors.TryGetValue(group.Color, out var hex) && hex.Length > 0
            ? (Color)ColorConverter.ConvertFromString(hex)
            : Theme.TypeGroup;

    /// <summary>Marco del recuadro. Transparente fuera de todo grupo.</summary>
    public Brush GroupBorderBrush =>
        OwnerGroup() is Cue g ? Theme.Solid(Theme.Alpha(ColorOf(g), 215)) : Brushes.Transparent;

    /// <summary>Relleno del recuadro: un velo del color del grupo. La cabecera
    /// va más marcada que los hijos para que el bloque se lea de un vistazo.</summary>
    public Brush GroupFillBrush =>
        OwnerGroup() is Cue g
            ? Theme.Solid(Theme.Alpha(ColorOf(g), _cue.Type == CueType.Group ? (byte)56 : (byte)24))
            : Brushes.Transparent;

    /// <summary>Tramo del marco que le toca a esta fila.</summary>
    public System.Windows.Thickness GroupBorderThickness
    {
        get
        {
            var g = OwnerGroup();
            if (g is null)
                return default;
            bool header = _cue.Type == CueType.Group;
            bool last = header ? g.Collapsed : IsLastOf(g);
            return new(GroupEdge, header ? GroupEdge : 0, GroupEdge, last ? GroupEdge : 0);
        }
    }

    /// <summary>Esquinas redondeadas arriba en la cabecera y abajo en el cierre
    /// (mismo radio que los recuadros del timeline).</summary>
    public System.Windows.CornerRadius GroupCornerRadius
    {
        get
        {
            var g = OwnerGroup();
            if (g is null)
                return default;
            bool header = _cue.Type == CueType.Group;
            bool last = header ? g.Collapsed : IsLastOf(g);
            return new(header ? 4 : 0, header ? 4 : 0, last ? 4 : 0, last ? 4 : 0);
        }
    }

    /// <summary>Grosor del marco: 2 px. En una sala a oscuras 1 px se pierde.</summary>
    private const double GroupEdge = 2.0;

    /// <summary>True si este cue es el ÚLTIMO del grupo (el que cierra la caja).</summary>
    private bool IsLastOf(Cue group) =>
        _model.Descendants(group.Id).LastOrDefault()?.Id == _cue.Id;

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
