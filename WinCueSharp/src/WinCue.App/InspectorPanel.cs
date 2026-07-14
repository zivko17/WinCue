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

    private readonly TabControl _tabs = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
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
        Foreground = new SolidColorBrush(Color.FromRgb(0xe8, 0xa2, 0x00)),
    };

    // Visor de edición (fotograma de referencia para trim y geometría)
    private readonly LiveMonitorControl _editPreview =
        new("EDICIÓN", Color.FromRgb(0xe8, 0xa2, 0x00));
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
        _editPreview.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x6f, 0xb0));
        _editPreview.BorderThickness = new Thickness(2);
        _editPreview.Height = 176;   // vídeo ≈ 16:9 dentro del panel de 280 px

        _previewHost.Children.Add(_editPreview);
        _previewHost.Children.Add(new TextBlock
        {
            Text = "El marco azul es el CANVAS de salida: lo que quede fuera " +
                   "se recorta. Arrastra el trim para ver el fotograma de corte.",
            FontSize = 10,
            Foreground = Brushes.Gray,
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
            if (_cue is { Type: CueType.Video })
                _editPreview.Scrub(seconds);
        };

        _tabs.Items.Add(Tab("Basics", BuildBasics()));
        _tabs.Items.Add(Tab("Triggers", BuildTriggers()));
        _tabs.Items.Add(Tab("Time & Loops", BuildTimeLoops()));
        _tabs.Items.Add(Tab("Geometry", BuildGeometry()));
        _tabs.Items.Add(Tab("Levels", BuildLevels()));

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
            _header.Text = "— sin cue seleccionado —";
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
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Visor de edición: solo para cues de VÍDEO con archivo.</summary>
    private void UpdateEditPreview(Cue cue)
    {
        if (cue.Type == CueType.Video && cue.FilePath.Length > 0)
        {
            _previewHost.Visibility = Visibility.Visible;
            _editPreview.ShowPaused(cue.FilePath, cue.TrimStart);
            ApplyPreviewGeometry(cue);
        }
        else
        {
            _previewHost.Visibility = Visibility.Collapsed;
            _editPreview.Clear();
        }
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

        Header(cols[0], "IDENTIDAD");
        Txt(cols[0], "number", "Número:", c => c.Number);
        _nameBox = Txt(cols[0], "name", "Nombre:", c => c.Name);
        ColorSwatches(cols[0]);
        var armedRow = ChkRow(cols[0]);
        Chk(armedRow, "armed", "Armado", c => c.Armed);
        Chk(armedRow, "flagged", "⚑ Flag", c => c.Flagged);
        TxtMultiline(cols[0], "notes", "Notas (se muestran en standby):",
            c => c.Notes);

        Header(cols[1], "TEMPORIZACIÓN");
        Cmb(cols[1], "continue_mode", "Al terminar:", c => c.ContinueMode.ToString(),
            ("None", "Nada (GO manual)"),
            ("AutoContinue", "Auto-continue (tras post-wait)"),
            ("AutoFollow", "Auto-follow (al acabar la acción)"));
        TimeF(cols[1], "pre_wait", "Pre-wait:", c => c.PreWait);
        TimeF(cols[1], "duration", "Duración:", c => c.Duration);
        TimeF(cols[1], "post_wait", "Post-wait:", c => c.PostWait);

        Header(cols[2], "DESTINO");
        FileRow(cols[2]);

        // Solo cues que apuntan a otro cue
        var targetRow = Conditional(cols[2], c => c.TargetsOther());
        TargetCombo(targetRow);
        var fadeStopRow = Conditional(cols[2], c => c.Type == CueType.Fade);
        Chk(fadeStopRow, "fade_stop_on_done", "Detener el objetivo al llegar a 0",
            c => c.FadeStopOnDone);

        // Solo grupos
        var groupRow = Conditional(cols[2], c => c.Type == CueType.Group);
        CmbInline(groupRow, "group_mode", "Modo de grupo:", c => c.GroupMode.ToString(),
            ("Enter", "ENTER — el playhead entra"),
            ("Timeline", "TIMELINE — todos a la vez"),
            ("GoNext", "GO NEXT — primer hijo"),
            ("Random", "RANDOM — aleatorio round-robin"));

        var stopOthersRow = ChkRow(cols[2]);
        Chk(stopOthersRow, "stop_others", "Detener los demás al empezar (GO per-cue)",
            c => c.StopOthers);

        return scroll;
    }


    private UIElement BuildTriggers()
    {
        var (scroll, panel) = Page();
        var cols = Columns(panel, 3);

        Header(cols[0], "DISPARO LOCAL");
        Txt(cols[0], "hotkey", "Hotkey (tecla):", c => c.Hotkey);
        Cmb(cols[0], "retrigger", "Segundo disparo:", c => c.Retrigger.ToString(),
            ("None", "Ignorar"),
            ("Panic", "Panic (fade y parar)"),
            ("Stop", "Parar"),
            ("HardStop", "Parada dura"),
            ("HardRestart", "Reiniciar"));

        Header(cols[0], "FUNDIR Y PARAR OTROS");
        var foRow = ChkRow(cols[0]);
        Chk(foRow, "fade_others", "Al dispararse, funde y para otros cues", c => c.FadeOthers);
        Num(cols[0], "fade_others_time", "Tiempo del fundido (s):", c => c.FadeOthersTime);
        Cmb(cols[0], "fade_others_scope", "Ámbito:", c => c.FadeOthersScope.ToString(),
            ("Peers", "PEERS — mismo nivel/grupo"),
            ("List", "LIST — toda la lista"),
            ("All", "ALL — todo"));

        Header(cols[1], "WALL-CLOCK (disparo por hora)");
        var wcRow = ChkRow(cols[1]);
        Chk(wcRow, "wallclock_enabled", "Disparar a una hora del día", c => c.WallclockEnabled);
        Txt(cols[1], "wallclock_time", "Hora (HH:MM:SS, 24h):", c => c.WallclockTime);
        Note(cols[1], "Se dispara cada día a esa hora\nmientras el show esté abierto.");

        Header(cols[2], "MIDI (backend en fase 6)");
        var midiRow = ChkRow(cols[2]);
        Chk(midiRow, "midi_enabled", "Disparo por MIDI", c => c.MidiEnabled);
        Cmb(cols[2], "midi_type", "Tipo de mensaje:", c => c.MidiType.ToString(),
            ("NoteOn", "Note On"), ("NoteOff", "Note Off"),
            ("ControlChange", "Control Change"), ("ProgramChange", "Program Change"));
        Int(cols[2], "midi_channel", "Canal:", c => c.MidiChannel);
        Int(cols[2], "midi_note", "Nota / CC:", c => c.MidiNote);
        Int(cols[2], "midi_velocity", "Velocity (-1 = cualquiera):", c => c.MidiVelocity);

        return scroll;
    }

    private UIElement BuildTimeLoops()
    {
        // Sin scroll: campos arriba y la barra de trim ESTIRADA al alto restante
        var root = new DockPanel { Margin = new Thickness(10, 4, 10, 10) };

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var cols = Columns(top, 2);

        Header(cols[0], "REPRODUCCIÓN");
        var loopRow = ChkRow(cols[0]);
        Chk(loopRow, "loop", "Loop infinito (hasta STOP)", c => c.Loop);
        Int(cols[0], "play_count", "Repeticiones (play count):", c => c.PlayCount);
        Num(cols[0], "rate", "Velocidad (1.0 = normal):", c => c.Rate);
        var pitchRow = ChkRow(cols[0]);
        Chk(pitchRow, "preserve_pitch", "Mantener tono al cambiar velocidad",
            c => c.PreservePitch);

        Header(cols[1], "FUNDIDOS Y RECORTE");
        TimeF(cols[1], "fade_in", "Fade in:", c => c.FadeIn);
        TimeF(cols[1], "fade_out", "Fade out:", c => c.FadeOut);
        TimeF(cols[1], "trim_start", "Trim: inicio:", c => c.TrimStart);
        TimeF(cols[1], "trim_end", "Trim: fin (0 = final):", c => c.TrimEnd);

        Header(top, "BARRA DE TRIM — arrastra los manejadores");
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

        var controls = new StackPanel { Margin = new Thickness(10, 6, 14, 10), Width = 340 };
        var scrollLeft = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = controls,
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
        root.Children.Add(_geoCanvas);

        Header(controls, "MODO");
        _rbFull = new RadioButton
        {
            Content = "Superficie completa — llena el canvas",
            GroupName = "geo_mode_grp", Margin = new Thickness(0, 2, 0, 2),
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
        };
        _rbCustom = new RadioButton
        {
            Content = "Posición manual — escala, posición y giro",
            GroupName = "geo_mode_grp", Margin = new Thickness(0, 2, 0, 2),
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
        };
        _rbFull.Checked += (_, _) => OnGeoModeChanged(false);
        _rbCustom.Checked += (_, _) => OnGeoModeChanged(true);
        controls.Children.Add(_rbFull);
        controls.Children.Add(_rbCustom);

        // ── Panel FULL ──────────────────────────────────────────────────
        _fullPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(_fullPanel);
        var aspectRow = ChkRow(_fullPanel);
        Chk(aspectRow, "preserve_aspect", "Mantener relación de aspecto",
            c => c.PreserveAspect);
        SliderF(_fullPanel, "scale", "Escala (%):", 10, 400, c => c.Scale, "0");

        // ── Panel CUSTOM ────────────────────────────────────────────────
        _customPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(_customPanel);
        SliderF(_customPanel, "scale_x", "Escala (×):", 0.1, 4.0, c => c.ScaleX, "0.00");
        SliderF(_customPanel, "translate_x", "Posición X (px):", -960, 960, c => c.TranslateX, "0");
        SliderF(_customPanel, "translate_y", "Posición Y (px):", -540, 540, c => c.TranslateY, "0");

        var rotRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        rotRow.Children.Add(new TextBlock
        {
            Text = "Rotación:", Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
        });
        foreach (int deg in new[] { 0, 90, 180, 270 })
        {
            var tb = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = $"{deg}°", Width = 46, Tag = deg,
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x3c, 0x3c, 0x3c)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
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
        var btnCenter = new Button { Content = "⌖ Centrar", Padding = new Thickness(10, 4, 10, 4) };
        var btnReset = new Button
        { Content = "↺ Restablecer", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
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

        Header(controls, "COMPOSITOR (fase 3b)");
        var compRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        compRow.Children.Add(new TextBlock
        {
            Text = "Layer:", VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
            Margin = new Thickness(0, 0, 4, 0),
        });
        var layerBox = MakeTextBox(64);
        WireTextCommit(layerBox, "layer", () => layerBox.Text);
        compRow.Children.Add(layerBox);
        compRow.Children.Add(new TextBlock
        {
            Text = "Opacidad %:", VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
            Margin = new Thickness(12, 0, 4, 0),
        });
        var opacityBox = MakeTextBox(48);
        WireTextCommit(opacityBox, "opacity", () =>
            double.TryParse(opacityBox.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double ov)
                ? ov : (object?)null);
        compRow.Children.Add(opacityBox);
        controls.Children.Add(compRow);
        _loaders["layer"] = c => layerBox.Text = c.Layer;
        _loaders["opacity"] = c => opacityBox.Text = c.Opacity.ToString("0.#",
            System.Globalization.CultureInfo.InvariantCulture);

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
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var slider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = 100, Height = 170,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        faderCol.Children.Add(slider);
        mixer.Children.Add(faderCol);

        var vuCol = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        vuCol.Children.Add(new TextBlock
        {
            Text = "VU",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _levelVu.Margin = new Thickness(0, 4, 0, 0);
        vuCol.Children.Add(_levelVu);
        mixer.Children.Add(vuCol);

        // Columna de información y ajustes a la derecha del fader
        var infoCol = new StackPanel { Margin = new Thickness(26, 4, 0, 0) };
        mixer.Children.Add(infoCol);

        var label = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2e, 0xcc, 0x71)),
        };
        infoCol.Children.Add(label);
        Note(infoCol, "Se aplica EN VIVO si el cue está sonando.\n" +
                      "El VU muestra el nivel real de ESTE cue.");

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
        // La salida por cue y la audición van en la columna de información
        panel = infoCol;

        // ── Salida de audio por cue ─────────────────────────────────────
        Header(panel, "SALIDA DE AUDIO DE ESTE CUE");
        var outRow = Conditional(panel, c => c.Type == CueType.Audio);
        outRow.Children.Add(new TextBlock
        {
            Text = "Dispositivo:", Width = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
        });
        var outCombo = new ComboBox { MinWidth = 260 };
        outCombo.SelectionChanged += (_, _) =>
        {
            if ((outCombo.SelectedItem as ComboBoxItem)?.Tag is string dev)
                Commit("audio_output", dev);
        };
        outRow.Children.Add(outCombo);
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
        var audRow = Conditional(panel, c => c.Type == CueType.Audio);
        var audBtn = new Button
        {
            Content = "🎧 Audición (cascos)",
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 5, 12, 5),
        };
        audBtn.Click += (_, _) => AuditionRequested?.Invoke();
        audRow.Children.Add(audBtn);
        Note(panel, "Reproduce este cue por el dispositivo de audición " +
                    "(Settings → Audio) sin pasar por la sala. Pulsa otra vez para parar.");

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

    private static void Header(StackPanel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 10, 0, 2),
        });

    private static void Note(StackPanel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontStyle = FontStyles.Italic,
            Foreground = Brushes.Gray,
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
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
        var row = LabeledRow(panel, "Color:");
        foreach (var (name, hex) in CueConstants.CueColors)
        {
            var btn = new Button
            {
                Width = 26, Height = 20, Margin = new Thickness(2, 0, 2, 0),
                Background = hex.Length > 0
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
                    : Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                Content = hex.Length == 0 ? "∅" : "",
                Foreground = Brushes.Gray,
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
            Text = "Archivo:", Width = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
        });
        var tb = MakeTextBox(300);
        var browse = new Button { Content = "…", Margin = new Thickness(4, 0, 0, 0) };
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
            Text = "Cue objetivo:", Width = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
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
            Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
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
