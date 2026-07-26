// =============================================================================
// InspectorPanel.cs — Inspector de cue con edición EN VIVO (port de
// inspector_panel.py, fase 5b)
// =============================================================================
// Pestañas: Basics · Triggers · Time & Loops · Display & Geometry · Levels.
// Cada control commitea SU campo al perder el foco / cambiar, a través del
// callback CommitField (→ MainController.OnCueEditCommitted), que aplica los
// cambios al modelo y EN VIVO al motor si el cue está sonando.
//
// GOTCHA HEREDADO RESUELTO: en Python cada edición repoblaba el inspector
// (re-decodificando media) y se atascaba; aquí el inspector solo se repuebla
// al CAMBIAR el cue seleccionado (_loading evita commits durante la carga).
// =============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinCue.Controller;
using WinCue.Core;

namespace WinCue.App;

public sealed class InspectorPanel : ContentControl
{
    /// <summary>Commit de un campo: (cueId, clave snake_case, valor).</summary>
    public Action<int, string, object?>? CommitField;

    /// <summary>RTA de la pestaña EQ: espectro del cue dado (null = no suena).</summary>
    public Func<int, float[]?>? SpectrumProvider;

    /// <summary>Medidor GR del compresor: reducción de ganancia (dB ≥ 0).</summary>
    public Func<int, float>? GainReductionProvider;

    /// <summary>true si el audio del cue pasa por el RACK NATIVO y por tanto
    /// se puede medir (VU por cue, RTA, GR). En cues de vídeo depende del
    /// enrutado: con AUDIO ESPEJO sí; con el audio por mpv, no.</summary>
    public Func<int, bool>? AudioAnalysisProvider;

    /// <summary>Segundos que le quedan al vídeo SALIENTE de la misma pista
    /// cuando este cue entra (lo sabe la proyección del timeline), o null si
    /// no hay saliente / no acaba nunca. Alimenta el aviso de fundido
    /// incoherente; sin cablear, ese aviso concreto no se emite.</summary>
    public Func<int, double?>? CrossfadeHeadroomProvider;

    private readonly EqEditorControl _eqEditor = new();

    /// <summary>El MISMO estado de silencio/solo que la mesa (lo inyecta
    /// MainWindow desde AudioMixerPanel.Board). Los botones de las dos pestañas
    /// son los mismos botones, no dos copias que mantener de acuerdo.</summary>
    public MuteSoloBoard? MuteSolo;

    /// <summary>Avisa a la mesa de que repinte sus strips tras tocar un botón
    /// desde aquí.</summary>
    public Action? MuteSoloChanged;

    /// <summary>Botón CUE: preescuchar ese cue por los cascos.</summary>
    public Action<int>? CueRequested;

    /// <summary>Qué cue suena por los cascos ahora mismo (−1 = ninguno).</summary>
    public Func<int>? AuditionSourceId;

    private Button _muteBtn = null!;
    private Button _soloBtn = null!;

    /// <summary>Botón de la botonera de niveles, con la palabra entera.</summary>
    private static Button LevelButton(string text, string tip) => new()
    {
        Content = text,
        Width = UiMetrics.StripButtonWidth, Height = UiMetrics.StripButtonHeight,
        ToolTip = tip,
        FontSize = 11, FontWeight = FontWeights.Bold,
        Margin = new Thickness(2, 0, 2, 0),
        Background = Theme.ButtonBgBrush,
        Foreground = Theme.TextPrimaryBrush,
    };

    /// <summary>Pone los botones al día con lo que dice el board. Deshabilitados
    /// si el cue no está sonando: sin strip vivo no hay nada que silenciar, y un
    /// botón que se deja pulsar sin efecto es peor que uno apagado.</summary>
    private void RefreshMuteSolo()
    {
        bool live = _cue is not null && MuteSolo?.Knows(_cue.Id) == true;
        _muteBtn.IsEnabled = live;
        // CUE es un PFL: pincha la señal VIVA. Si el cue no suena no hay señal
        // que pinchar, así que va deshabilitado — un botón que se deja pulsar
        // sin efecto enseña a no fiarse del resto. Para oír un cue parado
        // sigue estando la audición en el menú de la lista.
        _soloBtn.IsEnabled = live;

        bool muted = live && MuteSolo!.IsMuted(_cue!.Id);
        bool soloed = _cue is not null && AuditionSourceId?.Invoke() == _cue.Id;
        _muteBtn.Background = muted ? Theme.Solid(Theme.Pgm) : Theme.ButtonBgBrush;
        _muteBtn.Foreground = muted ? Brushes.Black : Theme.TextPrimaryBrush;
        _soloBtn.Background = soloed ? Theme.Solid(Theme.Standby) : Theme.ButtonBgBrush;
        _soloBtn.Foreground = soloed ? Brushes.Black : Theme.TextPrimaryBrush;
    }

    /// <summary>La mesa cambió de estado (o entró/salió un cue): repintar.</summary>
    public void RefreshMuteSoloButtons() => RefreshMuteSolo();

    /// <summary>Coloca el fader donde diga el MODELO. Lo llama la ventana en
    /// cada vuelta de sincronía para que el fader del inspector y el del strip
    /// del mixer sean EL MISMO fader: mover uno movía el otro solo al
    /// reseleccionar el cue, así que el mismo volumen se veía en dos sitios con
    /// dos valores distintos.
    ///
    /// No toca nada mientras el operador tenga ESTE fader agarrado — sería
    /// pelearse con su mano — ni si está escribiendo la cifra.</summary>
    public void SyncVolumeFromModel(Cue? cue)
    {
        if (cue is null || _cue is null || cue.Id != _cue.Id || _volSlider is null)
            return;
        if (_volSlider.IsMouseCaptureWithin || _volSlider.IsKeyboardFocusWithin)
            return;
        if (_volBox is not null && _volBox.IsKeyboardFocusWithin)
            return;
        double target = AudioLevels.GainToPosition(cue.Volume) * VolPosSteps;
        if (Math.Abs(_volSlider.Value - target) < 1.0)
            return;
        _loading = true;   // reflejar, no commitear: el valor ya está en el modelo
        try
        {
            _volSlider.Value = target;
            if (_volBox is not null)
                _volBox.Text = VolumeLabel(cue.Volume);
        }
        finally { _loading = false; }
    }

    /// <summary>Pasos del recorrido del fader de volumen (posición, no ganancia).</summary>
    private const double VolPosSteps = 1000.0;

    private Slider? _volSlider;
    private TextBox? _volBox;

    private readonly TabControl _tabs = new()
    {
        Background = Theme.PanelBgBrush,
        BorderBrush = Theme.Solid(Theme.Divider),
    };

    private Cue? _cue;
    private Func<IReadOnlyList<Cue>>? _allCues;   // para el combo de target
    private bool _loading;                        // suprime commits al poblar

    // Barra de trim con forma de onda (Time & Loops)
    private readonly WaveformControl _wave = new();
    private string _waveLoadedPath = "";

    // Registro clave → escribir valor al control (para poblar al seleccionar)
    private readonly Dictionary<string, Action<Cue>> _loaders = new();
    // Filas con visibilidad condicionada por tipo de cue
    private readonly List<(FrameworkElement El, Func<Cue, bool> VisibleIf)> _conditional = new();

    private readonly TextBlock _header = new()
    {
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(8, 6, 8, 2),
        Foreground = Theme.WarnBrush,
    };

    // Visor de edición (fotograma de referencia para trim y geometría)
    private readonly LiveMonitorControl _editPreview =
        new(Loc.T("EDICIÓN", "EDITING"), Theme.Warn);
    private readonly Grid _previewHost = new()
    {
        Margin = new Thickness(8, 4, 8, 8),
        Visibility = Visibility.Collapsed,
    };

    /// <summary>Chrome del monitor —barra del distintivo y bordes— que va por
    /// fuera del vídeo y no entra en el 16:9 (ver LiveMonitorControl).
    ///
    /// Se estima POR LO ALTO a propósito: el monitor calcula su alto real a
    /// partir del ancho que se le da aquí, y quedarse corto por dos píxeles no
    /// se nota mientras que pasarse los recorta.</summary>
    private const double PreviewChromeH = 26.0;

    public InspectorPanel()
    {
        // Marco AZUL = canvas de salida: mpv recorta exactamente en ese borde,
        // así que lo que quede fuera del marco es lo que se pierde en la salida.
        _editPreview.BorderBrush = Theme.Solid(Theme.Blue);
        _editPreview.BorderThickness = new Thickness(2);
        // El alto NO se fija aquí: LiveMonitorControl lo deriva de su ancho en
        // 16:9, así que quien manda es el ancho del host (ver ResizePreview).
        _previewHost.Children.Add(_editPreview);

        // La explicación del marco azul se va al TOOLTIP del visor, como el
        // detalle del dispositivo en el mixer: se lee una vez en la vida y
        // debajo del monitor solo servía para robarle alto.
        _editPreview.ToolTip = Loc.T(
            "El marco azul es el CANVAS de salida: lo que quede fuera se recorta.\nArrastra el trim para ver el fotograma de corte.",
            "The blue frame is the output CANVAS: whatever falls outside is cropped.\nDrag the trim to see the cut frame.");

        // El visor SACA SU TAMAÑO DEL PANEL en vez de llevar medidas fijas:
        // ocupa todo el alto del inspector y de ahí sale su ancho. Con los
        // 280×176 de antes se quedaba en un sello arriba a la derecha con media
        // columna vacía debajo, mientras la barra de trim —que no sabe qué
        // hacer con el alto— se quedaba con todo el de la izquierda.
        SizeChanged += (_, _) => ResizePreview();

        var body = new DockPanel();
        DockPanel.SetDock(_previewHost, Dock.Right);
        body.Children.Add(_previewHost);
        body.Children.Add(_tabs);

        var root = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        root.Children.Add(_header);
        root.Children.Add(body);
        Content = root;

        // Scrub del trim → fotograma de referencia en el visor
        _wave.Scrubbing += seconds =>
        {
            if (_cue is not { Type: CueType.Video })
                return;
            _editPreview.Scrub(seconds);
            // El fotograma del lienzo sigue al trim: el encuadre se hace sobre
            // el fotograma que de verdad va a verse al entrar el cue.
            RefreshGeometryFrame();
        };

        _tabs.Items.Add(Tab(Loc.T("Básicos", "Basics"), BuildBasics()));
        _tabs.Items.Add(Tab(Loc.T("Disparadores", "Triggers"), BuildTriggers()));
        _timeLoopsTab = Tab(Loc.T("Tiempo y bucles", "Time & Loops"), BuildTimeLoops());
        _tabs.Items.Add(_timeLoopsTab);
        _geometryTab = Tab(Loc.T("Geometría", "Geometry"), BuildGeometry());
        _tabs.Items.Add(_geometryTab);
        // TODO el audio del cue en UNA pestaña: niveles + EQ/comp (antes eran
        // dos, "Levels" y "EQ" — regla: Inspector = el cue, Mixer = el show)
        _eqEditor.Commit = (key, value) => Commit(key, value);
        _eqEditor.SpectrumProvider = () =>
            _cue is null ? null : SpectrumProvider?.Invoke(_cue.Id);
        _eqEditor.GainReductionProvider = () =>
            _cue is null ? 0f : GainReductionProvider?.Invoke(_cue.Id) ?? 0f;
        _audioTab = Tab($"{Icons.Volume} Audio", BuildAudioTab());
        _tabs.Items.Add(_audioTab);

        // El visor solo acompaña a las pestañas donde SIRVE para algo (ver
        // RefreshPreviewVisibility). Está fuera del TabControl —es hermano
        // suyo, no hijo de ninguna pestaña— así que no se oculta solo: hay que
        // reevaluarlo cada vez que cambia la pestaña.
        _tabs.SelectionChanged += (_, e) =>
        {
            // SelectionChanged BURBUJEA: cualquier ComboBox de dentro de una
            // pestaña dispara el mismo evento y llega hasta aquí. Sin este
            // filtro, elegir una opción en un desplegable recalcularía el visor.
            if (!ReferenceEquals(e.OriginalSource, _tabs))
                return;
            RefreshPreviewVisibility();
            // Al ABRIR geometría se vuelve a pedir el fotograma: para entonces
            // mpv lleva rato cargado, así que este es el intento que casi
            // siempre acierta aunque los de la carga del cue fallaran.
            if (ReferenceEquals(_tabs.SelectedItem, _geometryTab)
                && _cue is { Type: CueType.Video } gc && gc.FilePath.Length > 0)
                RefreshGeometryFrame();
        };

        IsEnabled = false;   // sin cue seleccionado
    }

    /// <summary>Muestra (y edita) el cue dado. null → inspector deshabilitado.</summary>
    public void SetCue(Cue? cue, Func<IReadOnlyList<Cue>> allCues)
    {
        _cue = cue;
        _allCues = allCues;
        IsEnabled = cue is not null;
        if (cue is null)
        {
            _header.Text = Loc.T("— sin cue seleccionado —", "— no cue selected —");
            // Sin esto el visor se quedaba con el fotograma del cue anterior,
            // ya deseleccionado, encima de un inspector deshabilitado.
            RefreshPreviewVisibility();
            return;
        }

        _loading = true;
        try
        {
            _header.Text = $"Cue {cue.DisplayNumber} · {cue.Type.ToString().ToUpperInvariant()} · {cue.Name}";
            foreach (var loader in _loaders.Values)
                loader(cue);
            foreach (var (el, visibleIf) in _conditional)
                el.Visibility = visibleIf(cue) ? Visibility.Visible : Visibility.Collapsed;
            UpdateWave(cue);
            UpdateEditPreview(cue);
            _eqEditor.SetCue(cue, HasAudioAnalysis(cue));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>true si el cue tiene analizador REAL (su audio va por el rack
    /// nativo). Sin provider cableado (tests/diseño) se asume el
    /// comportamiento clásico: solo los cues de AUDIO.</summary>
    private bool HasAudioAnalysis(Cue cue) =>
        AudioAnalysisProvider?.Invoke(cue.Id) ?? cue.Type == CueType.Audio;

    /// <summary>Visor de edición: solo para cues de VÍDEO con archivo.</summary>
    private void UpdateEditPreview(Cue cue)
    {
        if (cue.Type == CueType.Video && cue.FilePath.Length > 0)
        {
            _editPreview.ShowPaused(cue.FilePath, cue.TrimStart);
            ApplyPreviewGeometry(cue);
            RefreshGeometryFrame();
        }
        else
        {
            _editPreview.Clear();
            _geoCanvas.SetFrame(null);
        }
        RefreshPreviewVisibility();
    }

    /// <summary>Pide al visor un fotograma fijo y lo mete en el lienzo de
    /// geometría, para poder encuadrar contra la imagen real y no contra un
    /// rectángulo abstracto.
    ///
    /// Se pide con retardo porque ShowPaused() acaba de mandarle a mpv el
    /// fichero y el salto al punto de trim: si se capturase de inmediato saldría
    /// el fotograma anterior, o nada. Y se pide MIENTRAS el visor sigue cargado,
    /// no cuando se abre la pestaña, para que el mapa de bits ya esté listo
    /// aunque el visor esté oculto.
    ///
    /// async void y no async Task: es un manejador de refresco, nadie lo espera.
    /// Por eso se traga sus propios fallos — un fotograma que no llega deja el
    /// relleno ámbar de siempre, que sigue siendo utilizable.</summary>
    private async void RefreshGeometryFrame()
    {
        int token = ++_frameToken;
        try
        {
            // Se reintenta con esperas crecientes porque el momento en que mpv
            // tiene el fotograma listo NO es predecible: recién arrancada la
            // app tarda bastante más que con el reproductor ya caliente, y con
            // un solo intento el lienzo se quedaba en el relleno ámbar justo en
            // el caso más común — abrir la app y mirar la geometría.
            foreach (int wait in new[] { 220, 500, 1200 })
            {
                await Task.Delay(wait);
                // Otro cue entró mientras esperábamos: su fotograma manda.
                if (token != _frameToken)
                    return;
                var still = await _editPreview.TryGrabStillAsync();
                if (token != _frameToken)
                    return;
                if (still is not null)
                {
                    _geoCanvas.SetFrame(still);
                    return;
                }
            }
        }
        catch (Exception) { }
    }

    /// <summary>Descarta capturas de fotograma que han quedado obsoletas.</summary>
    private int _frameToken;

    /// <summary>Decide si se ve el visor de edición. Dos condiciones: que el cue
    /// tenga vídeo y que la pestaña abierta sea una de las dos donde el visor
    /// SIRVE — Geometría (encuadrar contra el canvas) y Tiempo y bucles (ver el
    /// fotograma en el que cortas al arrastrar el trim).
    ///
    /// En Básicos, Disparadores y Audio no pinta nada y solo robaba 280 px de
    /// ancho a los controles.
    ///
    /// Se oculta, no se descarga: el vídeo sigue cargado y pausado, igual que
    /// antes. Descargarlo y recargarlo en cada cambio de pestaña costaría un
    /// arranque de mpv por clic.</summary>
    /// <summary>Ancho del visor a partir del alto DISPONIBLE, en 16:9. Se le da
    /// el ancho porque el monitor deriva su propio alto de él; darle el alto
    /// sería pelearse con su SizeChanged.</summary>
    private void ResizePreview()
    {
        // Alto del cuerpo = el del inspector menos la cabecera y los márgenes
        // verticales del host (4 arriba + 8 abajo).
        double bodyH = ActualHeight - _header.ActualHeight - 12.0;
        if (bodyH <= 60.0)
            return;
        // Topes: por debajo de 220 no se distingue el encuadre y por encima de
        // 620 el visor le come la pestaña a los campos, que es lo que se edita.
        _previewHost.Width = Math.Clamp((bodyH - PreviewChromeH) * 16.0 / 9.0, 220.0, 620.0);
    }

    private void RefreshPreviewVisibility()
    {
        // En GEOMETRÍA ya no acompaña: el fotograma vive DENTRO del lienzo, que
        // es donde se encuadra. Tenerlo además al lado sería la misma imagen dos
        // veces robando ancho al lienzo, que es justo lo que interesa grande.
        bool usefulHere = ReferenceEquals(_tabs.SelectedItem, _timeLoopsTab);
        bool hasVideo = _cue is { Type: CueType.Video } c && c.FilePath.Length > 0;

        bool show = usefulHere && hasVideo;
        _previewHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Volver de Colapsado no cambia el tamaño del inspector, así que su
        // SizeChanged no salta y el visor se quedaría con el ancho sin calcular.
        if (show)
            ResizePreview();
    }

    /// <summary>Refleja la geometría del cue en el visor (misma matemática que
    /// VideoEngine; el canvas de referencia es 1920×1080).</summary>
    private void ApplyPreviewGeometry(Cue cue)
    {
        if (cue.GeoMode == GeoMode.Custom)
        {
            long rot = ((long)(Math.Round(cue.RotateZ / 90.0) * 90) % 360 + 360) % 360;
            _editPreview.SetGeometry(
                zoom: Math.Log2(Math.Max(0.01, cue.ScaleX)),
                panX: cue.TranslateX / 960.0,
                panY: -cue.TranslateY / 540.0,
                rotate: rot,
                keepAspect: true);
        }
        else
        {
            double baseZoom = Math.Abs(cue.Scale - 100.0) < 0.01
                ? 0.0 : Math.Log2(Math.Max(1.0, cue.Scale) / 100.0);
            _editPreview.SetGeometry(baseZoom, 0.0, 0.0, 0L, cue.PreserveAspect);
        }
    }

    /// <summary>Carga la barra de trim (decodifica solo si cambia el archivo).</summary>
    private void UpdateWave(Cue cue, bool force = false)
    {
        if (!cue.IsMedia() || cue.FilePath.Length == 0)
        {
            _wave.Clear();
            _waveLoadedPath = "";
            return;
        }
        if (!force && cue.FilePath == _waveLoadedPath)
        {
            _wave.SetMarkers(cue.TrimStart, cue.TrimEnd);
            return;
        }
        _waveLoadedPath = cue.FilePath;
        if (cue.Type == CueType.Audio)
            _wave.LoadAudio(cue.FilePath, cue.TrimStart, cue.TrimEnd);
        else
            _wave.LoadVideo(cue.FilePath, cue.TrimStart, cue.TrimEnd);
    }

    private void Commit(string key, object? value)
    {
        if (_loading || _cue is null)
            return;
        CommitField?.Invoke(_cue.Id, key, value);
        // Actualizar cabecera si cambió nombre/número (sin repoblar el resto)
        _header.Text = $"Cue {_cue.DisplayNumber} · {_cue.Type.ToString().ToUpperInvariant()} · {_cue.Name}";

        // El aviso de fundido incoherente depende de fades, trim y duración
        if (FadeWarnKeys.Contains(key))
            UpdateFadeWarning(_cue);

        // Sincronía barra de trim ↔ campos numéricos / archivo
        if (key is "trim_start" or "trim_end")
        {
            _wave.SetMarkers(_cue.TrimStart, _cue.TrimEnd);
            if (_cue.Type == CueType.Video)
                _editPreview.Scrub(key == "trim_start" ? _cue.TrimStart : _cue.TrimEnd);
        }
        else if (key == "file_path")
        {
            UpdateWave(_cue, force: true);
            UpdateEditPreview(_cue);
        }
        else if (GeoPreviewKeys.Contains(key))
        {
            // Reflejar en visor, lienzo y controles (sin re-commitear)
            if (_cue.Type == CueType.Video)
                ApplyPreviewGeometry(_cue);
            bool was = _loading;
            _loading = true;
            try
            {
                if (_loaders.TryGetValue(key, out var loader))
                    loader(_cue);
                SyncGeometryUi(_cue);
            }
            finally { _loading = was; }
        }
    }

    private static readonly HashSet<string> GeoPreviewKeys = new()
    {
        "geo_mode", "preserve_aspect", "scale", "translate_x", "translate_y",
        "scale_x", "rotate_z",
    };   // los cambios de estas claves se reflejan en visor + lienzo + controles

    // ==================================================================
    // Pestañas
    // ==================================================================

    private TextBox? _nameBox;

    /// <summary>F2: enfoca el campo Nombre (renombrado rápido).</summary>
    public void FocusName()
    {
        _tabs.SelectedIndex = 0;
        _nameBox?.Focus();
        _nameBox?.SelectAll();
    }

    private UIElement BuildBasics()
    {
        var (scroll, panel) = Page();
        var cols = Columns(panel, 3);

        Header(cols[0], Loc.T("IDENTIDAD", "IDENTITY"));
        Txt(cols[0], "number", Loc.T("Número:", "Number:"), c => c.Number);
        _nameBox = Txt(cols[0], "name", Loc.T("Nombre:", "Name:"), c => c.Name);
        ColorSwatches(cols[0]);
        var armedRow = ChkRow(cols[0]);
        Chk(armedRow, "armed", Loc.T("Armado", "Armed"), c => c.Armed);
        Chk(armedRow, "flagged", $"{Icons.Flag} Flag", c => c.Flagged);
        TxtMultiline(cols[0], "notes", Loc.T("Notas (se muestran en standby):", "Notes (shown in standby):"),
            c => c.Notes);

        Header(cols[1], Loc.T("TEMPORIZACIÓN", "TIMING"));
        Cmb(cols[1], "continue_mode", Loc.T("Al terminar:", "On finish:"), c => c.ContinueMode.ToString(),
            ("None", Loc.T("Nada (GO manual)", "None (manual GO)")),
            ("AutoContinue", Loc.T("Auto-continue (tras post-wait)", "Auto-continue (after post-wait)")),
            ("AutoFollow", Loc.T("Auto-follow (al acabar la acción)", "Auto-follow (when the action ends)")));
        TimeF(cols[1], "pre_wait", "Pre-wait:", c => c.PreWait);
        TimeF(cols[1], "duration", Loc.T("Duración:", "Duration:"), c => c.Duration);
        TimeF(cols[1], "post_wait", "Post-wait:", c => c.PostWait);

        Header(cols[2], Loc.T("DESTINO", "TARGET"));
        FileRow(cols[2]);

        // Solo cues que apuntan a otro cue
        var targetRow = Conditional(cols[2], c => c.TargetsOther());
        TargetCombo(targetRow);
        var fadeStopRow = Conditional(cols[2], c => c.Type == CueType.Fade);
        Chk(fadeStopRow, "fade_stop_on_done",
            Loc.T("Detener el objetivo al llegar a 0", "Stop the target when it reaches 0"),
            c => c.FadeStopOnDone);

        // Solo grupos
        var groupRow = Conditional(cols[2], c => c.Type == CueType.Group);
        CmbInline(groupRow, "group_mode", Loc.T("Modo de grupo:", "Group mode:"), c => c.GroupMode.ToString(),
            ("Enter", Loc.T("ENTER — el playhead entra", "ENTER — the playhead enters")),
            ("Timeline", Loc.T("TIMELINE — todos a la vez", "TIMELINE — all at once")),
            ("GoNext", Loc.T("GO NEXT — primer hijo", "GO NEXT — first child")),
            ("Random", Loc.T("RANDOM — aleatorio round-robin", "RANDOM — round-robin random")));

        var stopOthersRow = ChkRow(cols[2]);
        Chk(stopOthersRow, "stop_others", Loc.T("Detener los demás al empezar (GO per-cue)", "Stop others when starting (per-cue GO)"),
            c => c.StopOthers);

        return scroll;
    }


    private UIElement BuildTriggers()
    {
        var (scroll, panel) = Page();
        var cols = Columns(panel, 3);

        Header(cols[0], Loc.T("DISPARO LOCAL", "LOCAL TRIGGER"));
        Txt(cols[0], "hotkey", Loc.T("Hotkey (tecla):", "Hotkey (key):"), c => c.Hotkey);
        Cmb(cols[0], "retrigger", Loc.T("Segundo disparo:", "Second trigger:"), c => c.Retrigger.ToString(),
            ("None", Loc.T("Ignorar", "Ignore")),
            ("Panic", Loc.T("Panic (fade y parar)", "Panic (fade and stop)")),
            ("Stop", Loc.T("Parar", "Stop")),
            ("HardStop", Loc.T("Parada dura", "Hard stop")),
            ("HardRestart", Loc.T("Reiniciar", "Restart")));

        Header(cols[0], Loc.T("FUNDIR Y PARAR OTROS", "FADE & STOP OTHERS"));
        var foRow = ChkRow(cols[0]);
        Chk(foRow, "fade_others",
            Loc.T("Al dispararse, funde y para otros cues", "On trigger, fade and stop other cues"),
            c => c.FadeOthers);
        Num(cols[0], "fade_others_time", Loc.T("Tiempo del fundido (s):", "Fade time (s):"), c => c.FadeOthersTime);
        Cmb(cols[0], "fade_others_scope", Loc.T("Ámbito:", "Scope:"), c => c.FadeOthersScope.ToString(),
            ("Peers", Loc.T("PEERS — mismo nivel/grupo", "PEERS — same level/group")),
            ("List", Loc.T("LIST — toda la lista", "LIST — the whole list")),
            ("All", Loc.T("ALL — todo", "ALL — everything")));

        Header(cols[1], Loc.T("WALL-CLOCK (disparo por hora)", "WALL-CLOCK (time-of-day trigger)"));
        var wcRow = ChkRow(cols[1]);
        Chk(wcRow, "wallclock_enabled", Loc.T("Disparar a una hora del día", "Trigger at a time of day"), c => c.WallclockEnabled);
        Txt(cols[1], "wallclock_time", Loc.T("Hora (HH:MM:SS, 24h):", "Time (HH:MM:SS, 24h):"), c => c.WallclockTime);
        Note(cols[1], Loc.T("Se dispara cada día a esa hora\nmientras el show esté abierto.", "Triggers every day at that time\nwhile the show is open."));

        Header(cols[2], Loc.T("MIDI (backend en fase 6)", "MIDI (backend in phase 6)"));
        var midiRow = ChkRow(cols[2]);
        Chk(midiRow, "midi_enabled", Loc.T("Disparo por MIDI", "MIDI trigger"), c => c.MidiEnabled);
        Cmb(cols[2], "midi_type", Loc.T("Tipo de mensaje:", "Message type:"), c => c.MidiType.ToString(),
            ("NoteOn", "Note On"), ("NoteOff", "Note Off"),
            ("ControlChange", "Control Change"), ("ProgramChange", "Program Change"));
        Int(cols[2], "midi_channel", Loc.T("Canal:", "Channel:"), c => c.MidiChannel);
        Int(cols[2], "midi_note", Loc.T("Nota / CC:", "Note / CC:"), c => c.MidiNote);
        Int(cols[2], "midi_velocity", Loc.T("Velocity (-1 = cualquiera):", "Velocity (-1 = any):"), c => c.MidiVelocity);

        return scroll;
    }

    // ── Aviso de fundido incoherente (Tiempo y bucles) ─────────────────
    private const string FadeWarnKey = "__fade_warn";

    /// <summary>Campos cuya edición puede volver coherente (o incoherente) un
    /// fundido: al commitear cualquiera de ellos se recalcula el aviso.</summary>
    private static readonly HashSet<string> FadeWarnKeys = new(StringComparer.Ordinal)
    { "fade_in", "fade_out", "trim_start", "trim_end", "duration", "loop", "file_path" };

    private readonly TextBlock _fadeWarn = new()
    {
        // Ámbar de aviso — el mismo tono del resto de la app; discreto
        Foreground = Theme.WarnBrush,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 280,
        FontSize = 11,
        Margin = new Thickness(0, 2, 0, 4),
        Visibility = Visibility.Collapsed,
    };

    /// <summary>Pinta (o esconde) el aviso. NO impide nada: ni bloquea el
    /// guardado ni recorta el valor — solo cuenta qué va a pasar.</summary>
    private void UpdateFadeWarning(Cue cue)
    {
        double? remaining = cue.Type == CueType.Video
            ? CrossfadeHeadroomProvider?.Invoke(cue.Id) : null;
        var issues = FadeSanity.Check(cue, remaining);
        string? text = FadeSanity.ShortText(issues);
        if (text is null)
        {
            _fadeWarn.Visibility = Visibility.Collapsed;
            _fadeWarn.ToolTip = null;
            return;
        }
        _fadeWarn.Text = text;
        _fadeWarn.ToolTip = FadeSanity.Explain(
            issues, FadeSanity.EffectiveDuration(cue), remaining);
        _fadeWarn.Visibility = Visibility.Visible;
    }

    private UIElement BuildTimeLoops()
    {
        // Sin scroll: campos arriba y la barra de trim justo debajo, con su alto
        // propio. LastChildFill queda en false a propósito — con el relleno
        // puesto, la barra se quedaba con todo el alto sobrante de la pestaña.
        var root = new DockPanel
        { Margin = new Thickness(10, 4, 10, 10), LastChildFill = false };

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var cols = Columns(top, 2);

        Header(cols[0], Loc.T("REPRODUCCIÓN", "PLAYBACK"));
        var loopRow = ChkRow(cols[0]);
        Chk(loopRow, "loop", Loc.T("Loop infinito (hasta STOP)", "Infinite loop (until STOP)"), c => c.Loop);
        Int(cols[0], "play_count", Loc.T("Repeticiones (play count):", "Repeats (play count):"), c => c.PlayCount);
        Num(cols[0], "rate", Loc.T("Velocidad (1.0 = normal):", "Speed (1.0 = normal):"), c => c.Rate);
        var pitchRow = ChkRow(cols[0]);
        Chk(pitchRow, "preserve_pitch",
            Loc.T("Mantener tono al cambiar velocidad", "Keep pitch when changing speed"),
            c => c.PreservePitch);

        Header(cols[1], Loc.T("FUNDIDOS Y RECORTE", "FADES & TRIM"));
        TimeF(cols[1], "fade_in", "Fade in:", c => c.FadeIn);
        TimeF(cols[1], "fade_out", "Fade out:", c => c.FadeOut);
        // AVISO SUAVE (nunca bloqueo): un fundido más largo que el tiempo
        // sobre el que puede actuar. Los fundidos largos siguen permitidos.
        cols[1].Children.Add(_fadeWarn);
        _loaders[FadeWarnKey] = UpdateFadeWarning;
        TimeF(cols[1], "trim_start", Loc.T("Trim: inicio:", "Trim: start:"), c => c.TrimStart);
        TimeF(cols[1], "trim_end", Loc.T("Trim: fin (0 = final):", "Trim: end (0 = end):"), c => c.TrimEnd);

        Header(top, Loc.T("BARRA DE TRIM — arrastra los manejadores", "TRIM BAR — drag the handles"));
        _wave.Margin = new Thickness(0, 4, 0, 0);
        // Alto FIJO: el MinHeight del propio control se anula, o ganaría él.
        _wave.MinHeight = 0;
        _wave.Height = UiMetrics.TrimBarHeight;
        DockPanel.SetDock(_wave, Dock.Top);
        _wave.MarkersChanged += (start, end) =>
        {
            if (_cue is null) return;
            Commit("trim_start", Math.Round(start, 3));
            Commit("trim_end", Math.Round(end, 3));
            // Reflejar en los campos numéricos sin re-commitear
            _loading = true;
            try
            {
                _loaders["trim_start"](_cue);
                _loaders["trim_end"](_cue);
            }
            finally { _loading = false; }
        };
        root.Children.Add(_wave);

        return root;
    }

    // ── Pestaña Geometry: controles + lienzo interactivo ───────────────
    private readonly GeometryCanvasControl _geoCanvas = new();
    private StackPanel _fullPanel = null!;
    private StackPanel _customPanel = null!;
    private RadioButton _rbFull = null!;
    private RadioButton _rbCustom = null!;
    private readonly List<System.Windows.Controls.Primitives.ToggleButton> _rotButtons = new();

    private UIElement BuildGeometry()
    {
        var root = new DockPanel();

        // DOS COLUMNAS. En una sola, los controles de geometría suman más alto
        // del que tiene el panel y aparecía una barra de desplazamiento: en una
        // superficie de directo, un control que hay que buscar deslizando es un
        // control que no está. Repartidos en dos, entran de una vez.
        //
        // El ScrollViewer se conserva como red de seguridad para ventanas muy
        // bajas, pero con la altura normal no llega a mostrar barra.
        var controls = new StackPanel { Margin = new Thickness(10, 6, 12, 10), Width = 330 };
        var controlsRight = new StackPanel { Margin = new Thickness(0, 6, 14, 10), Width = 250 };

        var twoCols = new StackPanel { Orientation = Orientation.Horizontal };
        twoCols.Children.Add(controls);
        twoCols.Children.Add(controlsRight);

        var scrollLeft = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = twoCols,
        };
        DockPanel.SetDock(scrollLeft, Dock.Left);
        root.Children.Add(scrollLeft);

        // El lienzo ocupa TODO el espacio restante de la pestaña
        _geoCanvas.Margin = new Thickness(0, 6, 10, 10);
        _geoCanvas.Translated += (tx, ty) =>
        {
            Commit("translate_x", tx);
            Commit("translate_y", ty);
        };
        // Commit dispara la re-lectura de los controles, así que el deslizador
        // de «Escala (×)» sigue al tirador solo; no hay que tocarlo aquí.
        _geoCanvas.Scaled += sx => Commit("scale_x", sx);
        root.Children.Add(_geoCanvas);

        Header(controls, Loc.T("MODO", "MODE"));
        _rbFull = new RadioButton
        {
            Content = Loc.T("Superficie completa — llena el canvas", "Full surface — fills the canvas"),
            GroupName = "geo_mode_grp", Margin = new Thickness(0, 2, 0, 2),
            Foreground = Theme.TextPrimaryBrush,
        };
        _rbCustom = new RadioButton
        {
            Content = Loc.T("Posición manual — escala, posición y giro", "Manual position — scale, position and rotation"),
            GroupName = "geo_mode_grp", Margin = new Thickness(0, 2, 0, 2),
            Foreground = Theme.TextPrimaryBrush,
        };
        _rbFull.Checked += (_, _) => OnGeoModeChanged(false);
        _rbCustom.Checked += (_, _) => OnGeoModeChanged(true);
        controls.Children.Add(_rbFull);
        controls.Children.Add(_rbCustom);

        // ── Panel FULL ──────────────────────────────────────────────────
        _fullPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(_fullPanel);
        var aspectRow = ChkRow(_fullPanel);
        Chk(aspectRow, "preserve_aspect", Loc.T("Mantener relación de aspecto", "Keep aspect ratio"),
            c => c.PreserveAspect);
        SliderF(_fullPanel, "scale", Loc.T("Escala (%):", "Scale (%):"), 10, 400, c => c.Scale, "0");

        // ── Panel CUSTOM ────────────────────────────────────────────────
        _customPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(_customPanel);
        SliderF(_customPanel, "scale_x", Loc.T("Escala (×):", "Scale (×):"), 0.1, 4.0, c => c.ScaleX, "0.00");
        SliderF(_customPanel, "translate_x", Loc.T("Posición X (px):", "Position X (px):"), -960, 960, c => c.TranslateX, "0");
        SliderF(_customPanel, "translate_y", Loc.T("Posición Y (px):", "Position Y (px):"), -540, 540, c => c.TranslateY, "0");

        var rotRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        rotRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Rotación:", "Rotation:"), Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        foreach (int deg in new[] { 0, 90, 180, 270 })
        {
            var tb = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = $"{deg}°", Width = 46, Tag = deg,
                Margin = new Thickness(0, 0, 4, 0),
                Background = Theme.ButtonBgBrush,
                Foreground = Theme.TextPrimaryBrush,
            };
            tb.Click += (_, _) =>
            {
                if (_loading || _cue is null) return;
                Commit("rotate_z", (double)deg);
            };
            _rotButtons.Add(tb);
            rotRow.Children.Add(tb);
        }
        _customPanel.Children.Add(rotRow);

        var btnRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var btnCenter = new Button { Content = $"{Icons.Target} {Loc.T("Centrar", "Center")}", Padding = new Thickness(10, 4, 10, 4) };
        var btnReset = new Button
        { Content = $"{Icons.Reset} {Loc.T("Restablecer", "Reset")}", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        btnCenter.Click += (_, _) =>
        {
            Commit("translate_x", 0.0);
            Commit("translate_y", 0.0);
        };
        btnReset.Click += (_, _) =>
        {
            Commit("scale_x", 1.0);
            Commit("translate_x", 0.0);
            Commit("translate_y", 0.0);
            Commit("rotate_z", 0.0);
        };
        btnRow.Children.Add(btnCenter);
        btnRow.Children.Add(btnReset);
        _customPanel.Children.Add(btnRow);

        // (El campo legacy "layer" top/bottom del Python se retiró de la UI:
        // la capa REAL es timeline_layer/📌 — dos conceptos de capa a la
        // vista solo provocaban errores. El JSON lo conserva por compat.)
        // ── Columna DERECHA: compositor y salida ────────────────────────
        Header(controlsRight, Loc.T("COMPOSITOR", "COMPOSITOR"));
        var compRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        compRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Opacidad %:", "Opacity %:"), VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
            Margin = new Thickness(0, 0, 4, 0),
        });
        var opacityBox = MakeTextBox(48);
        WireTextCommit(opacityBox, "opacity", () =>
            double.TryParse(opacityBox.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double ov)
                ? ov : (object?)null);
        compRow.Children.Add(opacityBox);
        controlsRight.Children.Add(compRow);
        _loaders["opacity"] = c => opacityBox.Text = c.Opacity.ToString("0.#",
            System.Globalization.CultureInfo.InvariantCulture);

        // ── Salida de vídeo del cue (multi-monitor estilo Watchout) ─────
        Header(controlsRight, Loc.T("SALIDA", "OUTPUT"));
        var outRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        outRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Salida:", "Output:"), VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
            Margin = new Thickness(0, 0, 4, 0),
        });
        var screenCombo = new ComboBox { MinWidth = 170 };
        for (int s = 0; s < 4; s++)
            screenCombo.Items.Add(new ComboBoxItem
            {
                Content = s == 0
                    ? Loc.T("Salida 1 (principal)", "Output 1 (main)")
                    : Loc.T($"Salida {s + 1}", $"Output {s + 1}"),
                Tag = s,
            });
        screenCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || _cue is null) return;
            if ((screenCombo.SelectedItem as ComboBoxItem)?.Tag is int scr)
                Commit("video_output", scr);
        };
        outRow.Children.Add(screenCombo);
        controlsRight.Children.Add(outRow);
        var outNote = new TextBlock
        {
            Text = Loc.T("El monitor de cada salida se asigna en Settings → Vídeo.", "Each output's monitor is set in Settings → Video."),
            Foreground = Theme.TextDimBrush,
            FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        controlsRight.Children.Add(outNote);
        var canvasNote = new TextBlock
        {
            Text = Loc.T(
                "MODO CANVAS activo: el cue se coloca en el canvas y cada salida muestra su recorte (Settings → Vídeo).",
                "CANVAS MODE active: the cue is placed on the canvas and each output shows its crop (Settings → Video)."),
            Foreground = Theme.TextDimBrush,
            FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        controlsRight.Children.Add(canvasNote);
        _loaders["video_output"] = c =>
        {
            screenCombo.SelectedIndex = Math.Clamp(c.VideoOutput, 0, 3);
            // Con el modo canvas la selección de salida SE IGNORA: ocultarla
            // (mostrar un control muerto invita a errores en directo)
            bool canvasMode = SettingsManager.Instance.Get("video_canvas_mode", false)
                && SettingsManager.Instance.Get("video_compositor", false);
            outRow.Visibility = canvasMode ? Visibility.Collapsed : Visibility.Visible;
            outNote.Visibility = canvasMode ? Visibility.Collapsed : Visibility.Visible;
            canvasNote.Visibility = canvasMode ? Visibility.Visible : Visibility.Collapsed;
        };

        // Sincroniza radios/paneles/lienzo al cargar un cue
        _loaders["__geo_sync"] = SyncGeometryUi;

        return root;
    }

    private void OnGeoModeChanged(bool custom)
    {
        if (_loading || _cue is null)
            return;
        Commit("geo_mode", custom ? "CUSTOM" : "FULL");
    }

    /// <summary>Radios, visibilidad de paneles, botones de rotación y lienzo.</summary>
    private void SyncGeometryUi(Cue c)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            bool custom = c.GeoMode == GeoMode.Custom;
            _rbFull.IsChecked = !custom;
            _rbCustom.IsChecked = custom;
            _fullPanel.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
            _customPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

            long rot = (((long)(Math.Round(c.RotateZ / 90.0) * 90)) % 360 + 360) % 360;
            foreach (var tb in _rotButtons)
                tb.IsChecked = (int)tb.Tag == rot;

            _geoCanvas.SetValues(custom, c.Scale, c.PreserveAspect,
                c.ScaleX, c.TranslateX, c.TranslateY, rot);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>Slider + caja numérica sincronizados; commit EN VIVO al deslizar.</summary>
    private void SliderF(StackPanel panel, string key, string label,
                         double min, double max, Func<Cue, double> read, string fmt)
    {
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock
        {
            Text = label, Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Width = 140,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = MakeTextBox(52);
        box.Margin = new Thickness(6, 0, 0, 0);

        slider.ValueChanged += (_, e) =>
        {
            if (_loading) return;
            box.Text = e.NewValue.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            Commit(key, e.NewValue);
        };
        WireTextCommit(box, key, () =>
        {
            if (!double.TryParse(box.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                return null;
            v = Math.Clamp(v, min, max);
            bool was = _loading;
            _loading = true;
            try { slider.Value = v; } finally { _loading = was; }
            return v;
        });

        row.Children.Add(slider);
        row.Children.Add(box);
        panel.Children.Add(row);
        _loaders[key] = c =>
        {
            double v = read(c);
            slider.Value = Math.Clamp(v, min, max);
            box.Text = v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        };
    }

    // VU ESTÉREO del cue seleccionado (recibe niveles reales del mixer), con las
    // mismas medidas que el par de la mesa.
    private readonly VuMeterControl _levelVu = new(vertical: true);
    private readonly VuMeterControl _levelVuR = new(vertical: true);

    /// <summary>Nivel RMS de un cue (solo se pinta si es el seleccionado).</summary>
    public void UpdateCueLevel(int cueId, float rms)
    {
        if (_cue?.Id == cueId)
        {
            _levelVu.SetLevel(rms);
            _levelVuR.SetLevel(rms);
        }
    }

    /// <summary>Nivel ESTÉREO del cue seleccionado. Es el que usa la ventana:
    /// el mono de arriba se queda para quien solo tenga un valor.</summary>
    public void UpdateCueLevel(int cueId, float left, float right)
    {
        if (_cue?.Id != cueId)
            return;
        _levelVu.SetLevel(left);
        _levelVuR.SetLevel(right);
    }

    private TabItem _audioTab = null!;

    // Las dos pestañas que acompañan al visor de edición (RefreshPreviewVisibility)
    private TabItem _timeLoopsTab = null!;
    private TabItem _geometryTab = null!;

    /// <summary>Selecciona la pestaña 🔊 Audio (clic en un strip del Mixer).</summary>
    public void ShowAudioTab() => _tabs.SelectedItem = _audioTab;

    /// <summary>Pestaña 🔊 Audio: niveles del cue a la izquierda y editor de
    /// EQ/compresor (con RTA) a la derecha — todo el audio del cue junto.</summary>
    private UIElement BuildAudioTab()
    {
        var levelsHost = new Border
        {
            BorderBrush = Theme.Solid(Theme.Divider),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = BuildLevels(),
            // 360 y no 320: con 320 el desplegable de «salida de este cue» se
            // cortaba contra la columna de GANANCIA. Un desplegable que no deja
            // leer la opción elegida no sirve para lo único que hace.
            MaxWidth = 360,
        };
        var host = new DockPanel();
        DockPanel.SetDock(levelsHost, Dock.Left);
        host.Children.Add(levelsHost);
        // Ahora que la EQ y el compresor tienen alto FIJO, hace falta la misma
        // red de seguridad que en Geometría: con el inspector muy bajo se
        // recortarían sin salida. Con la altura normal la barra no aparece.
        host.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _eqEditor,
        });
        return host;
    }

    private UIElement BuildLevels()
    {
        var (scroll, panel) = Page();

        // ── Bloque estilo mesa: fader vertical + VU vertical + lectura ──
        var mixer = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(mixer);

        var faderCol = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        faderCol.Children.Add(new TextBlock
        {
            Text = "VOL",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextDimBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        // Recorre POSICIÓN, no ganancia, con la MISMA ley que los faders del
        // mixer (AudioLevels): dos faders del mismo volumen tienen que estar a
        // la misma altura, y el techo (+10 dB) tiene que ser el mismo en los
        // dos sitios o el mixer permitiría lo que el inspector recorta.
        const double posSteps = VolPosSteps;
        var slider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = posSteps,
            Width = UiMetrics.FaderWidth, Height = UiMetrics.FaderHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        // ESCALA + FADER, como en la mesa. Un fader con escala en el mixer y el
        // mismo volumen sin ella aquí se lee como si midieran cosas distintas.
        var faderRow = new StackPanel
        { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        faderRow.Children.Add(new FaderScaleControl
        { Width = UiMetrics.FaderScaleWidth, Height = UiMetrics.FaderHeight });
        faderRow.Children.Add(slider);
        // VU ESTÉREO pegado al fader, con las mismas medidas que en la mesa: dos
        // medidores de la misma señal con distinto ancho y distinto largo se
        // leen como si midieran cosas distintas.
        _levelVu.Width = 7;
        _levelVu.Height = UiMetrics.FaderHeight;
        _levelVu.Margin = new Thickness(6, 0, 0, 0);
        _levelVuR.Width = 7;
        _levelVuR.Height = UiMetrics.FaderHeight;
        _levelVuR.Margin = new Thickness(1, 0, 0, 0);
        faderRow.Children.Add(_levelVu);
        faderRow.Children.Add(_levelVuR);
        faderCol.Children.Add(faderRow);

        // La lectura va DEBAJO del fader, como en el mixer y en el GAIN del EQ.
        // Antes vivía en la columna de la derecha: el mismo dato en tres sitios
        // distintos según la pestaña obliga a buscarlo cada vez.
        // EDITABLE: se teclean los dB, igual que en la mesa. Sin fondo ni borde
        // propios —el marco lo pone el Border de fuera—, así que se ve igual que
        // cuando era una etiqueta.
        var label = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Theme.GoBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CaretBrush = Theme.GoBrush,
            Padding = new Thickness(0),
            ToolTip = Loc.T("Escribe los dB y pulsa Intro",
                            "Type the dB value and press Enter"),
            // Ancho FIJO: era esto lo que movía medio panel al subir el volumen
            // — el texto crecía («+0,0 dB (100%)» → «+12,0 dB (400%)») y
            // empujaba a todo lo que tenía al lado.
            Width = UiMetrics.DbReadoutWideWidth,
            TextAlignment = TextAlignment.Center,
        };
        void CommitTypedVolume()
        {
            if (AudioLevels.ParseDb(label.Text) is not double gain)
            {
                // Ilegible: se repone lo que había. Un texto que no se entiende
                // no puede acabar en silencio, que es el peor sitio al que caer.
                label.Text = VolumeLabel(
                    AudioLevels.PositionToGain(slider.Value / posSteps));
                return;
            }
            slider.Value = AudioLevels.GainToPosition(gain) * posSteps;
        }
        label.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitTypedVolume();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                label.Text = VolumeLabel(
                    AudioLevels.PositionToGain(slider.Value / posSteps));
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        label.LostFocus += (_, _) => CommitTypedVolume();
        label.GotKeyboardFocus += (_, _) => label.SelectAll();
        // Referencias para la sincronía con el strip del mixer (SyncVolumeFromModel)
        _volSlider = slider;
        _volBox = label;
        // La lectura va ARRIBA, como en el strip del mixer. Se INSERTA delante
        // del fader (índice 1, tras el rótulo VOL) en vez de añadirse al final:
        // este strip tiene que leerse igual que el de la mesa, y ahí la cifra
        // está encima.
        faderCol.Children.Insert(1, new Border
        {
            Background = Theme.Solid(Theme.SunkenBg),
            BorderBrush = Theme.Solid(Theme.Divider),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = label,
        });

        // BOTONERA, la misma que en la mesa y sobre el MISMO estado: no son dos
        // juegos de botones que haya que mantener de acuerdo, es el mismo board.
        // Solo valen con el cue SONANDO —es cuando existe su strip—, así que
        // fuera de eso van deshabilitados en vez de mentir.
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _muteBtn = LevelButton("MUTE", Loc.T("Silenciar este cue", "Mute this cue"));
        // CUE, no solo: manda el cue a los CASCOS sin tocar la sala. El
        // solo-in-place enmudecía la sala para dejar oír uno, que en directo es
        // una trampa.
        _soloBtn = LevelButton("CUE", Loc.T(
            "Escuchar este cue por los cascos (no toca la sala)",
            "Listen to this cue on headphones (does not touch the room)"));
        btnRow.Children.Add(_muteBtn);
        btnRow.Children.Add(_soloBtn);
        faderCol.Children.Add(btnRow);

        _muteBtn.Click += (_, _) =>
        {
            if (_cue is null) return;
            MuteSolo?.ToggleMute(_cue.Id);
            MuteSoloChanged?.Invoke();
            RefreshMuteSolo();
        };
        _soloBtn.Click += (_, _) =>
        {
            if (_cue is null) return;
            CueRequested?.Invoke(_cue.Id);
            MuteSoloChanged?.Invoke();
            RefreshMuteSolo();
        };
        _loaders["__mute_solo"] = _ => RefreshMuteSolo();

        // EL MISMO STRIP QUE EN LA MESA, en su misma caja: mismo reparto, mismas
        // medidas y los mismos VU estéreo pegados al fader. Antes el VU vivía en
        // una columna aparte, con su propio rótulo y de otro ancho, y el mismo
        // cue se veía de dos maneras distintas según la pestaña.
        mixer.Children.Add(new Border
        {
            Background = Theme.Solid(Theme.ToolbarBg),
            BorderBrush = Theme.Solid(Theme.ButtonBgAlt),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Top,
            Child = faderCol,
        });

        // Columna de información y ajustes a la derecha del fader — COMPACTA:
        // esta columna comparte pestaña con el editor de EQ/comp, que es quien
        // necesita el ancho (curva + RTA)
        var infoCol = new StackPanel { Margin = new Thickness(12, 4, 0, 0) };
        mixer.Children.Add(infoCol);

        slider.ToolTip = Loc.T(
            "Volumen del cue: en vivo si está sonando.\nDoble clic: volver a 0 dB.",
            "Cue volume: live if it is playing.\nDouble-click: back to 0 dB.");
        _levelVu.ToolTip = Loc.T(
            "VU real de ESTE cue (cues de AUDIO y vídeos con AUDIO ESPEJO por el rack nativo).",
            "THIS cue's real VU (AUDIO cues and videos whose MIRRORED AUDIO goes through the native rack).");

        slider.ValueChanged += (_, e) =>
        {
            double v = AudioLevels.PositionToGain(e.NewValue / posSteps);
            label.Text = VolumeLabel(v);
            Commit("volume", v);   // en vivo: el controlador lo aplica al motor
        };
        // DOBLE CLIC → 0 dB, igual que en los faders del mixer. Es el mismo
        // volumen con la misma ley: que el gesto funcionara solo en un sitio
        // sería peor que no tenerlo, porque enseña a no fiarse de él.
        slider.MouseDoubleClick += (_, e) =>
        {
            slider.Value = AudioLevels.GainToPosition(1.0) * posSteps;
            e.Handled = true;
        };
        _loaders["volume"] = c =>
        {
            slider.Value = AudioLevels.GainToPosition(c.Volume) * posSteps;
            label.Text = VolumeLabel(c.Volume);
        };
        // El VU por cue existe donde hay rack nativo: cues de AUDIO y cues de
        // VÍDEO con AUDIO ESPEJO (motor HAP propio o ruta híbrida). El vídeo
        // cuyo audio sale por mpv no es medible → sin VU, igual que en la mesa
        // (allí es el withVu:false del strip). Se ocultan los DOS medidores,
        // que ahora viven pegados al fader y ya no en una columna propia.
        _conditional.Add((_levelVu, HasAudioAnalysis));
        _conditional.Add((_levelVuR, HasAudioAnalysis));

        // La salida por cue y la audición van en la columna de información
        panel = infoCol;

        // AQUÍ IBA «SALIDA DE ESTE CUE», un desplegable por cue para elegir
        // dispositivo. Fuera: el enrutado de salida se toca en UN solo sitio,
        // Ajustes → Audio. Tenerlo también aquí repartía la misma decisión
        // entre dos pantallas, y en directo eso significa no saber cuál manda.
        //
        // NO se ha tocado el campo del modelo (Cue.AudioOutput): un show
        // guardado con salida por cue sigue sonando por donde decía. Lo que
        // desaparece es la forma de cambiarlo desde aquí.

        // AQUÍ IBA el botón «Audición (cascos)». Lo hace el botón CUE del strip,
        // que es el mismo bus: dos botones para lo mismo, en la misma pestaña,
        // solo sirven para que te preguntes en qué se diferencian. Se queda el
        // CUE, que además dice si está sonando encendiéndose.
        return scroll;
    }

    /// <summary>Libera el mpv del visor de edición (al cerrar la app).</summary>
    public void ShutdownPreview() => _editPreview.Shutdown();

    private static string VolumeLabel(double v) =>
        v <= 0.0001 ? "−∞ dB" : $"{20.0 * Math.Log10(v):+0.0;-0.0} dB ({v * 100:0}%)";

    // ==================================================================
    // Framework de formularios (helpers)
    // ==================================================================

    private static TabItem Tab(string title, UIElement content) =>
        new() { Header = title, Content = content };

    private static (ScrollViewer Scroll, StackPanel Panel) Page()
    {
        var panel = new StackPanel { Margin = new Thickness(10, 6, 10, 10) };
        return (new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        }, panel);
    }

    /// <summary>Cabecera de sección: rótulo espaciado + filete que llega hasta
    /// el final del bloque.
    ///
    /// Antes era un rótulo suelto y el inspector se leía como una lista plana de
    /// campos: sin nada que marcase dónde acaba IDENTIDAD y empieza TIEMPO, hay
    /// que leerlo entero para encontrar un dato. El filete hace el trabajo de
    /// una caja agrupadora sin tener que meter cada sección en un contenedor —
    /// que habría obligado a reescribir los seis constructores de pestaña.</summary>
    private static void Header(StackPanel panel, string text)
    {
        var block = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
        block.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            // Espaciado entre letras: a 10 px y en negrita, un rótulo de sección
            // sin aire se lee como un borrón. Es lo que lo distingue de un
            // campo más.
            Foreground = Theme.Solid(Theme.TextMuted),
            Margin = new Thickness(0, 0, 0, 3),
        });
        block.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Solid(Theme.Divider),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        panel.Children.Add(block);
    }

    private static void Note(StackPanel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontStyle = FontStyles.Italic,
            Foreground = Theme.TextNoteBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 2),
        });

    private static StackPanel LabeledRow(StackPanel panel, string label)
    {
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label, Width = 185,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        panel.Children.Add(row);
        return row;
    }

    /// <summary>N columnas independientes dentro de una página del inspector.</summary>
    private static StackPanel[] Columns(StackPanel panel, int count)
    {
        var grid = new Grid();
        var cols = new StackPanel[count];
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cols[i] = new StackPanel();
            Grid.SetColumn(cols[i], i * 2);
            grid.Children.Add(cols[i]);
        }
        panel.Children.Add(grid);
        return cols;
    }

    private static StackPanel ChkRow(StackPanel panel)
    {
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        panel.Children.Add(row);
        return row;
    }

    /// <summary>Fila con visibilidad condicionada al tipo de cue.</summary>
    private StackPanel Conditional(StackPanel panel, Func<Cue, bool> visibleIf)
    {
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(row);
        _conditional.Add((row, visibleIf));
        return row;
    }

    private TextBox Txt(StackPanel panel, string key, string label, Func<Cue, string> read)
    {
        var row = LabeledRow(panel, label);
        var tb = MakeTextBox(180);
        WireTextCommit(tb, key, () => tb.Text);
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c);
        return tb;
    }

    private void TxtMultiline(StackPanel panel, string key, string label, Func<Cue, string> read)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Theme.TextTertiaryBrush,
            Margin = new Thickness(0, 6, 0, 2),
        });
        var tb = MakeTextBox(0);
        tb.AcceptsReturn = true;
        tb.TextWrapping = TextWrapping.Wrap;
        tb.MinHeight = 44;
        tb.HorizontalAlignment = HorizontalAlignment.Stretch;
        tb.LostFocus += (_, _) => Commit(key, tb.Text);
        panel.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c);
    }

    private void Num(StackPanel panel, string key, string label, Func<Cue, double> read)
    {
        var row = LabeledRow(panel, label);
        var tb = MakeTextBox(90);
        WireTextCommit(tb, key, () =>
            double.TryParse(tb.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? v : (object?)null);
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c).ToString("0.###",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Campo de tiempo: muestra m:ss.d si ≥60 s y acepta ambos formatos
    /// ("90", "1:30", "1:30.5") — equivalente al TimeEdit de la versión Python.</summary>
    private void TimeF(StackPanel panel, string key, string label, Func<Cue, double> read)
    {
        var row = LabeledRow(panel, label);
        var tb = MakeTextBox(90);
        WireTextCommit(tb, key, () => ParseTime(tb.Text));
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = FormatTime(read(c));
    }

    private static string FormatTime(double s) =>
        s >= 60.0
            ? $"{(int)(s / 60)}:{s % 60:00.0##}".TrimEnd('0').TrimEnd('.')
            : s.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static object? ParseTime(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int m) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double sec))
                return m * 60 + sec;
            return null;
        }
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : null;
    }

    private void Int(StackPanel panel, string key, string label, Func<Cue, int> read)
    {
        var row = LabeledRow(panel, label);
        var tb = MakeTextBox(70);
        WireTextCommit(tb, key, () =>
            int.TryParse(tb.Text, out int v) ? v : (object?)null);
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c).ToString();
    }

    private void Cmb(StackPanel panel, string key, string label, Func<Cue, string> read,
                     params (string Value, string Label)[] items)
    {
        var row = LabeledRow(panel, label);
        CmbInto(row, key, read, items);
    }

    private void CmbInline(StackPanel row, string key, string label, Func<Cue, string> read,
                           params (string Value, string Label)[] items)
    {
        row.Children.Add(new TextBlock
        {
            Text = label, Width = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        CmbInto(row, key, read, items);
    }

    private void CmbInto(StackPanel row, string key, Func<Cue, string> read,
                         (string Value, string Label)[] items)
    {
        var combo = new ComboBox { MinWidth = 230 };
        foreach (var (value, text) in items)
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        combo.SelectionChanged += (_, _) =>
        {
            if ((combo.SelectedItem as ComboBoxItem)?.Tag is string v)
                Commit(key, v);
        };
        row.Children.Add(combo);
        _loaders[key] = c =>
        {
            string current = read(c);
            foreach (ComboBoxItem item in combo.Items)
                if ((string)item.Tag == current)
                {
                    combo.SelectedItem = item;
                    return;
                }
        };
    }

    private void Chk(StackPanel row, string key, string label, Func<Cue, bool> read)
    {
        var cb = new CheckBox
        {
            Content = label,
            Foreground = Theme.TextPrimaryBrush,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cb.Checked += (_, _) => Commit(key, true);
        cb.Unchecked += (_, _) => Commit(key, false);
        row.Children.Add(cb);
        _loaders[key] = c => cb.IsChecked = read(c);
    }

    private void ColorSwatches(StackPanel panel)
    {
        var row = LabeledRow(panel, Loc.T("Color:", "Color:"));
        foreach (var (name, hex) in CueConstants.CueColors)
        {
            var btn = new Button
            {
                Width = 26, Height = 20, Margin = new Thickness(2, 0, 2, 0),
                Background = hex.Length > 0
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
                    : Brushes.Transparent,
                BorderBrush = Theme.TextFaintBrush,
                Content = hex.Length == 0 ? Icons.NoColor : "",
                Foreground = Theme.TextNoteBrush,
                Tag = name,
            };
            btn.Click += (_, _) => Commit("color", name);
            row.Children.Add(btn);
        }
        _loaders["color"] = _ => { };   // sin estado visual de selección (5b)
    }

    private void FileRow(StackPanel panel)
    {
        var row = Conditional(panel,
            c => c.IsMedia() || c.Type == CueType.Script);
        row.Children.Add(new TextBlock
        {
            Text = Loc.T("Archivo:", "File:"), Width = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        var tb = MakeTextBox(300);
        var browse = new Button { Content = Icons.Browse, Margin = new Thickness(4, 0, 0, 0) };
        WireTextCommit(tb, "file_path", () => tb.Text);
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            if (dlg.ShowDialog() == true)
            {
                tb.Text = dlg.FileName;
                Commit("file_path", dlg.FileName);
            }
        };
        row.Children.Add(tb);
        row.Children.Add(browse);
        _loaders["file_path"] = c => tb.Text = c.FilePath;
    }

    private void TargetCombo(StackPanel row)
    {
        row.Children.Add(new TextBlock
        {
            Text = Loc.T("Cue objetivo:", "Target cue:"), Width = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        var combo = new ComboBox { MinWidth = 260 };
        combo.SelectionChanged += (_, _) =>
        {
            if ((combo.SelectedItem as ComboBoxItem)?.Tag is int id)
                Commit("target_id", id == -1 ? null : id);
        };
        row.Children.Add(combo);
        _loaders["target_id"] = c =>
        {
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = "(sin objetivo)", Tag = -1 });
            if (_allCues is not null)
                foreach (var other in _allCues())
                    if (other.Id != c.Id)
                        combo.Items.Add(new ComboBoxItem
                        {
                            Content = $"{other.DisplayNumber} · {other.Name}",
                            Tag = other.Id,
                        });
            int wanted = c.TargetId ?? -1;
            foreach (ComboBoxItem item in combo.Items)
                if ((int)item.Tag == wanted)
                {
                    combo.SelectedItem = item;
                    return;
                }
            combo.SelectedIndex = 0;
        };
    }

    private static TextBox MakeTextBox(double width)
    {
        var tb = new TextBox
        {
            Background = Theme.ControlBgBrush,
            Foreground = Theme.TextPrimaryBrush,
            BorderBrush = Theme.Solid(Theme.BorderControl),
            Padding = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (width > 0) tb.Width = width;
        return tb;
    }

    /// <summary>Commit al perder foco o pulsar Enter (si el valor parsea).</summary>
    private void WireTextCommit(TextBox tb, string key, Func<object?> parse)
    {
        void DoCommit()
        {
            object? v = parse();
            if (v is not null)
                Commit(key, v);
        }
        tb.LostFocus += (_, _) => DoCommit();
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                DoCommit();
                e.Handled = true;
            }
        };
    }
}
