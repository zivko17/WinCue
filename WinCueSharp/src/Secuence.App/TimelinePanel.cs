// TimelinePanel — la cabecera del timeline, encima del lienzo.
//
// Equivalente de WinCue.App.TimelinePanel: rótulo TIMELINE DEL SHOW a la
// izquierda, la pista de uso, y a la derecha la casilla del crossfade
// automático con los tres botones de zoom. El lienzo (TimelineCanvas) queda
// debajo llenando lo que sobre.
//
// El reparto es el de WinCue elemento por elemento. Lo único que cambia es el
// andamio: allí es un DockPanel dentro de un Border, y aquí un Grid de dos
// filas, porque WinUI 3 no trae DockPanel.
//
// Este panel NO tiene lógica propia: reexpone la del lienzo para que
// MainWindow siga hablando con una sola cosa.

using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

public sealed class TimelinePanel : UserControl
{
    private readonly TimelineCanvas _canvas = new();
    private readonly CheckBox _chkXfade;

    /// <summary>Avisa de que el operador ha tocado la casilla, para
    /// persistirla. Igual que WinCue.App.TimelinePanel.AutoCrossfadeToggled,
    /// NO salta cuando el estado lo fija SetAutoCrossfade desde settings: si
    /// no, cargar el ajuste lo volvería a guardar.</summary>
    public Action<bool>? AutoCrossfadeToggled;

    private bool _loadingXfade;

    public TimelinePanel()
    {
        Background = Theme.MeterBgAltBrush;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ── Cabecera ──
        var header = new Grid { Margin = new Thickness(6, 3, 6, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // El glifo va en su PROPIO TextBlock: la fuente es del control entero
        // en WinUI 3, así que un rótulo de texto plano con un icono delante lo
        // pintaría como un cuadradito (ver IconFont.cs).
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0),
        };
        title.Children.Add(new TextBlock
        {
            Text = Icons.Timeline,
            FontFamily = IconFont.Font,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.StandbyBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        title.Children.Add(new TextBlock
        {
            Text = Loc.T("TIMELINE DEL SHOW", "SHOW TIMELINE"),
            FontWeight = FontWeights.Bold,
            Foreground = Theme.StandbyBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        // Pista de uso, pegada a la derecha del rótulo y antes de los mandos —
        // el mismo sitio que en WinCue.
        var hint = new TextBlock
        {
            Text = Loc.T(
                "arrastrar: tiempo · sobre el anterior = SOLAPAR (paralelo) · " +
                "vertical: pista · Ctrl+rueda: zoom",
                "drag: time · over the previous = OVERLAP (parallel) · " +
                "vertical: track · Ctrl+wheel: zoom"),
            Foreground = Theme.TextDimmerBrush,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(hint, 1);
        header.Children.Add(hint);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _chkXfade = new CheckBox
        {
            Content = Loc.T("Crossfade al solapar", "Crossfade on overlap"),
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = Theme.TextTertiaryBrush,
            FontSize = 11,
            // Ver Chk del inspector: el CheckBox de WinUI 3 trae MinWidth =
            // 120 de serie y aquí abriría un hueco antes de los botones.
            MinWidth = 0,
        };
        ToolTipService.SetToolTip(_chkXfade, Loc.T(
            "ACTIVADO: dos medias consecutivas de la MISMA capa que se " +
            "solapan se cruzan (fade out/in = solape; el vídeo funde por " +
            "opacidad con el compositor GPU) y al separarlas se quita. Las " +
            "capas distintas son independientes: nunca se funden solas.\n" +
            "DESACTIVADO: el timeline no toca ningún fade (todo a mano en el " +
            "inspector).",
            "ON: two consecutive clips on the SAME layer that overlap " +
            "crossfade (fade out/in = overlap; the video fades by opacity " +
            "with the GPU compositor) and separating them removes it. " +
            "Different layers are independent: they never fade on their own.\n" +
            "OFF: the timeline touches no fade (all by hand in the " +
            "inspector)."));
        _chkXfade.Checked += (_, _) => OnXfadeToggle(true);
        _chkXfade.Unchecked += (_, _) => OnXfadeToggle(false);
        right.Children.Add(_chkXfade);

        right.Children.Add(HeaderButton(Icons.ZoomOut, icon: true,
            Loc.T("Alejar", "Zoom out"), () => _canvas.ZoomStep(1 / 1.4)));
        right.Children.Add(HeaderButton(Icons.ZoomIn, icon: true,
            Loc.T("Acercar", "Zoom in"), () => _canvas.ZoomStep(1.4)));
        right.Children.Add(HeaderButton(Loc.T("Ajustar", "Fit"), icon: false,
            Loc.T("Que quepa el show entero", "Fit the whole show"),
            () => _canvas.ZoomToFit()));
        Grid.SetColumn(right, 2);
        header.Children.Add(right);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        Grid.SetRow(_canvas, 1);
        root.Children.Add(_canvas);

        Content = root;
        // Un ContentControl NO estira su contenido en WinUI 3 (Top/Left por
        // defecto): sin esto el timeline se quedaría flotando arriba a la
        // izquierda dentro del panel.
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    private static Button HeaderButton(string text, bool icon, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = icon
                ? new TextBlock { Text = text, FontFamily = IconFont.Font, FontSize = 12 }
                : new TextBlock { Text = text, FontSize = 12 },
            Padding = new Thickness(8, 0, 8, 1),
            Margin = new Thickness(3, 0, 0, 0),
            Background = Theme.ButtonBgBrush,
            Foreground = Theme.TextPrimaryBrush,
            BorderBrush = Theme.BorderBrush,
            MinWidth = 0,
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void OnXfadeToggle(bool enabled)
    {
        _canvas.AutoCrossfade = enabled;
        if (!_loadingXfade)
            AutoCrossfadeToggled?.Invoke(enabled);
    }

    /// <summary>Fija el estado del crossfade automático desde settings, SIN
    /// que eso cuente como que lo ha tocado el operador.</summary>
    public void SetAutoCrossfade(bool enabled)
    {
        _loadingXfade = true;
        try
        {
            _chkXfade.IsChecked = enabled;
            _canvas.AutoCrossfade = enabled;
        }
        finally { _loadingXfade = false; }
    }

    // ── Reexposición del lienzo: MainWindow habla solo con el panel ──

    public Action<int, string, object?>? CommitField
    {
        get => _canvas.CommitField;
        set => _canvas.CommitField = value;
    }

    public Action<int, int>? MoveCueRequested
    {
        get => _canvas.MoveCueRequested;
        set => _canvas.MoveCueRequested = value;
    }

    public Func<bool>? IsEditAllowed
    {
        get => _canvas.IsEditAllowed;
        set => _canvas.IsEditAllowed = value;
    }

    public IShowClock? Clock
    {
        get => _canvas.Clock;
        set => _canvas.Clock = value;
    }

    public event Action<int>? CueSelected
    {
        add => _canvas.CueSelected += value;
        remove => _canvas.CueSelected -= value;
    }

    /// <summary>Scrub del cursor de edición. Ver TimelineCanvas.EditScrubbed:
    /// hoy no lo escucha nadie porque Secuence no tiene visor de edición.</summary>
    public event Action<Cue?, double>? EditScrubbed
    {
        add => _canvas.EditScrubbed += value;
        remove => _canvas.EditScrubbed -= value;
    }

    /// <summary>Doble clic en la regla: reproducir el show desde ahí.</summary>
    public Action<IReadOnlyList<MainController.TimelinePlayItem>>? PlayFromRequested
    {
        get => _canvas.PlayFromRequested;
        set => _canvas.PlayFromRequested = value;
    }

    public void SetModel(CueModel model) => _canvas.SetModel(model);
    public void RefreshFromModel() => _canvas.RefreshFromModel();
    public void SetSelected(int cueId) => _canvas.SetSelected(cueId);
    public void SetActive(System.Collections.Generic.IReadOnlyList<ActiveCueInfo> rows) =>
        _canvas.SetActive(rows);
    public double? OutgoingRemaining(int cueId) => _canvas.OutgoingRemaining(cueId);
}
