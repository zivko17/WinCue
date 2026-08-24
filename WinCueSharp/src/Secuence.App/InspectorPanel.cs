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
// ver GeometryCanvas.cs para el detalle. Con la opacidad del compositor y la
// salida de vídeo por cue, en su columna derecha, igual que WinCue. Sigue
// fuera el VISOR DE EDICIÓN con fotograma fijo que WinCue pone a la derecha
// de esa pestaña — eso pide una ventana de vídeo que Secuence no tiene
// todavía; ver la cabecera de GeometryCanvas.cs para el porqué exacto.
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
using WinCue.Controller;
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

    /// <summary>¿El audio de este cue pasa por el rack nativo? Sin proveedor
    /// cableado se asume el comportamiento clásico (solo los cues de AUDIO
    /// tienen analizador) — mismo criterio que
    /// WinCue.App.InspectorPanel.HasAudioAnalysis.</summary>
    private bool HasAudioAnalysis(Cue cue) =>
        AudioAnalysisProvider?.Invoke(cue.Id) ?? cue.Type == CueType.Audio;

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
    private RadioButton _rbFull = null!;
    private RadioButton _rbCustom = null!;
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
        // Armado y Flag van EN LA MISMA FILA, como en WinCue (ChkRow): son la
        // misma decisión —si el cue entra en el GO y si va marcado— y apiladas
        // estiraban esta columna una fila más que sus dos vecinas.
        var armedRow = ChkRow();
        Chk(armedRow, "armed", Loc.T("Armado", "Armed"), c => c.Armed);
        Chk(armedRow, "flagged", $"{Icons.Flag} Flag", c => c.Flagged, icon: true);
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
        // (Aquí NO va el volumen: en WinCue el fader del cue vive en la
        // pestaña de Audio, dentro del strip de niveles. Tenerlo en las dos
        // repartía el mismo mando entre dos pestañas.)

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
        AudioTabSections();

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
        => Chk(_root, key, label, read, icon);

    /// <summary>Casilla dentro de un contenedor CONCRETO — para las que van
    /// en una fila horizontal compartida (ver <see cref="ChkRow"/>). El
    /// margen es distinto según dónde caiga: apilada necesita aire arriba y
    /// abajo, en fila lo necesita a la derecha. Son los dos márgenes de
    /// WinCue (ChkRow + Chk de allí).</summary>
    private void Chk(StackPanel target, string key, string label, Func<Cue, bool> read,
                     bool icon = false)
    {
        bool inRow = target.Orientation == Orientation.Horizontal;
        var cb = new CheckBox
        {
            Content = icon ? IconLabel(label) : label,
            Foreground = Theme.TextPrimaryBrush,
            Margin = inRow ? new Thickness(0, 0, 16, 0) : new Thickness(0, 3, 0, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cb.Checked += (_, _) => Commit(key, true);
        cb.Unchecked += (_, _) => Commit(key, false);
        target.Children.Add(cb);
        _loaders[key] = c => cb.IsChecked = read(c);
    }

    /// <summary>Fila horizontal para casillas que se leen JUNTAS (Armado +
    /// Flag). Mismo helper y mismo margen que WinCue.App.InspectorPanel.</summary>
    private StackPanel ChkRow()
    {
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        _root.Children.Add(row);
        return row;
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
        Cmb("retrigger", Loc.T("Segundo disparo:", "Second trigger:"), c => c.Retrigger.ToString(),
            CueChoices.RetriggerModes);
        // La advertencia va DEBAJO de los dos campos, no en medio: entre
        // «Hotkey» y «Segundo disparo» rompía el orden que tiene WinCue y
        // separaba dos campos que se leen juntos.
        Note(Loc.T(
            "Los hotkeys por cue todavía no se atienden en Secuence:\nel campo se guarda y WinCue lo respeta.",
            "Per-cue hotkeys are not handled in Secuence yet:\nthe field is saved and WinCue honours it."));

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
        Header(Loc.T("MIDI (backend en fase 6)", "MIDI (backend in phase 6)"));
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

    // ==================================================================
    // Pestaña de AUDIO: niveles del cue a la izquierda, rack (ganancia, EQ y
    // compresor) a la derecha — el MISMO reparto que
    // WinCue.App.InspectorPanel.BuildAudioTab, que dockea el bloque de
    // niveles a la izquierda y deja el editor de EQ llenando el resto.
    //
    // El fader de VOLUMEN vive AQUÍ y no en «Tiempo y bucles»: es el mismo
    // mando que el del strip de la mesa (misma ley, mismo tope), y tenerlo en
    // dos pestañas repartía la misma decisión entre dos sitios.
    // ==================================================================

    private void AudioTabSections()
    {
        var page = _root;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var levels = new StackPanel();
        var rack = new StackPanel();
        Grid.SetColumn(levels, 0);
        Grid.SetColumn(rack, 2);
        grid.Children.Add(levels);
        grid.Children.Add(rack);
        page.Children.Add(grid);

        _root = levels;
        LevelsSection();
        _root = rack;
        EqSection();
        _root = page;
    }

    // ── Strip de niveles del cue (el mismo que en la mesa) ──────────────
    private readonly VuMeterCanvas _levelVu = new(vertical: true);
    private readonly VuMeterCanvas _levelVuR = new(vertical: true);
    private Slider? _volSlider;
    private TextBox? _volBox;
    private Button _muteBtn = null!;
    private Button _soloBtn = null!;

    /// <summary>Estado de mute/solo de la mesa — el MISMO board que usa el
    /// mixer (MixerPanel.Board). No son dos juegos de botones que haya que
    /// mantener de acuerdo: es el mismo estado visto desde dos sitios.</summary>
    public MuteSoloBoard? MuteSolo;
    /// <summary>Avisar a la mesa de que el board cambió, para que repinte sus
    /// strips.</summary>
    public Action? MuteSoloChanged;
    /// <summary>Pedir la preescucha (PFL) de un cue por los cascos.</summary>
    public Action<int>? CueRequested;
    /// <summary>Id del cue que está ahora en el bus de audición, para encender
    /// el botón CUE.</summary>
    public Func<int>? AuditionSourceId;
    /// <summary>false si no hay dispositivo de cascos: sin él, CUE no puede
    /// hacer nada y va deshabilitado en vez de encenderse sin sonar.</summary>
    public Func<bool>? CueAvailable;

    /// <summary>Botón de la botonera de niveles, con la palabra entera —
    /// mismas medidas que en el strip de la mesa (UiMetrics).</summary>
    private static Button LevelButton(string text, string tip)
    {
        var b = new Button
        {
            Content = text,
            Width = UiMetrics.StripButtonWidth,
            Height = UiMetrics.StripButtonHeight,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            Background = Theme.ButtonBgBrush,
            Foreground = Theme.TextPrimaryBrush,
        };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    /// <summary>Pone los botones al día con lo que dice el board — mismo
    /// criterio que WinCue.App.InspectorPanel.RefreshMuteSolo: sin strip vivo
    /// no hay nada que silenciar, y sin cascos no hay dónde pinchar.</summary>
    private void RefreshMuteSolo()
    {
        bool live = _cue is not null && MuteSolo?.Knows(_cue.Id) == true;
        _muteBtn.IsEnabled = live;

        bool hayCascos = CueAvailable?.Invoke() ?? true;
        _soloBtn.IsEnabled = live && hayCascos;
        ToolTipService.SetToolTip(_soloBtn, hayCascos
            ? Loc.T("Escuchar este cue por los cascos (no toca la sala)",
                    "Listen to this cue on headphones (does not touch the room)")
            : Loc.T("Sin dispositivo de audición: elígelo en Ajustes → Audio",
                    "No audition device: pick one in Settings → Audio"));

        bool muted = live && MuteSolo!.IsMuted(_cue!.Id);
        bool soloed = _cue is not null && AuditionSourceId?.Invoke() == _cue.Id;
        _muteBtn.Background = muted ? Theme.PgmBrush : Theme.ButtonBgBrush;
        _muteBtn.Foreground = muted ? Theme.Solid(Colors.Black) : Theme.TextPrimaryBrush;
        _soloBtn.Background = soloed ? Theme.StandbyBrush : Theme.ButtonBgBrush;
        _soloBtn.Foreground = soloed ? Theme.Solid(Colors.Black) : Theme.TextPrimaryBrush;
    }

    /// <summary>La mesa cambió de estado (o entró/salió un cue): repintar.</summary>
    public void RefreshMuteSoloButtons() => RefreshMuteSolo();

    /// <summary>VU de ESTE cue, si es el que está en el inspector. Lo llama la
    /// ventana con los mismos niveles que manda al strip de la mesa: el mismo
    /// cue no puede medir distinto según dónde se mire.</summary>
    public void UpdateCueLevel(int cueId, float left, float right)
    {
        if (_cue is null || _cue.Id != cueId)
            return;
        _levelVu.SetLevel(left);
        _levelVuR.SetLevel(right);
    }

    /// <summary>Coloca el fader donde diga el MODELO, para que el del
    /// inspector y el del strip sean EL MISMO fader. No toca nada mientras el
    /// operador tenga este fader agarrado o esté escribiendo la cifra —
    /// pelearse con su mano es peor que ir desincronizado un segundo.</summary>
    public void SyncVolumeFromModel(Cue? cue)
    {
        if (cue is null || _cue is null || cue.Id != _cue.Id || _volSlider is null)
            return;
        if (_volSlider.FocusState != FocusState.Unfocused)
            return;
        if (_volBox is not null && _volBox.FocusState != FocusState.Unfocused)
            return;
        double target = AudioLevels.GainToPosition(cue.Volume) * VolPosSteps;
        if (Math.Abs(_volSlider.Value - target) < 1.0)
            return;
        bool was = _loading;
        _loading = true;
        try
        {
            _volSlider.Value = target;
            if (_volBox is not null)
                _volBox.Text = VolumeLabel(cue.Volume);
        }
        finally { _loading = was; }
    }

    /// <summary>Bloque de niveles: rótulo VOL, lectura en dB, escala + fader +
    /// VU estéreo y la botonera MUTE/CUE, en ese orden y dentro de su caja —
    /// el mismo strip que en la mesa (ver MixerPanel.Strip.Init) y el mismo
    /// orden que WinCue.App.InspectorPanel.BuildLevels.</summary>
    private void LevelsSection()
    {
        var faderCol = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };

        faderCol.Children.Add(new TextBlock
        {
            Text = "VOL",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextDimBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // LECTURA EN dB, EDITABLE y ARRIBA — como en el strip de la mesa. El
        // ancho es FIJO: el texto crece al subir el volumen («+0,0 dB (100%)»
        // → «+12,0 dB (400%)») y sin ancho reservado empujaba a su vecino.
        var box = new TextBox
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Theme.GoBrush,
            Background = Theme.TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = UiMetrics.DbReadoutWideWidth,
            TextAlignment = TextAlignment.Center,
            Text = VolumeLabel(1.0),
        };
        ToolTipService.SetToolTip(box,
            Loc.T("Escribe los dB y pulsa Intro", "Type the dB value and press Enter"));
        faderCol.Children.Add(new Border
        {
            Background = Theme.SunkenBgBrush,
            BorderBrush = Theme.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = box,
        });

        // El fader recorre POSICIÓN (0..1000), no ganancia: la ley la reparte
        // AudioLevels, COMPARTIDA con el mixer y con el inspector de WinCue.
        var slider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = VolPosSteps,
            Width = UiMetrics.FaderWidth, Height = UiMetrics.FaderHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(slider, Loc.T(
            "Volumen del cue: en vivo si está sonando.",
            "Cue volume: live if it is playing."));

        var faderRow = new StackPanel
        { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        faderRow.Children.Add(new FaderScaleCanvas
        { Width = UiMetrics.FaderScaleWidth, Height = UiMetrics.FaderHeight });
        faderRow.Children.Add(slider);
        // VU ESTÉREO pegado al fader, con las mismas medidas que en la mesa:
        // dos medidores de la misma señal con distinto ancho se leen como si
        // midieran cosas distintas.
        _levelVu.Width = 7;
        _levelVu.Height = UiMetrics.FaderHeight;
        _levelVu.Margin = new Thickness(6, 0, 0, 0);
        _levelVuR.Width = 7;
        _levelVuR.Height = UiMetrics.FaderHeight;
        _levelVuR.Margin = new Thickness(1, 0, 0, 0);
        ToolTipService.SetToolTip(_levelVu, Loc.T(
            "VU real de ESTE cue (cues de AUDIO y vídeos con AUDIO ESPEJO).",
            "THIS cue's real VU (AUDIO cues and videos with MIRRORED AUDIO)."));
        faderRow.Children.Add(_levelVu);
        faderRow.Children.Add(_levelVuR);
        faderCol.Children.Add(faderRow);

        // BOTONERA, la misma que en la mesa y sobre el MISMO board. Solo valen
        // con el cue SONANDO —es cuando existe su strip—, así que fuera de eso
        // van deshabilitados en vez de mentir.
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _muteBtn = LevelButton("MUTE", Loc.T("Silenciar este cue", "Mute this cue"));
        // CUE, no solo: manda el cue a los CASCOS sin tocar la sala.
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

        _root.Children.Add(new Border
        {
            Background = Theme.ToolbarBgBrush,
            BorderBrush = Theme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = faderCol,
        });

        // El VU por cue solo existe donde hay rack nativo (cues de AUDIO y
        // vídeo con audio espejo). El vídeo cuyo audio sale por mpv no es
        // medible → sin VU, igual que en la mesa.
        _conditional.Add((_levelVu, HasAudioAnalysis));
        _conditional.Add((_levelVuR, HasAudioAnalysis));

        _volSlider = slider;
        _volBox = box;

        slider.ValueChanged += (_, e) =>
        {
            double v = AudioLevels.PositionToGain(e.NewValue / VolPosSteps);
            box.Text = VolumeLabel(v);
            if (_loading) return;
            Commit("volume", v);   // en vivo: el controlador lo aplica al motor
        };

        // Escribir la cifra solo MUEVE el fader; quien compromete el valor al
        // motor es el ValueChanged de arriba. Así hay UN solo sitio que
        // commitea el volumen — mismo patrón que en WinCue.
        void CommitTypedVolume()
        {
            if (AudioLevels.ParseDb(box.Text) is not double gain)
            {
                // Ilegible: se repone lo que había. Un texto que no se entiende
                // no puede acabar en silencio, que es el peor sitio al que caer.
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
            else if (e.Key == VirtualKey.Escape)
            {
                box.Text = VolumeLabel(AudioLevels.PositionToGain(slider.Value / VolPosSteps));
                e.Handled = true;
            }
        };
        box.GotFocus += (_, _) => box.SelectAll();

        _loaders["volume"] = c =>
        {
            slider.Value = AudioLevels.GainToPosition(c.Volume) * VolPosSteps;
            box.Text = VolumeLabel(c.Volume);
        };
    }

    private static string VolumeLabel(double v) =>
        v <= 0.0001 ? "−∞ dB" : $"{20.0 * Math.Log10(v):+0.0;-0.0} dB ({v * 100:0}%)";

    // ==================================================================
    // Geometría de vídeo: modo, aspecto, escala/posición/rotación, opacidad
    // del compositor, salida del cue y el lienzo interactivo (GeometryCanvas,
    // Win2D). Mismo reparto y mismo ORDEN que
    // WinCue.App.InspectorPanel.BuildGeometry: MODO │ COMPOSITOR + SALIDA │
    // lienzo. En una sola columna los controles no cabían de una vez.
    // ==================================================================

    private void GeometrySection()
    {
        // Toda la pestaña solo tiene sentido para cues de VÍDEO — a diferencia
        // del resto de campos del inspector (tiempos, fundidos…), que WinCue
        // tampoco esconde por tipo.
        bool VideoOnly(Cue c) => c.Type == CueType.Video;

        var page = _root;

        // MODO │ COMPOSITOR + SALIDA │ LIENZO — el mismo reparto que
        // WinCue.App.InspectorPanel.BuildGeometry: dos columnas de controles
        // a la izquierda y el lienzo llenando lo que queda. En una sola
        // columna los controles pasaban del alto del panel y aparecía una
        // barra de desplazamiento: en una superficie de directo, un control
        // que hay que buscar deslizando es un control que no está.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var controls = new StackPanel { Width = 330 };
        var controlsRight = new StackPanel { Width = 250 };
        Grid.SetColumn(controls, 0);
        Grid.SetColumn(controlsRight, 2);
        grid.Children.Add(controls);
        grid.Children.Add(controlsRight);
        page.Children.Add(grid);
        _conditional.Add((grid, VideoOnly));

        // ── Columna IZQUIERDA: MODO ────────────────────────────────────
        _root = controls;
        Header(Loc.T("MODO", "MODE"));

        // DOS RADIOS, no un desplegable: son dos modos excluyentes y se ven
        // los dos a la vez, igual que en WinCue. Con un combo había que
        // abrirlo para saber qué otras opciones existían.
        _rbFull = new RadioButton
        {
            Content = Loc.T("Superficie completa — llena el canvas",
                            "Full surface — fills the canvas"),
            GroupName = "geo_mode_grp",
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = Theme.TextPrimaryBrush,
        };
        _rbCustom = new RadioButton
        {
            Content = Loc.T("Posición manual — escala, posición y giro",
                            "Manual position — scale, position and rotation"),
            GroupName = "geo_mode_grp",
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = Theme.TextPrimaryBrush,
        };
        _rbFull.Checked += (_, _) => OnGeoModeChanged(false);
        _rbCustom.Checked += (_, _) => OnGeoModeChanged(true);
        controls.Children.Add(_rbFull);
        controls.Children.Add(_rbCustom);

        // ── Panel FULL ──────────────────────────────────────────────────
        _fullGeoPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(_fullGeoPanel);
        Chk(_fullGeoPanel, "preserve_aspect",
            Loc.T("Mantener relación de aspecto", "Keep aspect ratio"),
            c => c.PreserveAspect);
        SliderF(_fullGeoPanel, "scale", Loc.T("Escala (%):", "Scale (%):"),
            10, 400, c => c.Scale, "0");

        // ── Panel CUSTOM ────────────────────────────────────────────────
        _customGeoPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(_customGeoPanel);
        SliderF(_customGeoPanel, "scale_x", Loc.T("Escala (×):", "Scale (×):"),
            0.1, 4.0, c => c.ScaleX, "0.00");
        SliderF(_customGeoPanel, "translate_x", Loc.T("Posición X (px):", "Position X (px):"),
            -960, 960, c => c.TranslateX, "0");
        SliderF(_customGeoPanel, "translate_y", Loc.T("Posición Y (px):", "Position Y (px):"),
            -540, 540, c => c.TranslateY, "0");

        var rotRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        rotRow.Children.Add(new TextBlock
        {
            // 112, el de WinCue: es la misma fila con los mismos cuatro
            // botones y tiene que empezar donde empiezan los deslizadores.
            Text = Loc.T("Rotación:", "Rotation:"), Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
        });
        foreach (int deg in new[] { 0, 90, 180, 270 })
        {
            var btn = new Button
            {
                Content = $"{deg}°", Width = 46,
                Padding = new Thickness(0),
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

        // CENTRAR y RESTABLECER: los dos atajos de WinCue. Sin ellos, volver
        // de una posición manual a la de partida era teclear cuatro campos.
        var btnRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var btnCenter = new Button
        {
            Content = IconLabel($"{Icons.Target} {Loc.T("Centrar", "Center")}"),
            Padding = new Thickness(10, 4, 10, 4),
            Background = Theme.ButtonBgBrush,
            Foreground = Theme.TextPrimaryBrush,
        };
        var btnReset = new Button
        {
            Content = IconLabel($"{Icons.Reset} {Loc.T("Restablecer", "Reset")}"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Theme.ButtonBgBrush,
            Foreground = Theme.TextPrimaryBrush,
        };
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
        _customGeoPanel.Children.Add(btnRow);

        // ── Columna DERECHA: compositor y salida ───────────────────────
        _root = controlsRight;
        Header(Loc.T("COMPOSITOR", "COMPOSITOR"));
        var compRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        compRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Opacidad %:", "Opacity %:"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
            Margin = new Thickness(0, 0, 4, 0),
        });
        var opacityBox = MakeTextBox(48);
        WireTextCommit(opacityBox, "opacity", () =>
            double.TryParse(opacityBox.Text.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double ov)
                ? ov : (object?)null);
        compRow.Children.Add(opacityBox);
        controlsRight.Children.Add(compRow);
        _loaders["opacity"] = c =>
            opacityBox.Text = c.Opacity.ToString("0.#", CultureInfo.InvariantCulture);

        // ── Salida de vídeo del cue (multi-monitor estilo Watchout) ────
        Header(Loc.T("SALIDA", "OUTPUT"));
        var outRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        outRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Salida:", "Output:"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextTertiaryBrush,
            Margin = new Thickness(0, 0, 4, 0),
        });
        var screenCombo = new ComboBox { MinWidth = 170 };
        for (int i = 0; i < 4; i++)
            screenCombo.Items.Add(new ComboBoxItem
            {
                Content = i == 0
                    ? Loc.T("Salida 1 (principal)", "Output 1 (main)")
                    : Loc.T($"Salida {i + 1}", $"Output {i + 1}"),
                Tag = i,
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
            Text = Loc.T("El monitor de cada salida se asigna en Ajustes → Vídeo.",
                         "Each output's monitor is set in Settings → Video."),
            Foreground = Theme.TextDimBrush,
            FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        controlsRight.Children.Add(outNote);
        var canvasNote = new TextBlock
        {
            Text = Loc.T(
                "MODO CANVAS activo: el cue se coloca en el canvas y cada salida muestra su recorte (Ajustes → Vídeo).",
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
            // Con el modo canvas la selección de salida SE IGNORA: ocultarla,
            // porque mostrar un control muerto invita a errores en directo.
            bool canvasMode = SettingsManager.Instance.Get("video_canvas_mode", false)
                && SettingsManager.Instance.Get("video_compositor", false);
            outRow.Visibility = canvasMode ? Visibility.Collapsed : Visibility.Visible;
            outNote.Visibility = canvasMode ? Visibility.Collapsed : Visibility.Visible;
            canvasNote.Visibility = canvasMode ? Visibility.Visible : Visibility.Collapsed;
        };

        // ── El lienzo llena lo que queda a la derecha ───────────────────
        _geoCanvas.Height = UiMetrics.TrimBarHeight * 3;
        _geoCanvas.HorizontalAlignment = HorizontalAlignment.Stretch;
        _geoCanvas.VerticalAlignment = VerticalAlignment.Top;
        _geoCanvas.Margin = new Thickness(0, 6, 0, 10);
        _geoCanvas.Translated += (tx, ty) =>
        {
            Commit("translate_x", tx);
            Commit("translate_y", ty);
        };
        // Commit dispara SyncGeometryUi, así que el deslizador de «Escala (×)»
        // sigue al tirador solo; no hace falta tocarlo aquí.
        _geoCanvas.Scaled += sx => Commit("scale_x", sx);
        Grid.SetColumn(_geoCanvas, 4);
        grid.Children.Add(_geoCanvas);

        _loaders["__geo_sync"] = SyncGeometryUi;
        _root = page;
    }

    /// <summary>Cambio de modo desde los radios. Con _loading puesto es la
    /// propia carga del cue moviendo el radio, no el operador.</summary>
    private void OnGeoModeChanged(bool custom)
    {
        if (_loading || _cue is null)
            return;
        Commit("geo_mode", custom ? "CUSTOM" : "FULL");
    }

    /// <summary>Deslizador + cifra editable, como en WinCue: el deslizador
    /// para buscar y la cifra para clavar el valor. Los dos comprometen el
    /// mismo campo.</summary>
    private void SliderF(StackPanel target, string key, string label,
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
            box.Text = e.NewValue.ToString(fmt, CultureInfo.InvariantCulture);
            Commit(key, e.NewValue);
        };
        WireTextCommit(box, key, () =>
        {
            if (!double.TryParse(box.Text.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return null;
            v = Math.Clamp(v, min, max);
            bool was = _loading;
            _loading = true;
            try { slider.Value = v; } finally { _loading = was; }
            return v;
        });

        row.Children.Add(slider);
        row.Children.Add(box);
        target.Children.Add(row);
        _loaders[key] = c =>
        {
            double v = Math.Clamp(read(c), min, max);
            slider.Value = v;
            box.Text = v.ToString(fmt, CultureInfo.InvariantCulture);
        };
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

    /// <summary>Paneles FULL/CUSTOM, botones de rotación y lienzo — mismo
    /// reparto que WinCue.App.InspectorPanel.SyncGeometryUi.</summary>
    private void SyncGeometryUi(Cue c)
    {
        bool custom = c.GeoMode == GeoMode.Custom;
        // Marcar un radio dispara su Checked: con _loading puesto,
        // OnGeoModeChanged no commitea. Se pone a mano porque SyncGeometryUi
        // también se llama desde Commit (GeoPreviewKeys), fuera de SetCue.
        bool was = _loading;
        _loading = true;
        try
        {
            _rbFull.IsChecked = !custom;
            _rbCustom.IsChecked = custom;
        }
        finally { _loading = was; }

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
    // VÍDEO — mismo alcance que WinCue.App.EqEditorControl. Ocupa la COLUMNA
    // DERECHA de la pestaña de Audio; a su izquierda va el strip de niveles
    // del cue (ver AudioTabSections/LevelsSection), igual que en
    // WinCue.App.InspectorPanel.BuildAudioTab. Dentro de la columna, los
    // campos van encima y su lienzo debajo.
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
