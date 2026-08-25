// =============================================================================
// CueRow.cs — Fila de la tabla de cues (binding de WinUI 3).
// =============================================================================
// Equivalente de src/WinCue.App/CueRowViewModel.cs para esta interfaz, no una
// copia. Con transporte ya conectado, SÍ hace falta INotifyPropertyChanged
// para IsPlayhead (ver MainWindow.xaml.cs.SetPlayhead): x:Bind con Mode=OneWay
// repinta la fila cuando cambia, sin recrear la lista entera.
// Sin el recuadro de grupo dibujado por fila (eso es una decoración del hito 1
// de WPF; aquí "grupos" significa, de momento, SOLO la sangría que pide el
// encargo — CueModel.Depth).
//
// El icono del tipo y el de plegado de grupo van en columnas/celdas PROPIAS,
// nunca mezclados en el mismo TextBlock que texto normal: así Phosphor se
// puede aplicar directo sin la fuente compuesta que necesita WPF (ver
// IconFont.cs).
// =============================================================================

using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

public sealed class CueRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Id del cue (WinCue.Core.Cue.Id): con qué fila del modelo se
    /// corresponde esta, para SetPlayhead/RefreshRow y el panel de activos.</summary>
    public int CueId { get; }

    public CueRow(Cue cue, CueModel model)
    {
        CueId = cue.Id;
        Number = cue.DisplayNumber;
        LabelBrush = Theme.BrushForLabelName(cue.Color);
        StateBrush = Theme.BrushForState(cue.State);

        TypeIcon = TypeIconFor(cue.Type);
        TypeName = TypeNameFor(cue.Type);
        TypeBrush = Theme.BrushForCueType(cue.Type);

        Name = cue.Name;
        GroupToggleIcon = cue.Type == CueType.Group
            ? (cue.Collapsed ? Icons.Get(nameof(Icons.Collapsed)) : Icons.Get(nameof(Icons.Expanded)))
            : "";

        // Sangría de los grupos: 18 px por nivel de anidamiento, igual que en
        // WPF (CueRowViewModel.NameIndent), más 4 px de aire fijo.
        NameIndent = new Thickness(model.Depth(cue) * 18 + 4, 0, 0, 0);

        PreWaitText = cue.PreWait > 0 ? FormatTime(cue.PreWait) : "";
        DurationText = cue.Duration > 0 ? FormatTime(cue.Duration) : "—";
        PostWaitText = cue.PostWait > 0 ? FormatTime(cue.PostWait) : "";

        // Icono y palabra en propiedades SEPARADAS (no concatenadas en un
        // único string, como haría CueRowViewModel.ContinueText en WPF): un
        // TextBlock con FontFamily=Phosphor convertiría "auto"/"follow" en
        // ligaduras si compartiera celda con el glifo.
        ContinueIcon = cue.ContinueMode is ContinueMode.AutoContinue or ContinueMode.AutoFollow
            ? Icons.Get(nameof(Icons.FollowInto))
            : "";
        ContinueText = cue.ContinueMode switch
        {
            ContinueMode.AutoContinue => "auto",
            ContinueMode.AutoFollow => "follow",
            _ => "",
        };

        FlagText = cue.Flagged ? Icons.Flag : "";
        ArmedText = cue.Armed ? "" : Icons.Cross;
    }

    public string Number { get; }
    public SolidColorBrush LabelBrush { get; }
    public SolidColorBrush StateBrush { get; }

    public string TypeIcon { get; }
    public string TypeName { get; }
    public SolidColorBrush TypeBrush { get; }

    public string Name { get; }
    public string GroupToggleIcon { get; }
    public Thickness NameIndent { get; }

    public string PreWaitText { get; }
    public string DurationText { get; }
    public string PostWaitText { get; }
    public string ContinueIcon { get; }
    public string ContinueText { get; }

    /// <summary>Banderín del cue, o nada. Mismo criterio que
    /// WinCue.App.CueRowViewModel.FlagText.</summary>
    public string FlagText { get; }

    /// <summary>La ✕ marca los cues DESARMADOS, no los armados: en una lista
    /// donde casi todo está armado, lo que hay que ver de un vistazo es lo
    /// que NO va a sonar. Mismo criterio que
    /// WinCue.App.CueRowViewModel.ArmedText — ojo, el nombre engaña en los
    /// dos sitios.</summary>
    public string ArmedText { get; }

    // ── Playhead (SetPlayhead/RefreshRow/ReloadTable de IShowView) ────────
    private bool _isPlayhead;

    /// <summary>true = GO disparará este cue a continuación. Lo fija
    /// MainWindow.SetPlayhead recorriendo TODAS las filas (una sola vez
    /// true, el resto false) — igual que CueRowViewModel.IsPlayhead en WPF.</summary>
    public bool IsPlayhead
    {
        get => _isPlayhead;
        set
        {
            if (_isPlayhead == value)
                return;
            _isPlayhead = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlayhead)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayheadBarBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NumberWeight)));
        }
    }

    /// <summary>Marca del playhead: una barra en el BORDE IZQUIERDO de la fila.
    /// No es cosmética la elección del eje. Antes esto teñía el FONDO
    /// (RowBrush), y el resaltado de selección del ListView también es fondo:
    /// mientras seleccionar movía el playhead los dos estados coincidían
    /// siempre y la colisión no se veía. Desde que son independientes —mirar un
    /// cue ya no cambia lo que disparará el GO— hay que poder leer los dos a la
    /// vez en la misma fila, y por eso uno usa borde y el otro fondo.</summary>
    public SolidColorBrush PlayheadBarBrush =>
        _isPlayhead ? Theme.PlayheadRowBrush : Theme.TransparentBrush;

    /// <summary>El número del cue del playhead va en negrita: segunda pista,
    /// para quien mire la tabla de lejos o no distinga bien el color.</summary>
    public Windows.UI.Text.FontWeight NumberWeight =>
        _isPlayhead ? FontWeights.Bold : FontWeights.Normal;

    private static string TypeIconFor(CueType type) => type switch
    {
        CueType.Audio => Icons.Get(nameof(Icons.Audio)),
        CueType.Video => Icons.Get(nameof(Icons.Video)),
        CueType.Wait => Icons.Get(nameof(Icons.Wait)),
        CueType.Fade => Icons.Get(nameof(Icons.Fade)),
        CueType.Start => Icons.Get(nameof(Icons.Start)),
        CueType.Stop => Icons.Get(nameof(Icons.Stop)),
        CueType.Script => Icons.Get(nameof(Icons.Script)),
        CueType.Memo => Icons.Get(nameof(Icons.Memo)),
        CueType.Group => Icons.Get(nameof(Icons.Group)),
        _ => "",
    };

    private static string TypeNameFor(CueType type) => type switch
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
        _ => type.ToString().ToUpperInvariant(),
    };

    private static string FormatTime(double seconds) =>
        seconds >= 60
            ? $"{(int)(seconds / 60)}:{seconds % 60:00.0}"
            : $"{seconds:0.0}s";
}
