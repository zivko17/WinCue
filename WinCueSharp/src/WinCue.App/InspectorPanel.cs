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
    private readonly StackPanel _previewHost = new()
    {
        Width = 280,
        Margin = new Thickness(8, 4, 8, 8),
        Visibility = Visibility.Collapsed,
    };

    public InspectorPanel()
    {
        // Marco AZUL = canvas de salida: mpv recorta exactamente en ese borde,
        // así que lo que quede fuera del marco es lo que se pierde en la salida.
        _editPreview.BorderBrush = Theme.Solid(Theme.Blue);
        _editPreview.BorderThickness = new Thickness(2);
        _editPreview.Height = 176;   // vídeo ≈ 16:9 dentro del panel de 280 px

        _previewHost.Children.Add(_editPreview);
        _previewHost.Children.Add(new TextBlock
        {
            Text = Loc.T("El marco azul es el CANVAS de salida: lo que quede fuera se recorta. Arrastra el trim para ver el fotograma de corte.",
                   "The blue frame is the output CANVAS: whatever falls outside is cropped. Drag the trim to see the cut frame."),
            FontSize = 10,
            Foreground = Theme.TextNoteBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

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
    private void RefreshPreviewVisibility()
    {
        // En GEOMETRÍA ya no acompaña: el fotograma vive DENTRO del lienzo, que
        // es donde se encuadra. Tenerlo además al lado sería la misma imagen dos
        // veces robando 280 px al lienzo, que es justo lo que interesa grande.
        bool usefulHere = ReferenceEquals(_tabs.SelectedItem, _timeLoopsTab);
        bool hasVideo = _cue is { Type: CueType.Video } c && c.FilePath.Length > 0;

        _previewHost.Visibility = usefulHere && hasVideo
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        // Sin scroll: campos arriba y la barra de trim ESTIRADA al alto restante
        var root = new DockPanel { Margin = new Thickness(10, 4, 10, 10) };

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
        _wave.MinHeight = 64;   // sin altura fija: llena el resto de la pestaña
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

    // VU vertical del cue seleccionado (recibe niveles reales del mixer)
    private readonly VuMeterControl _levelVu = new(vertical: true)
    {
        Width = 16,
        Height = 170,
    };

    /// <summary>Nivel RMS de un cue (solo se pinta si es el seleccionado).</summary>
    public void UpdateCueLevel(int cueId, float rms)
    {
        if (_cue?.Id == cueId)
            _levelVu.SetLevel(rms);
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
            MaxWidth = 320,   // columna compacta: el ancho es para la EQ/comp
        };
        var host = new DockPanel();
        DockPanel.SetDock(levelsHost, Dock.Left);
        host.Children.Add(levelsHost);
        host.Children.Add(_eqEditor);
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
        var slider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = 100,
            Width = UiMetrics.FaderWidth, Height = UiMetrics.FaderHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        faderCol.Children.Add(slider);

        // La lectura va DEBAJO del fader, como en el mixer y en el GAIN del EQ.
        // Antes vivía en la columna de la derecha: el mismo dato en tres sitios
        // distintos según la pestaña obliga a buscarlo cada vez.
        var label = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = Theme.GoBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        faderCol.Children.Add(label);
        mixer.Children.Add(faderCol);

        var vuCol = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        vuCol.Children.Add(new TextBlock
        {
            Text = "VU",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextDimBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _levelVu.Margin = new Thickness(0, 4, 0, 0);
        vuCol.Children.Add(_levelVu);
        mixer.Children.Add(vuCol);

        // Columna de información y ajustes a la derecha del fader — COMPACTA:
        // esta columna comparte pestaña con el editor de EQ/comp, que es quien
        // necesita el ancho (curva + RTA)
        var infoCol = new StackPanel { Margin = new Thickness(12, 4, 0, 0) };
        mixer.Children.Add(infoCol);

        slider.ToolTip = Loc.T("Volumen del cue: en vivo si está sonando.",
                               "Cue volume: live if it is playing.");
        _levelVu.ToolTip = Loc.T(
            "VU real de ESTE cue (cues de AUDIO y vídeos con AUDIO ESPEJO por el rack nativo).",
            "THIS cue's real VU (AUDIO cues and videos whose MIRRORED AUDIO goes through the native rack).");

        slider.ValueChanged += (_, e) =>
        {
            double v = e.NewValue / 100.0;
            label.Text = VolumeLabel(v);
            Commit("volume", v);   // en vivo: el controlador lo aplica al motor
        };
        _loaders["volume"] = c =>
        {
            slider.Value = c.Volume * 100.0;
            label.Text = VolumeLabel(c.Volume);
        };
        // El VU por cue existe donde hay rack nativo: cues de AUDIO y cues de
        // VÍDEO con AUDIO ESPEJO (motor HAP propio o ruta híbrida). El vídeo
        // cuyo audio sale por mpv no es medible → sin columna VU.
        _conditional.Add((vuCol, HasAudioAnalysis));

        // La salida por cue y la audición van en la columna de información
        panel = infoCol;

        // ── Sección SOLO AUDIO: salida por cue + audición ───────────────
        var audioOnly = new StackPanel();
        panel.Children.Add(audioOnly);
        _conditional.Add((audioOnly, c => c.Type == CueType.Audio));

        Header(audioOnly, Loc.T("SALIDA DE ESTE CUE", "THIS CUE'S OUTPUT"));
        var outCombo = new ComboBox
        {
            MinWidth = 170, MaxWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 2),
        };
        outCombo.SelectionChanged += (_, _) =>
        {
            if ((outCombo.SelectedItem as ComboBoxItem)?.Tag is string dev)
                Commit("audio_output", dev);
        };
        audioOnly.Children.Add(outCombo);
        _loaders["audio_output"] = c =>
        {
            outCombo.Items.Clear();
            outCombo.Items.Add(new ComboBoxItem
            { Content = "(salida principal)", Tag = "" });
            try
            {
                foreach (var dev in WinCue.Audio.AudioEngine.ListDevices())
                    outCombo.Items.Add(new ComboBoxItem { Content = dev.Name, Tag = dev.Name });
                foreach (string asio in WinCue.Audio.AudioEngine.ListAsioDrivers())
                    outCombo.Items.Add(new ComboBoxItem { Content = $"[ASIO] {asio}", Tag = asio });
            }
            catch (Exception) { }
            foreach (ComboBoxItem item in outCombo.Items)
                if ((string)item.Tag == c.AudioOutput)
                {
                    outCombo.SelectedItem = item;
                    return;
                }
            outCombo.SelectedIndex = 0;
        };

        // ── Audición local ──────────────────────────────────────────────
        var audBtn = new Button
        {
            Content = $"{Icons.Headphones} {Loc.T("Audición (cascos)", "Audition (headphones)")}",
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 5, 12, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        audBtn.Click += (_, _) => AuditionRequested?.Invoke();
        audBtn.ToolTip = Loc.T(
            "Por el dispositivo de audición, sin pasar por la sala. Otra pulsación para parar.",
            "On the audition device, without going to the room. Press again to stop.");
        audioOnly.Children.Add(audBtn);

        return scroll;
    }

    /// <summary>El botón de audición fue pulsado (la ventana llama al controlador).</summary>
    public event Action? AuditionRequested;

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
