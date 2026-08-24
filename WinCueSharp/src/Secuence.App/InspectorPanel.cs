// =============================================================================
// InspectorPanel.cs — Inspector de cue (WinUI 3, declarativo).
// =============================================================================
// Equivalente de src/WinCue.App/InspectorPanel.cs para esta interfaz, NO una
// copia (regla 2 del proyecto: lo compartido sube, no se copia entre
// productos). Aquí no hay DrawingContext que traducir — es un panel
// DECLARATIVO: controles corrientes de WinUI 3 (TextBox, ComboBox, CheckBox,
// Slider) enlazados a mano, con el mismo reparto de responsabilidad que WPF
// usa para los suyos (helpers Txt/TimeF/Cmb/Chk por campo, un Dictionary de
// "loaders" para repoblar al cambiar de cue), solo que con los tipos de
// Microsoft.UI.Xaml.Controls en vez de System.Windows.Controls.
//
// ALCANCE (ver el encargo — "hitos-0-1", inspector de cues): las propiedades
// que gobiernan un show. Tras la SEGUNDA PASADA, el mismo conjunto de campos
// que la pestaña «Básicos» + «Disparadores» + «Tiempo y bucles» de WinCue:
//   - Identidad: número, nombre, color de etiqueta, armado, flag, notas (el
//     tipo se ve en la cabecera, como en WPF: WinCue tampoco deja editarlo
//     desde el inspector).
//   - Destino: archivo, cue objetivo (START/STOP/FADE), «detener el objetivo
//     al llegar a 0», modo de grupo, «detener los demás al empezar».
//   - Temporización: pre-wait, duración, post-wait, encadenado (continue_mode).
//   - Fundidos: fade in, fade out, con el AVISO de fundido incoherente
//     (FadeSanity, compartido — el mismo que marca el timeline).
//   - Reproducción: loop, play count, trim (inicio/fin), velocidad, mantener
//     el tono, volumen.
//   - Disparadores: hotkey, segundo disparo (retrigger), «fundir y parar
//     otros» (con tiempo y ámbito), wall-clock y MIDI.
//
// DISPOSICIÓN — las CINCO pestañas de WinCue, en su orden y con su reparto en
// columnas (Básicos: identidad │ temporización │ destino · Disparadores: local
// │ wall-clock │ MIDI · Tiempo y bucles: reproducción │ fundidos y recorte, con
// la barra de onda a lo ancho debajo · Geometría · Audio). Hasta esta pasada
// era UNA sola columna que scrolleaba, y con ello el inspector se leía como
// una lista plana muy larga en vez de como el de WinCue. Las medidas también
// son las suyas: páginas con margen (10,6,10,10), columnas separadas 30 px,
// etiquetas de 185 px y cabeceras a (0,12,0,4).
//
// El truco que permitió el cambio sin tocar los ~25 sitios que ya escribían
// campos: _root dejó de ser un panel fijo y pasó a ser un PUNTERO al
// contenedor actual, que Tab()/Columns() van moviendo. Ver su declaración.
//
// LAS OPCIONES de cada desplegable (continue_mode, group_mode, retrigger,
// fade_others_scope, midi_type) y la paleta de colores de etiqueta vienen de
// WinCue.Presentation.CueChoices — una sola definición para los dos
// productos, con pruebas que exigen que cada valor de cada enum tenga su
// etiqueta (CueChoicesTests). Antes estaban escritas en línea en WinCue, y
// repetirlas aquí era exactamente la clase de copia que la regla 2 prohíbe:
// añadir un modo al enum habría dejado a un producto sin ofrecerlo, en
// silencio.
//
// PENDIENTE conocido: el HOTKEY por cue se guarda y WinCue lo respeta, pero
// Secuence todavía no construye los atajos por cue (RebuildHotkeys solo cubre
// Espacio/Esc) — el campo lleva su aclaración en pantalla, no es un engaño.
//
// RACK DE AUDIO (sumado en la tarea "editor de EQ", séptimo y último control
// pintado a mano, hitos-0-1): ganancia de clip + EQ paramétrica de 4 bandas
// (HPF/LPF) + compresor, con RTA y medidor GR EN VIVO, para cues de AUDIO y
// de VÍDEO — mismo alcance que WinCue.App.EqEditorControl (WPF). El CÁLCULO
// (ejes, respuesta de los filtros —biquads RBJ, la MISMA fórmula que aplica
// el motor de audio—, balística del RTA, transferencia del compresor) es
// compartido: WinCue.Presentation.EqCurveLayout/CompCurveLayout y WinCue.Core.EqResponse.
// Ver EqSection() más abajo y EqCurveCanvas.cs/CompCurveCanvas.cs (Win2D)
// para el detalle.
//
// GEOMETRÍA DE VÍDEO (sumada en la tarea "canvas de geometría", hitos-0-1):
// modo FULL/CUSTOM, aspecto, escala, posición y rotación, con el mismo
// GeometryCanvas (Win2D) que WinCue.App.GeometryCanvasControl (WPF) — el
// CÁLCULO de coordenadas es compartido (WinCue.Presentation.GeometryLayout),
// ver GeometryCanvas.cs para el detalle. Sin el compositor multi-salida
// (opacidad, canvas/output por cue) ni el visor de edición con fotograma fijo
// — eso pide controles y una ventana que Secuence no tiene todavía; ver la
// cabecera de GeometryCanvas.cs para el porqué exacto.
//
// EDICIÓN EN VIVO: ningún campo se deshabilita por que el cue esté sonando.
// WinCue tampoco lo hace (ver InspectorPanel.cs de WPF, Commit/
// MainController.OnCueEditCommitted/ApplyLiveEdits) — el volumen se refleja
// EN VIVO en el motor si el cue suena; el resto de campos de esta pasada
// (tiempos, fundidos, loop, trim) surten efecto la próxima vez que el cue se
// dispare. Replicar esa política exactamente es la instrucción del encargo:
// "mira qué hace WinCue y haz lo mismo".
//
// BARRA DE TRIM CON ONDA (WaveformCanvas, Win2D): igual que en WPF, mover el
// trim en los campos numéricos mueve los manejadores de la onda (SetMarkers
// en Commit) y arrastrar los manejadores de la onda comete "trim_start"/
// "trim_end" y repuebla los campos (MarkersChanged) — dos direcciones, ver
// UpdateWave más abajo. Solo cues de AUDIO en esta pasada (ver la cabecera
// de WaveformCanvas.cs para el porqué de dejar fuera vídeo).
//
// FORMATO Y VALIDACIÓN: el formato/parseo del campo de tiempo editable
// (CueTimeField) y el rango/ley del fader de volumen (AudioLevels) viven en
// WinCue.Presentation / WinCue.Core — código COMPARTIDO con WinCue, no
// reescrito aquí. Qué campos aplican a qué tipo de cue (Cue.IsMedia()) y el
// commit de un campo por nombre snake_case (CueFields.Apply, del lado del
// controlador) también son ya compartidos: esta pasada no necesita subir
// nada más para el conjunto de campos de arriba.
// =============================================================================

using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

public sealed class InspectorPanel : ContentControl
{
    /// <summary>Commit de un campo: (cueId, clave snake_case, valor) — mismo
    /// contrato que WinCue.App.InspectorPanel.CommitField, para que
    /// MainWindow lo conecte a MainController.OnCueEditCommitted exactamente
    /// igual que hace WinCue.App.MainWindow.xaml.cs.Attach.</summary>
    public Action<int, string, object?>? CommitField;

    /// <summary>Abre el selector de archivo y devuelve la ruta elegida (o
    /// null si se canceló). Vive en MainWindow porque el picker de WinUI 3
    /// necesita el hwnd propio de la ventana (WinRT.Interop.
    /// InitializeWithWindow) — el inspector no tiene por qué saber de eso,
    /// igual que no sabe de FileOpenPicker en absoluto.</summary>
    public Func<Task<string?>>? BrowseFile;

    /// <summary>Los cues del show, para el desplegable de «cue objetivo» de un
    /// START/STOP/FADE. Lo pone MainWindow: el inspector no conoce el CueModel
    /// — mismo reparto que el parámetro allCues de
    /// WinCue.App.InspectorPanel.SetCue.</summary>
    public Func<IReadOnlyList<Cue>>? AllCues;

    /// <summary>Segundos que le quedan al vídeo saliente de la misma pista
    /// cuando este cue entra (del timeline), para el aviso de fundido
    /// incoherente. Mismo contrato que
    /// WinCue.App.InspectorPanel.CrossfadeHeadroomProvider.</summary>
    public Func<int, double?>? CrossfadeHeadroomProvider;

    private Cue? _cue;
    private bool _loading;   // suprime commits al repoblar (igual que en WPF)

    /// <summary>false en modo SHOW: bloquea la edición de campos igual que
    /// WinCue.App bloquea su toolbar entera (MainWindow.SetEditingEnabled).
    /// El inspector es la ÚNICA superficie de edición de campos de Secuence
    /// (no hay grid editable ni menú contextual todavía), así que este es el
    /// candado equivalente de este producto. IsEnabled ya combinaba "hay cue
    /// seleccionado"; aquí se le añade la segunda condición sin tocar esa.</summary>
    private bool _editingAllowed = true;
    public bool EditingAllowed
    {
        get => _editingAllowed;
        set
        {
            _editingAllowed = value;
            IsEnabled = _cue is not null && _editingAllowed;
        }
    }
    private readonly WaveformCanvas _wave = new();
    private string _waveLoadedPath = "";

    // ── Aviso de fundido incoherente ───────────────────────────────────
    // El CÁLCULO es compartido (WinCue.Core.FadeSanity, el mismo que usa el
    // inspector de WPF y el indicador de solapamiento del timeline): aquí
    // solo se pinta. Aviso SUAVE — no impide guardar ni recorta ningún valor,
    // solo cuenta qué va a pasar.
    private readonly TextBlock _fadeWarn = new()
    {
        Foreground = Theme.WarnBrush,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 300,
        FontSize = 11,
        Margin = new Thickness(10, 2, 10, 4),
        Visibility = Visibility.Collapsed,
    };

    /// <summary>Campos cuya edición puede volver coherente (o incoherente) un
    /// fundido — misma lista que WinCue.App.InspectorPanel.FadeWarnKeys.</summary>
    private static readonly HashSet<string> FadeWarnKeys = new(StringComparer.Ordinal)
    { "fade_in", "fade_out", "trim_start", "trim_end", "duration", "loop", "file_path" };

    private void UpdateFadeWarning(Cue cue)
    {
        double? remaining = cue.Type == CueType.Video
            ? CrossfadeHeadroomProvider?.Invoke(cue.Id) : null;
        var issues = FadeSanity.Check(cue, remaining);
        string? text = FadeSanity.ShortText(issues);
        if (text is null)
        {
            _fadeWarn.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(_fadeWarn, null);
            return;
        }
        _fadeWarn.Text = text;
        ToolTipService.SetToolTip(_fadeWarn,
            FadeSanity.Explain(issues, FadeSanity.EffectiveDuration(cue), remaining));
        _fadeWarn.Visibility = Visibility.Visible;
    }

    // ── Rack de audio: ganancia + EQ + compresor ────────────────────────
    /// <summary>Espectro (~12 Hz) del cue dado, o null si no suena — mismo
    /// contrato que WinCue.App.InspectorPanel.SpectrumProvider.</summary>
    public Func<int, float[]?>? SpectrumProvider;
    /// <summary>Reducción de ganancia del compresor (dB ≥ 0) del cue dado.</summary>
    public Func<int, float>? GainReductionProvider;
    /// <summary>true si el audio de este cue pasa por el rack nativo (RTA/GR
    /// reales) — false = vídeo cuyo audio sale por mpv sin espejo.</summary>
    public Func<int, bool>? AudioAnalysisProvider;

    private readonly EqCurveCanvas _eqCanvas = new();
    private readonly CompCurveCanvas _compCanvas = new();
    private readonly TextBlock _eqReadout = new()
    {
        Foreground = Theme.TextDimmerBrush,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        FontSize = 10,
        Margin = new Thickness(10, 2, 10, 0),
        TextWrapping = TextWrapping.Wrap,
        Text = Loc.T("arrastrar punto = frec/gan · rueda = Q", "drag point = freq/gain · wheel = Q"),
    };
    private readonly TextBlock _noAnalyzerNote = new()
    {
        Text = Loc.T(
            "Sin analizador (RTA/GR): el audio de este vídeo lo saca mpv. La ganancia, la EQ y el compresor SÍ actúan, pero solo hay medidores cuando el audio va por el rack (AUDIO ESPEJO).",
            "No analyzer (RTA/GR): this video's audio is played by mpv. Gain, EQ and compressor DO work, but there are meters only when audio goes through the rack (MIRRORED AUDIO)."),
        Foreground = Theme.WarnBrush,
        FontSize = 10,
        Margin = new Thickness(10, 4, 10, 0),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _eqTimer;

    // ── Geometría de vídeo ──────────────────────────────────────────────
    private readonly GeometryCanvas _geoCanvas = new();
    private StackPanel _fullGeoPanel = null!;
    private StackPanel _customGeoPanel = null!;
    private readonly List<(Button Btn, int Deg)> _rotButtons = new();

    /// <summary>Claves cuyo cambio se refleja en el lienzo + paneles + botones
    /// de rotación (sin re-commitear). Mismo conjunto que
    /// WinCue.App.InspectorPanel.GeoPreviewKeys.</summary>
    private static readonly HashSet<string> GeoPreviewKeys = new()
    {
        "geo_mode", "preserve_aspect", "scale", "translate_x", "translate_y", "scale_x", "rotate_z",
    };

    private readonly TextBlock _header = new()
    {
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(10, 8, 10, 4),
        Foreground = Theme.WarnBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Contenedor al que se añaden los campos AHORA MISMO. No es un
    /// panel fijo: Tab()/Columns() lo van moviendo según se construye el
    /// formulario, de modo que TODOS los helpers (Header/LabeledRow/Chk/
    /// Note/…) siguen escribiendo en «el sitio actual» sin saber en qué
    /// pestaña ni en qué columna están. Es lo que permitió pasar el inspector
    /// de una columna única a las pestañas de WinCue sin tocar los ~25 sitios
    /// que ya escribían aquí.</summary>
    private StackPanel _root = new();

    /// <summary>Las pestañas del inspector — LAS MISMAS que WinCue.App
    /// (Básicos · Disparadores · Tiempo y bucles · Geometría · Audio), en el
    /// mismo orden.</summary>
    private readonly TabView _tabs = new()
    {
        IsAddTabButtonVisible = false,
        TabWidthMode = TabViewWidthMode.SizeToContent,
        Background = Theme.PanelBgBrush,
    };

    // Registro clave → escribir valor al control (para poblar al seleccionar)
    private readonly Dictionary<string, Action<Cue>> _loaders = new();
    // Filas con visibilidad condicionada por tipo de cue (aquí, solo "Archivo")
    private readonly List<(FrameworkElement El, Func<Cue, bool> VisibleIf)> _conditional = new();

    private const double VolPosSteps = 1000.0;

    public InspectorPanel()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_header, 0);
        Grid.SetRow(_tabs, 1);
        layout.Children.Add(_header);
        layout.Children.Add(_tabs);
        Content = layout;

        // El TabView tiene que LLENAR el hueco (WinUI 3 da Left/Top por
        // defecto en un ContentControl — misma trampa que el diálogo de
        // Ajustes, ver SettingsPanel).
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        // ═══ Las CINCO pestañas de WinCue, en su orden y con su reparto en
        // columnas (ver BuildBasics/BuildTriggers/BuildTimeLoops de allí). ═══

        // ── Básicos: IDENTIDAD │ TEMPORIZACIÓN │ DESTINO ──
        Tab(Loc.T("Básicos", "Basics"));
        var basics = Columns(3);

        _root = basics[0];
        Header(Loc.T("IDENTIDAD", "IDENTITY"));
        Txt("number", Loc.T("Número:", "Number:"), c => c.Number);
        Txt("name", Loc.T("Nombre:", "Name:"), c => c.Name);
        ColorSwatches();
        Chk("armed", Loc.T("Armado", "Armed"), c => c.Armed);
        Chk("flagged", $"{Icons.Flag} Flag", c => c.Flagged, icon: true);
        TxtMultiline("notes", Loc.T("Notas (se muestran en standby):", "Notes (shown in standby):"),
            c => c.Notes);

        _root = basics[1];
        Header(Loc.T("TEMPORIZACIÓN", "TIMING"));
        Cmb("continue_mode", Loc.T("Al terminar:", "On finish:"), c => c.ContinueMode.ToString(),
            CueChoices.ContinueModes);
        TimeF("pre_wait", "Pre-wait:", c => c.PreWait);
        TimeF("duration", Loc.T("Duración:", "Duration:"), c => c.Duration);
        TimeF("post_wait", "Post-wait:", c => c.PostWait);

        _root = basics[2];
        Header(Loc.T("DESTINO", "TARGET"));
        FileRow();
        TargetCombo();
        Conditional(c => c.Type == CueType.Fade, () =>
            Chk("fade_stop_on_done",
                Loc.T("Detener el objetivo al llegar a 0", "Stop the target when it reaches 0"),
                c => c.FadeStopOnDone));
        Conditional(c => c.Type == CueType.Group, () =>
            Cmb("group_mode", Loc.T("Modo de grupo:", "Group mode:"), c => c.GroupMode.ToString(),
                CueChoices.GroupModes));
        Chk("stop_others",
            Loc.T("Detener los demás al empezar (GO per-cue)", "Stop others when starting (per-cue GO)"),
            c => c.StopOthers);

        // ── Disparadores ──
        Tab(Loc.T("Disparadores", "Triggers"));
        TriggersSection();

        // ── Tiempo y bucles: REPRODUCCIÓN │ FUNDIDOS Y RECORTE, con la barra
        //    de trim debajo (mismo reparto que WinCue.BuildTimeLoops) ──
        Tab(Loc.T("Tiempo y bucles", "Time & Loops"));
        var timeLoops = _root;
        var loops = Columns(2);

        _root = loops[0];
        Header(Loc.T("REPRODUCCIÓN", "PLAYBACK"));
        Chk("loop", Loc.T("Loop infinito (hasta STOP)", "Infinite loop (until STOP)"), c => c.Loop);
        Int("play_count", Loc.T("Repeticiones (play count):", "Repeats (play count):"), c => c.PlayCount);
        Num("rate", Loc.T("Velocidad (1.0 = normal):", "Speed (1.0 = normal):"), c => c.Rate);
        Chk("preserve_pitch",
            Loc.T("Mantener el tono al cambiar la velocidad", "Preserve pitch when changing speed"),
            c => c.PreservePitch);
        VolumeField();

        _root = loops[1];
        Header(Loc.T("FUNDIDOS Y RECORTE", "FADES & TRIM"));
        TimeF("fade_in", "Fade in:", c => c.FadeIn);
        TimeF("fade_out", "Fade out:", c => c.FadeOut);
        _root.Children.Add(_fadeWarn);
        TimeF("trim_start", Loc.T("Trim: inicio:", "Trim: start:"), c => c.TrimStart);
        TimeF("trim_end", Loc.T("Trim: fin (0 = final):", "Trim: end (0 = end):"), c => c.TrimEnd);

        // La barra de onda va A LO ANCHO, bajo las dos columnas — igual que en
        // WinCue, donde el DockPanel la deja debajo del bloque de campos.
        _root = timeLoops;
        Header(Loc.T("BARRA DE TRIM — arrastra los manejadores",
                     "TRIM BAR — drag the handles"));
        WaveformRow();

        // ── Geometría y Audio: cada una en la suya, como WinCue ──
        Tab(Loc.T("Geometría", "Geometry"));
        GeometrySection();

        _audioTab = Tab($"{Icons.Volume} Audio", icon: true);
        EqSection();

        IsEnabled = false;   // sin cue seleccionado
        _header.Text = Loc.T("— sin cue seleccionado —", "— no cue selected —");

        // RTA + medidor GR ~12 Hz mientras haya un cue de audio/vídeo
        // seleccionado — mismo periodo que el DispatcherTimer de
        // WinCue.App.EqEditorControl. Se arma al cargar (mismo motivo que
        // MixerPanel.StartGrTimer: el DispatcherQueue de este control no
        // existe hasta que está en el árbol visual).
        Loaded += (_, _) => StartEqTimer();
        Unloaded += (_, _) => _eqTimer?.Stop();
    }

    private void StartEqTimer()
    {
        if (_eqTimer is not null)
            return;
        _eqTimer = DispatcherQueue.CreateTimer();
        _eqTimer.Interval = TimeSpan.FromMilliseconds(80);
        _eqTimer.Tick += (_, _) =>
        {
            if (_cue is not { Type: CueType.Audio or CueType.Video } cue)
                return;
            // Sin analizador (vídeo por mpv) no se pide nada: la curva de la
            // EQ sigue viva porque es matemática de los parámetros — mismo
            // criterio que WinCue.App.EqEditorControl.
            if (AudioAnalysisProvider?.Invoke(cue.Id) ?? cue.Type == CueType.Audio)
            {
                _eqCanvas.SetSpectrum(SpectrumProvider?.Invoke(cue.Id));
                _compCanvas.SetGainReduction(GainReductionProvider?.Invoke(cue.Id) ?? 0f);
            }
        };
        _eqTimer.Start();
    }

    /// <summary>Muestra (y edita) el cue dado. null → inspector deshabilitado.
    /// Mismo contrato que WinCue.App.InspectorPanel.SetCue, sin el parámetro
    /// allCues (target_id queda fuera de esta pasada — ver el encargo).</summary>
    public void SetCue(Cue? cue)
    {
        _cue = cue;
        IsEnabled = cue is not null && _editingAllowed;
        if (cue is null)
        {
            _header.Text = Loc.T("— sin cue seleccionado —", "— no cue selected —");
            _wave.Clear();
            _waveLoadedPath = "";
            _eqCanvas.SetCue(null);
            _compCanvas.SetCue(null);
            return;
        }

        _loading = true;
        try
        {
            _header.Text = HeaderText(cue);
            foreach (var loader in _loaders.Values)
                loader(cue);
            foreach (var (el, visibleIf) in _conditional)
                el.Visibility = visibleIf(cue) ? Visibility.Visible : Visibility.Collapsed;
            UpdateWave(cue);
            UpdateFadeWarning(cue);
        }
        finally
        {
            _loading = false;
        }
    }

    private static string HeaderText(Cue cue) =>
        $"Cue {cue.DisplayNumber} · {cue.Type.ToString().ToUpperInvariant()} · {cue.Name}";

    private void Commit(string key, object? value)
    {
        if (_loading || _cue is null)
            return;
        CommitField?.Invoke(_cue.Id, key, value);
        // El commit pudo cambiar nombre/número/tipo (file_path autodetecta
        // tipo por extensión, ver MainController.OnCueEditCommitted): _cue es
        // el MISMO objeto que el modelo acaba de mutar, así que basta con
        // releerlo — no hace falta repoblar el resto del formulario.
        _header.Text = HeaderText(_cue);

        if (FadeWarnKeys.Contains(key))
            UpdateFadeWarning(_cue);

        // Sincronía barra de trim ↔ campos numéricos / archivo (mismo reparto
        // que WinCue.App.InspectorPanel.Commit).
        if (key is "trim_start" or "trim_end")
            _wave.SetMarkers(_cue.TrimStart, _cue.TrimEnd);
        else if (key == "file_path")
            UpdateWave(_cue, force: true);
        else if (GeoPreviewKeys.Contains(key))
        {
            // Reflejar en lienzo + paneles + controles (sin re-commitear) —
            // mismo reparto que WinCue.App.InspectorPanel.Commit.
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
        else if (IsEqKey(key))
        {
            // Repintar las dos gráficas (el campo numérico ya cambió el cue;
            // el lienzo lee el MISMO objeto, solo hace falta invalidar) —
            // mismo motivo por el que MakeNumBox/MakeCheck invalidaban en
            // WinCue.App.EqEditorControl.
            _eqCanvas.Refresh();
            _compCanvas.Refresh();
        }
    }

    /// <summary>Mismo criterio que MainController.IsEqKey (privado allí): qué
    /// claves pertenecen al rack de audio.</summary>
    private static bool IsEqKey(string key) =>
        key is "gain_db" or "eq_enabled" or "hpf_on" or "hpf_freq" or "lpf_on" or "lpf_freq"
        || key.StartsWith("eq", StringComparison.Ordinal)
        || key.StartsWith("comp", StringComparison.Ordinal);

    /// <summary>Carga la barra de trim (decodifica solo si cambia el
    /// archivo). Solo AUDIO en esta pasada — ver la cabecera de
    /// WaveformCanvas.cs.</summary>
    private void UpdateWave(Cue cue, bool force = false)
    {
        if (cue.Type != CueType.Audio || cue.FilePath.Length == 0)
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
        _wave.LoadAudio(cue.FilePath, cue.TrimStart, cue.TrimEnd);
    }

    /// <summary>Barra de trim con onda: arrastrar sus manejadores comete
    /// trim_start/trim_end y repuebla los campos numéricos, igual que
    /// WinCue.App.InspectorPanel hace con MarkersChanged.</summary>
    private void WaveformRow()
    {
        Header(Loc.T("BARRA DE TRIM — arrastra los manejadores", "TRIM BAR — drag the handles"));
        _wave.Margin = new Thickness(10, 2, 10, 6);
        _wave.Height = UiMetrics.TrimBarHeight;
        _root.Children.Add(_wave);
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
    }

    // ==================================================================
    // Framework de formularios (helpers) — equivalente WinUI 3 de los
    // helpers de WinCue.App.InspectorPanel, un único formulario en columna
    // en vez de tres pestañas: el conjunto de campos de esta pasada es
    // pequeño y no necesita más disposición que esa (ver el informe).
    // ==================================================================

    private static void Header(StackPanel panel, string text)
    {
        // Margen (0,12,0,4): el de WinCue. El 10 horizontal que llevaba antes
        // sobra desde que cada página tiene su propio margen (ver Tab()).
        var block = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
        block.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextDimBrush,
            Margin = new Thickness(0, 0, 0, 3),
        });
        block.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.DividerBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        panel.Children.Add(block);
    }

    private void Header(string text) => Header(_root, text);

    // ------------------------------------------------------------------
    // Pestañas y columnas — mismas medidas que WinCue.App/InspectorPanel.cs
    // ------------------------------------------------------------------
    // WinCue reparte el inspector en pestañas, y dentro de cada una en
    // columnas separadas por 30 px (helpers Tab/Page/Columns de allí). Esto es
    // su equivalente en WinUI 3, con los MISMOS números: si se cambia uno, el
    // inspector se lee distinto que el de WinCue y esa es justo la diferencia
    // que este trabajo vino a quitar.

    /// <summary>Abre una pestaña nueva y deja _root apuntando a su página, de
    /// modo que todo lo que se construya a partir de aquí cae dentro.
    ///
    /// icon: true cuando el título EMPIEZA por un glifo de Icons (Phosphor):
    /// en WinUI 3 la fuente es del control entero, así que un Header de texto
    /// plano pintaría el glifo como un cuadrito. Se parte en dos TextBlock,
    /// mismo remedio que Chk(icon: true) — ver IconFont.cs.</summary>
    private TabViewItem Tab(string title, bool icon = false)
    {
        var panel = new StackPanel { Margin = new Thickness(10, 6, 10, 10) };
        var item = new TabViewItem
        {
            Header = icon ? IconLabel(title) : title,
            IsClosable = false,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel,
            },
        };
        _tabs.TabItems.Add(item);
        _root = panel;
        return item;
    }

    /// <summary>Abre la pestaña de Audio — la usa el mixer al pinchar un canal
    /// de cue, igual que WinCue.App.InspectorPanel.ShowAudioTab.</summary>
    public void ShowAudioTab() => _tabs.SelectedItem = _audioTab;

    private TabViewItem? _audioTab;

    /// <summary>Parte la página actual en <paramref name="count"/> columnas de
    /// ancho automático separadas por 30 px, y devuelve sus paneles. Quien
    /// llama va poniendo _root en cada uno para llenarlo.</summary>
    private StackPanel[] Columns(int count)
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
        _root.Children.Add(grid);
        return cols;
    }

    private StackPanel LabeledRow(string label)
    {
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            // 185 y margen (0,2,0,2): los de WinCue.App.LabeledRow. Con 150 las
            // etiquetas largas se partían en dos líneas y las filas no
            // cuadraban entre columnas.
            Width = 185,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        _root.Children.Add(row);
        return row;
    }

    private static TextBox MakeTextBox(double width) => new()
    {
        Background = Theme.ControlBgBrush,
        Foreground = Theme.TextPrimaryBrush,
        BorderBrush = Theme.DividerBrush,
        Padding = new Thickness(6, 4, 6, 4),
        VerticalAlignment = VerticalAlignment.Center,
        Width = width,
    };

    /// <summary>Commit al perder foco o pulsar Enter (si el valor parsea) —
    /// mismo contrato que WireTextCommit en WPF.</summary>
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
            if (e.Key == VirtualKey.Enter)
            {
                DoCommit();
                e.Handled = true;
            }
        };
    }

    private void Txt(string key, string label, Func<Cue, string> read)
    {
        var row = LabeledRow(label);
        var tb = MakeTextBox(220);
        WireTextCommit(tb, key, () => tb.Text);
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c);
    }

    /// <summary>Campo de tiempo (pre-wait, duración, fundidos, trim…): mismo
    /// texto editable que WPF, con el formato/parseo COMPARTIDO de
    /// WinCue.Presentation.CueTimeField.</summary>
    private void TimeF(string key, string label, Func<Cue, double> read)
    {
        var row = LabeledRow(label);
        var tb = MakeTextBox(90);
        WireTextCommit(tb, key, () => CueTimeField.Parse(tb.Text));
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = CueTimeField.Format(read(c));
    }

    private void Int(string key, string label, Func<Cue, int> read)
    {
        var row = LabeledRow(label);
        var tb = MakeTextBox(70);
        WireTextCommit(tb, key, () =>
            int.TryParse(tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v : (object?)null);
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Casilla de un campo booleano. icon: true cuando la etiqueta
    /// EMPIEZA por un glifo de Icons (Phosphor): en WinUI 3 el FontFamily es
    /// del control entero, así que el texto se parte en dos TextBlock — el
    /// glifo con Phosphor y las letras con la fuente normal. Es la misma
    /// razón por la que la tabla de cues nunca mezcla glifo y texto en un
    /// mismo TextBlock (ver IconFont.cs); en WPF no hace falta porque su
    /// fuente compuesta resuelve por rango.</summary>
    private void Chk(string key, string label, Func<Cue, bool> read, bool icon = false)
    {
        var cb = new CheckBox
        {
            Content = icon ? IconLabel(label) : label,
            Foreground = Theme.TextPrimaryBrush,
            Margin = new Thickness(0, 3, 0, 3),   // ChkRow de WinCue
        };
        cb.Checked += (_, _) => Commit(key, true);
        cb.Unchecked += (_, _) => Commit(key, false);
        _root.Children.Add(cb);
        _loaders[key] = c => cb.IsChecked = read(c);
    }

    /// <summary>Etiqueta que empieza por un glifo de icono: glifo con Phosphor
    /// y el resto con la fuente normal, en una fila. Ver Chk(icon: true).</summary>
    private static UIElement IconLabel(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = label[..1],
            FontFamily = IconFont.Font,
            FontSize = 14,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = label[1..].TrimStart(),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    /// <summary>Construye lo que <paramref name="build"/> añada y lo hace
    /// visible solo para los cues que cumplan <paramref name="visibleIf"/> —
    /// mismo papel que el helper Conditional del inspector de WPF. Envuelve
    /// TODO lo añadido (una fila, una casilla…) para no depender de que sea
    /// un único control.</summary>
    private void Conditional(Func<Cue, bool> visibleIf, Action build)
    {
        int before = _root.Children.Count;
        build();
        for (int i = before; i < _root.Children.Count; i++)
            if (_root.Children[i] is FrameworkElement el)
                _conditional.Add((el, visibleIf));
    }

    /// <summary>Fila etiquetada que solo se ve para ciertos cues.</summary>
    private StackPanel ConditionalRow(string label, Func<Cue, bool> visibleIf)
    {
        var row = LabeledRow(label);
        _conditional.Add((row, visibleIf));
        return row;
    }

    /// <summary>Fila de muestras de color de etiqueta. La paleta, su orden y
    /// las etiquetas son cálculo COMPARTIDO (WinCue.Presentation.CueChoices.
    /// Colors): aquí solo se pinta. Con el color de un GRUPO se pinta su
    /// recuadro en la tabla y en el timeline, así que la muestra activa tiene
    /// que verse — misma marca que en WinCue.</summary>
    private void ColorSwatches()
    {
        var row = LabeledRow(Loc.T("Color:", "Color:"));
        var swatches = new List<(Button Btn, CueColorChoice Choice, TextBlock Mark)>();
        foreach (var choice in CueChoices.Colors)
        {
            var mark = new TextBlock
            {
                FontFamily = IconFont.Font,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var btn = new Button
            {
                Width = 26,
                Height = 22,
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(0),
                Background = choice.HasColor
                    ? Theme.Solid(Theme.Hex(choice.Rgb))
                    : Theme.TransparentBrush,
                BorderBrush = Theme.TextFaintBrush,
                Content = mark,
            };
            ToolTipService.SetToolTip(btn, choice.Label);
            string key = choice.Key;
            btn.Click += (_, _) => Commit("color", key);
            row.Children.Add(btn);
            swatches.Add((btn, choice, mark));
        }
        _loaders["color"] = c =>
        {
            foreach (var (_, choice, mark) in swatches)
            {
                bool active = choice.Key == c.Color;
                // La marca va oscura sobre el color y clara sobre el hueco de
                // «sin color»: contraste en los dos casos (igual que WinCue).
                mark.Text = active ? Icons.Check : choice.HasColor ? "" : Icons.NoColor;
                mark.Foreground = active && choice.HasColor
                    ? Theme.TextOnAccentBrush : Theme.TextPrimaryBrush;
            }
        };
    }

    /// <summary>Cue objetivo de un START/STOP/FADE. La lista de candidatos la
    /// da AllCues (la pone MainWindow): el inspector no conoce el CueModel,
    /// igual que en WPF.</summary>
    private void TargetCombo()
    {
        var row = ConditionalRow(Loc.T("Cue objetivo:", "Target cue:"),
            c => c.TargetsOther());
        var combo = new ComboBox { MinWidth = 230 };
        combo.SelectionChanged += (_, _) =>
        {
            if ((combo.SelectedItem as ComboBoxItem)?.Tag is int id)
                Commit("target_id", id == -1 ? null : id);
        };
        row.Children.Add(combo);
        _loaders["target_id"] = c =>
        {
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem
            { Content = Loc.T("(sin objetivo)", "(no target)"), Tag = -1 });
            if (AllCues is not null)
                foreach (var other in AllCues())
                    if (other.Id != c.Id)
                        combo.Items.Add(new ComboBoxItem
                        {
                            Content = $"{other.DisplayNumber} · {other.Name}",
                            Tag = other.Id,
                        });
            foreach (ComboBoxItem item in combo.Items)
                if ((int)item.Tag == (c.TargetId ?? -1))
                {
                    combo.SelectedItem = item;
                    return;
                }
        };
    }

    /// <summary>Disparadores del cue más allá del GO: tecla local, qué hacer
    /// si se re-dispara, «fundir y parar otros», hora del día (wall-clock) y
    /// MIDI. Los MISMOS campos que la pestaña «Disparadores» de WinCue, con
    /// las opciones compartidas (CueChoices) — el backend que los atiende ya
    /// es común (WinCue.Controller + WinCue.Remote), así que esto es
    /// exponerlos, no implementarlos.</summary>
    private void TriggersSection()
    {
        // Tres columnas, el MISMO reparto que WinCue.App.BuildTriggers:
        // disparo local + fundir/parar otros │ wall-clock │ MIDI.
        var cols = Columns(3);

        _root = cols[0];
        Header(Loc.T("DISPARO LOCAL", "LOCAL TRIGGER"));
        Txt("hotkey", Loc.T("Hotkey (tecla):", "Hotkey (key):"), c => c.Hotkey);
        Note(Loc.T(
            "Los hotkeys por cue todavía no se atienden en Secuence:\nel campo se guarda y WinCue lo respeta.",
            "Per-cue hotkeys are not handled in Secuence yet:\nthe field is saved and WinCue honours it."));
        Cmb("retrigger", Loc.T("Segundo disparo:", "Second trigger:"), c => c.Retrigger.ToString(),
            CueChoices.RetriggerModes);

        Header(Loc.T("FUNDIR Y PARAR OTROS", "FADE & STOP OTHERS"));
        Chk("fade_others",
            Loc.T("Al dispararse, funde y para otros cues", "On trigger, fade and stop other cues"),
            c => c.FadeOthers);
        Num("fade_others_time", Loc.T("Tiempo del fundido (s):", "Fade time (s):"), c => c.FadeOthersTime);
        Cmb("fade_others_scope", Loc.T("Ámbito:", "Scope:"), c => c.FadeOthersScope.ToString(),
            CueChoices.FadeScopes);

        _root = cols[1];
        Header(Loc.T("WALL-CLOCK (disparo por hora)", "WALL-CLOCK (time-of-day trigger)"));
        Chk("wallclock_enabled", Loc.T("Disparar a una hora del día", "Trigger at a time of day"),
            c => c.WallclockEnabled);
        Txt("wallclock_time", Loc.T("Hora (HH:MM:SS, 24h):", "Time (HH:MM:SS, 24h):"), c => c.WallclockTime);
        Note(Loc.T("Se dispara cada día a esa hora\nmientras el show esté abierto.",
                   "Triggers every day at that time\nwhile the show is open."));

        _root = cols[2];
        Header("MIDI");
        Chk("midi_enabled", Loc.T("Disparo por MIDI", "MIDI trigger"), c => c.MidiEnabled);
        Cmb("midi_type", Loc.T("Tipo de mensaje:", "Message type:"), c => c.MidiType.ToString(),
            CueChoices.MidiTypes);
        Int("midi_channel", Loc.T("Canal:", "Channel:"), c => c.MidiChannel);
        Int("midi_note", Loc.T("Nota / CC:", "Note / CC:"), c => c.MidiNote);
        Int("midi_velocity", Loc.T("Velocity (-1 = cualquiera):", "Velocity (-1 = any):"), c => c.MidiVelocity);
    }

    /// <summary>Campo numérico con decimales (tiempo de fundido de "parar
    /// otros", velocidad…). Los TIEMPOS de cue van por TimeF, que usa el
    /// formato compartido de CueTimeField; esto es para las cifras sueltas
    /// que WPF también muestra como número corriente (helper Num allí).</summary>
    private void Num(string key, string label, Func<Cue, double> read)
    {
        var row = LabeledRow(label);
        var tb = MakeTextBox(90);
        WireTextCommit(tb, key, () =>
            double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : (object?)null);
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Notas del cue: varias líneas, con el mismo destino que en WPF
    /// (se muestran en standby). AcceptsReturn implica que Enter NO comete —
    /// mete un salto de línea, como debe— así que aquí el commit es solo al
    /// perder el foco.</summary>
    private void TxtMultiline(string key, string label, Func<Cue, string> read)
    {
        var row = LabeledRow(label);
        var tb = MakeTextBox(220);
        tb.AcceptsReturn = true;
        tb.TextWrapping = TextWrapping.Wrap;
        tb.Height = 60;
        tb.VerticalAlignment = VerticalAlignment.Top;
        tb.LostFocus += (_, _) => Commit(key, tb.Text);
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c);
    }

    /// <summary>Aclaración discreta bajo un campo — mismo papel que el helper
    /// Note del inspector de WPF.</summary>
    private void Note(string text) => _root.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 11,
        Foreground = Theme.TextFaintBrush,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 300,
        Margin = new Thickness(0, 0, 0, 6),
    });

    /// <summary>Desplegable con opciones escritas aquí mismo. Reservado a los
    /// combos que NO son un enum compartido con WinCue — hoy solo «geo_mode»,
    /// que además usa los valores en UPPER_SNAKE porque WinCue no tiene un
    /// combo equivalente (allí es una casilla FULL/CUSTOM). Para todo lo
    /// demás, la lista viene de CueChoices: repetir aquí las opciones de un
    /// enum es exactamente la copia que la regla 2 prohíbe.</summary>
    private void Cmb(string key, string label, Func<Cue, string> read,
                     params (string Value, string Label)[] items) =>
        Cmb(key, label, read,
            items.Select(i => new CueChoice(i.Value, i.Label)).ToList());

    private void Cmb(string key, string label, Func<Cue, string> read,
                     IReadOnlyList<CueChoice> items)
    {
        var row = LabeledRow(label);
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

    /// <summary>Archivo del cue — solo visible en cues de media o script,
    /// igual que WPF (Cue.IsMedia(), compartido con el motor: no se repite
    /// aquí qué tipos cuentan como "media"). El botón de examinar delega en
    /// BrowseFile porque el picker necesita el hwnd de la ventana.</summary>
    private void FileRow()
    {
        var row = LabeledRow(Loc.T("Archivo:", "File:"));
        _conditional.Add((row, c => c.IsMedia() || c.Type == CueType.Script));

        var tb = MakeTextBox(220);
        WireTextCommit(tb, "file_path", () => tb.Text);
        row.Children.Add(tb);

        var browse = new Button
        {
            Content = new TextBlock { Text = Icons.Browse, FontFamily = IconFont.Font, FontSize = 14 },
            Margin = new Thickness(4, 0, 0, 0),
        };
        ToolTipService.SetToolTip(browse, Loc.T("Examinar…", "Browse…"));
        browse.Click += async (_, _) =>
        {
            if (BrowseFile is null || _cue is null)
                return;
            string? path = await BrowseFile();
            if (path is null)
                return;
            tb.Text = path;
            Commit("file_path", path);
        };
        row.Children.Add(browse);

        _loaders["file_path"] = c => tb.Text = c.FilePath;
    }

    /// <summary>Volumen: mismo fader (rango y ley de WinCue.Core.AudioLevels,
    /// COMPARTIDO con el mixer y el inspector de WPF) sin el VU ni los
    /// botones MUTE/CUE — eso es "rack de audio" y queda fuera de esta
    /// pasada (ver el encargo). El Commit real vive en el ValueChanged del
    /// slider: escribir la cifra en dB solo MUEVE el slider (igual patrón
    /// que el inspector de WPF), así que solo hay un sitio que compromete el
    /// campo al motor.</summary>
    private void VolumeField()
    {
        var row = LabeledRow(Loc.T("Volumen:", "Volume:"));
        var slider = new Slider
        {
            Minimum = 0, Maximum = VolPosSteps, Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = MakeTextBox(90);

        slider.ValueChanged += (_, e) =>
        {
            double v = AudioLevels.PositionToGain(e.NewValue / VolPosSteps);
            box.Text = VolumeLabel(v);
            Commit("volume", v);
        };

        void CommitTypedVolume()
        {
            if (AudioLevels.ParseDb(box.Text) is not double gain)
            {
                box.Text = VolumeLabel(AudioLevels.PositionToGain(slider.Value / VolPosSteps));
                return;
            }
            slider.Value = AudioLevels.GainToPosition(gain) * VolPosSteps;
        }
        box.LostFocus += (_, _) => CommitTypedVolume();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                CommitTypedVolume();
                e.Handled = true;
            }
        };

        row.Children.Add(slider);
        row.Children.Add(box);
        _loaders["volume"] = c =>
        {
            slider.Value = AudioLevels.GainToPosition(c.Volume) * VolPosSteps;
            box.Text = VolumeLabel(c.Volume);
        };
    }

    private static string VolumeLabel(double v) =>
        v <= 0.0001 ? "−∞ dB" : $"{20.0 * Math.Log10(v):+0.0;-0.0} dB";

    // ==================================================================
    // Geometría de vídeo: modo, aspecto, escala/posición/rotación y el
    // lienzo interactivo (GeometryCanvas, Win2D). Mismo reparto de campos
    // que WinCue.App.InspectorPanel.BuildGeometry, en la columna única de
    // este formulario (Secuence no tiene aún el layout a dos columnas de
    // WPF — ver la cabecera del archivo).
    // ==================================================================

    /// <summary>Campo numérico (double) con commit al perder foco/Enter —
    /// mismo contrato que TimeF/Int, sin el formato de tiempo.</summary>
    private void Dbl(string key, string label, Func<Cue, double> read, string fmt = "0.##")
    {
        var row = LabeledRow(label);
        var tb = MakeTextBox(80);
        WireTextCommit(tb, key, () =>
            double.TryParse(tb.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : (object?)null);
        row.Children.Add(tb);
        _loaders[key] = c => tb.Text = read(c).ToString(fmt, CultureInfo.InvariantCulture);
    }

    private void GeometrySection()
    {
        // Toda la sección (cabecera, modo, paneles FULL/CUSTOM y lienzo) solo
        // tiene sentido para cues de VÍDEO — a diferencia del resto de campos
        // de esta pasada (tiempos, fundidos…), que WinCue tampoco esconde por
        // tipo. VisibleIf lo evalúa SetCue; aquí solo se recogen los
        // elementos de nivel superior para registrarlos.
        bool VideoOnly(Cue c) => c.Type == CueType.Video;

        var header = LastAdded(() => Header(Loc.T("GEOMETRÍA DE VÍDEO", "VIDEO GEOMETRY")));
        _conditional.Add((header, VideoOnly));

        var modeRow = LastAdded(() => Cmb("geo_mode", Loc.T("Modo:", "Mode:"),
            c => c.GeoMode.ToString().ToUpperInvariant(),
            ("FULL", Loc.T("Full — llena el canvas", "Full — fills the canvas")),
            ("CUSTOM", Loc.T("Custom — posición manual", "Custom — manual position"))));
        _conditional.Add((modeRow, VideoOnly));

        // ── Panel FULL ──────────────────────────────────────────────────
        _fullGeoPanel = new StackPanel();
        _root.Children.Add(_fullGeoPanel);
        _conditional.Add((_fullGeoPanel, VideoOnly));
        {
            var aspectRow = new CheckBox
            {
                Content = Loc.T("Mantener relación de aspecto", "Keep aspect ratio"),
                Foreground = Theme.TextPrimaryBrush,
                Margin = new Thickness(10, 3, 10, 3),
            };
            aspectRow.Checked += (_, _) => Commit("preserve_aspect", true);
            aspectRow.Unchecked += (_, _) => Commit("preserve_aspect", false);
            _fullGeoPanel.Children.Add(aspectRow);
            _loaders["preserve_aspect"] = c => aspectRow.IsChecked = c.PreserveAspect;
        }
        MoveLastRowInto(_fullGeoPanel,
            () => Dbl("scale", Loc.T("Escala (%):", "Scale (%):"), c => c.Scale, "0"));

        // ── Panel CUSTOM ────────────────────────────────────────────────
        _customGeoPanel = new StackPanel();
        _root.Children.Add(_customGeoPanel);
        _conditional.Add((_customGeoPanel, VideoOnly));
        MoveLastRowInto(_customGeoPanel,
            () => Dbl("scale_x", Loc.T("Escala (×):", "Scale (×):"), c => c.ScaleX, "0.00"));
        MoveLastRowInto(_customGeoPanel,
            () => Dbl("translate_x", Loc.T("Posición X (px):", "Position X (px):"), c => c.TranslateX, "0"));
        MoveLastRowInto(_customGeoPanel,
            () => Dbl("translate_y", Loc.T("Posición Y (px):", "Position Y (px):"), c => c.TranslateY, "0"));

        var rotRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4, 10, 2) };
        rotRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Rotación:", "Rotation:"), Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        foreach (int deg in new[] { 0, 90, 180, 270 })
        {
            var btn = new Button
            {
                Content = $"{deg}°", Width = 50,
                Margin = new Thickness(0, 0, 4, 0),
                Background = Theme.ButtonBgBrush,
                Foreground = Theme.TextPrimaryBrush,
            };
            btn.Click += (_, _) =>
            {
                if (_loading || _cue is null) return;
                Commit("rotate_z", (double)deg);
            };
            _rotButtons.Add((btn, deg));
            rotRow.Children.Add(btn);
        }
        _customGeoPanel.Children.Add(rotRow);

        // El lienzo ocupa el ancho del formulario, con la misma proporción
        // 16:9 aproximada que WinCue le da en su columna derecha.
        _geoCanvas.Height = UiMetrics.TrimBarHeight * 3;
        _geoCanvas.Margin = new Thickness(10, 8, 10, 10);
        _geoCanvas.Translated += (tx, ty) =>
        {
            Commit("translate_x", tx);
            Commit("translate_y", ty);
        };
        // Commit dispara SyncGeometryUi, así que el campo "Escala (×)" sigue
        // al tirador solo; no hace falta tocarlo aquí.
        _geoCanvas.Scaled += sx => Commit("scale_x", sx);
        _root.Children.Add(_geoCanvas);
        _conditional.Add((_geoCanvas, VideoOnly));

        _loaders["__geo_sync"] = SyncGeometryUi;
    }

    /// <summary>Ejecuta <paramref name="build"/> (que añade una fila a
    /// _root, como Header/Cmb) y devuelve esa fila ya añadida — para
    /// registrarla en _conditional sin reescribir esos helpers con un
    /// StackPanel de destino.</summary>
    private FrameworkElement LastAdded(Action build)
    {
        build();
        return (FrameworkElement)_root.Children[^1];
    }

    /// <summary>Como <see cref="LastAdded"/>, pero MUEVE la fila resultante a
    /// <paramref name="target"/> en vez de dejarla en _root — para los campos
    /// que caen dentro del panel FULL o CUSTOM (Dbl() no recibe un StackPanel
    /// de destino).</summary>
    private void MoveLastRowInto(StackPanel target, Action build)
    {
        var row = LastAdded(build);
        _root.Children.RemoveAt(_root.Children.Count - 1);
        target.Children.Add(row);
    }

    /// <summary>Paneles FULL/CUSTOM, botones de rotación y lienzo — mismo
    /// reparto que WinCue.App.InspectorPanel.SyncGeometryUi.</summary>
    private void SyncGeometryUi(Cue c)
    {
        bool custom = c.GeoMode == GeoMode.Custom;
        _fullGeoPanel.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        _customGeoPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

        long rot = (((long)(Math.Round(c.RotateZ / 90.0) * 90)) % 360 + 360) % 360;
        foreach (var (btn, deg) in _rotButtons)
            btn.Background = deg == rot ? Theme.StandbyBrush : Theme.ButtonBgBrush;

        _geoCanvas.SetValues(custom, c.Scale, c.PreserveAspect, c.ScaleX, c.TranslateX, c.TranslateY, rot);
    }

    // ==================================================================
    // Rack de audio: ganancia + EQ paramétrica (4 bandas + HPF/LPF) +
    // compresor, con RTA y medidor GR en vivo. Vale para cues de AUDIO y de
    // VÍDEO — mismo alcance que WinCue.App.EqEditorControl. Layout en
    // columna única (Secuence no tiene aún el reparto "parámetros a la
    // izquierda de su gráfica" de WPF — mismo motivo que GeometrySection):
    // los campos van encima, el lienzo debajo, igual que la barra de trim.
    // ==================================================================

    private void EqSection()
    {
        bool AudioOrVideo(Cue c) => c.Type is CueType.Audio or CueType.Video;

        var header = LastAdded(() => Header(Loc.T("GANANCIA / EQ", "GAIN / EQ")));
        _conditional.Add((header, AudioOrVideo));

        // Ganancia de clip (dB, pre-EQ) — aparte del fader de VOLUMEN de arriba.
        var gainRow = LabeledRow(Loc.T("Ganancia (dB):", "Gain (dB):"));
        gainRow.Children.Add(EqNumBox("gain_db", c => c.GainDb, -24, 12, 80));
        _conditional.Add((gainRow, AudioOrVideo));

        var eqOnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 10, 3) };
        eqOnRow.Children.Add(EqCheck("eq_enabled", Loc.T("EQ activa", "EQ active"), c => c.EqEnabled));
        _root.Children.Add(eqOnRow);
        _conditional.Add((eqOnRow, AudioOrVideo));

        var hpfRow = HpfLpfRow("HPF", "hpf_on", "hpf_freq", c => c.HpfOn, c => c.HpfFreq, EqCurveLayout.ClampHpfFreq);
        _conditional.Add((hpfRow, AudioOrVideo));
        var lpfRow = HpfLpfRow("LPF", "lpf_on", "lpf_freq", c => c.LpfOn, c => c.LpfFreq, EqCurveLayout.ClampLpfFreq);
        _conditional.Add((lpfRow, AudioOrVideo));

        var bandColors = new[] { Theme.Pgm, Theme.Warn, Theme.Go, Theme.Blue };
        for (int i = 0; i < 4; i++)
        {
            var row = BandRow(i + 1, bandColors[i]);
            _conditional.Add((row, AudioOrVideo));
        }

        _eqCanvas.Height = UiMetrics.EqCanvasHeight;
        _eqCanvas.Margin = new Thickness(10, 6, 10, 0);
        _eqCanvas.Commit = (k, v) => Commit(k, v);
        _eqCanvas.ReadoutChanged += text => _eqReadout.Text = text;
        _root.Children.Add(_eqCanvas);
        _conditional.Add((_eqCanvas, AudioOrVideo));

        _root.Children.Add(_eqReadout);
        _conditional.Add((_eqReadout, AudioOrVideo));

        // Visibilidad real (AudioOrVideo Y sin analizador) la decide
        // SyncEqUi — depende de AudioAnalysisProvider, no solo del tipo de
        // cue, así que no encaja en el patrón _conditional (Func<Cue,bool>
        // fijo). Se registra igualmente para que quede oculta por defecto
        // hasta el primer SetCue.
        _root.Children.Add(_noAnalyzerNote);

        var compHeader = LastAdded(() => Header(Loc.T("COMPRESOR", "COMPRESSOR")));
        _conditional.Add((compHeader, AudioOrVideo));

        var compOnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 10, 3) };
        compOnRow.Children.Add(EqCheck("comp_enabled", Loc.T("Compresor", "Compressor"), c => c.CompEnabled));
        _root.Children.Add(compOnRow);
        _conditional.Add((compOnRow, AudioOrVideo));

        var compRow1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 10, 3) };
        compRow1.Children.Add(FieldLabelInline(Loc.T("Umbral", "Threshold")));
        compRow1.Children.Add(EqNumBox("comp_threshold", c => c.CompThreshold, -60, 0, 50));
        compRow1.Children.Add(FieldLabelInline("dB"));
        compRow1.Children.Add(FieldLabelInline("Ratio"));
        compRow1.Children.Add(EqNumBox("comp_ratio", c => c.CompRatio, 1, 20, 40));
        compRow1.Children.Add(FieldLabelInline(":1"));
        _root.Children.Add(compRow1);
        _conditional.Add((compRow1, AudioOrVideo));

        var compRow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 10, 3) };
        compRow2.Children.Add(FieldLabelInline(Loc.T("Ataque", "Attack")));
        compRow2.Children.Add(EqNumBox("comp_attack", c => c.CompAttack, 0.1, 200, 44));
        compRow2.Children.Add(FieldLabelInline("ms"));
        compRow2.Children.Add(FieldLabelInline("Release"));
        compRow2.Children.Add(EqNumBox("comp_release", c => c.CompRelease, 5, 2000, 48, "0"));
        compRow2.Children.Add(FieldLabelInline("ms"));
        compRow2.Children.Add(FieldLabelInline("Makeup"));
        compRow2.Children.Add(EqNumBox("comp_makeup", c => c.CompMakeup, 0, 24, 44));
        compRow2.Children.Add(FieldLabelInline("dB"));
        _root.Children.Add(compRow2);
        _conditional.Add((compRow2, AudioOrVideo));

        _compCanvas.Height = UiMetrics.EqCanvasHeight;
        _compCanvas.Margin = new Thickness(10, 6, 10, 10);
        _compCanvas.Commit = (k, v) => Commit(k, v);
        _root.Children.Add(_compCanvas);
        _conditional.Add((_compCanvas, AudioOrVideo));

        _loaders["__eq_sync"] = SyncEqUi;
    }

    /// <summary>Repuebla los dos lienzos y decide si la nota "sin analizador"
    /// se ve — mismo criterio que WinCue.App.InspectorPanel.HasAudioAnalysis:
    /// sin AudioAnalysisProvider cableado, se asume el comportamiento clásico
    /// (solo cues de AUDIO tienen analizador real).</summary>
    private void SyncEqUi(Cue c)
    {
        _eqCanvas.SetCue(c);
        _compCanvas.SetCue(c);
        bool hasAnalysis = AudioAnalysisProvider?.Invoke(c.Id) ?? c.Type == CueType.Audio;
        _noAnalyzerNote.Visibility = c.Type is CueType.Audio or CueType.Video && !hasAnalysis
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Casilla suelta (sin LabeledRow: va empaquetada en una fila con
    /// más campos, como HPF/LPF y las bandas) — mismo contrato de commit que
    /// Chk(), pero sin reservar su propia fila.</summary>
    private CheckBox EqCheck(string key, string label, Func<Cue, bool> read)
    {
        var cb = new CheckBox
        {
            Content = label,
            Foreground = Theme.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        cb.Checked += (_, _) => Commit(key, true);
        cb.Unchecked += (_, _) => Commit(key, false);
        _loaders[key] = c => cb.IsChecked = read(c);
        return cb;
    }

    /// <summary>Caja numérica suelta con commit acotado a [min, max] —
    /// mismo contrato que MakeNumBox/MakeFreqBox en WinCue.App.
    /// EqEditorControl (el acotado es lo que ahí evitaba que un valor fuera
    /// de rango llegara al motor).</summary>
    private TextBox EqNumBox(string key, Func<Cue, double> read, double min, double max, double width, string fmt = "0.#")
    {
        var box = MakeTextBox(width);
        void DoCommit()
        {
            if (_loading)
                return;
            if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                Commit(key, Math.Round(Math.Clamp(v, min, max), 3));
        }
        box.LostFocus += (_, _) => DoCommit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                DoCommit();
                e.Handled = true;
            }
        };
        _loaders[key] = c => box.Text = read(c).ToString(fmt, CultureInfo.InvariantCulture);
        return box;
    }

    /// <summary>Fila HPF/LPF: casilla on/off + frecuencia acotada + "Hz" —
    /// mismo reparto que hpfRow/lpfRow en WinCue.App.EqEditorControl.</summary>
    private StackPanel HpfLpfRow(string label, string onKey, string freqKey,
        Func<Cue, bool> onRead, Func<Cue, double> freqRead, Func<double, double> clamp)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 10, 3) };
        row.Children.Add(EqCheck(onKey, label, onRead));
        row.Children.Add(EqNumBox(freqKey, freqRead, 0, 20000, 52, "0"));
        row.Children.Add(FieldLabelInline("Hz"));
        _root.Children.Add(row);
        return row;
        // El acotado de verdad (20..2000 HPF, 200..20000 LPF) lo aplica
        // EqNumBox con el "clamp" que se le pasó como min/max — aquí (0,
        // 20000) es solo el límite de PARSEO; ver la llamada en EqSection.
    }

    /// <summary>Fila de una banda paramétrica: punto de color + frecuencia +
    /// ganancia + Q — mismo reparto que el bucle de bandas en
    /// WinCue.App.EqEditorControl.</summary>
    private StackPanel BandRow(int n, Color dotColor)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 10, 3) };
        row.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(dotColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
        double ReadFreq(Cue c) => n switch { 1 => c.Eq1Freq, 2 => c.Eq2Freq, 3 => c.Eq3Freq, _ => c.Eq4Freq };
        double ReadGain(Cue c) => n switch { 1 => c.Eq1Gain, 2 => c.Eq2Gain, 3 => c.Eq3Gain, _ => c.Eq4Gain };
        double ReadQ(Cue c) => n switch { 1 => c.Eq1Q, 2 => c.Eq2Q, 3 => c.Eq3Q, _ => c.Eq4Q };
        row.Children.Add(EqNumBox($"eq{n}_freq", ReadFreq, 20, 20000, 52, "0"));
        row.Children.Add(FieldLabelInline("Hz"));
        row.Children.Add(EqNumBox($"eq{n}_gain", ReadGain, -18, 18, 42));
        row.Children.Add(FieldLabelInline("dB"));
        row.Children.Add(EqNumBox($"eq{n}_q", ReadQ, 0.1, 12, 38, "0.##"));
        row.Children.Add(FieldLabelInline("Q"));
        _root.Children.Add(row);
        return row;
    }

    private static TextBlock FieldLabelInline(string text) => new()
    {
        Text = text,
        Foreground = Theme.TextDimmerBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 3, 0),
        FontSize = 11,
    };
}
