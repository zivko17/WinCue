// =============================================================================
// InspectorPanel.cs — Inspector de cue, PRIMERA PASADA (WinUI 3, declarativo).
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
// ALCANCE de esta pasada (ver el encargo — "hitos-0-1", inspector de cues):
// las propiedades que gobiernan un show.
//   - Identidad: número, nombre, archivo (el tipo se ve en la cabecera, como
//     en WPF: WinCue tampoco deja editar el tipo desde el inspector).
//   - Temporización: pre-wait, duración, post-wait, encadenado (continue_mode).
//   - Fundidos: fade in, fade out.
//   - Reproducción: loop, play count, trim (inicio/fin), volumen.
//
// FUERA de esta pasada, a propósito: el rack de audio (EQ, compresor, VU por
// cue) y los disparadores remotos (hotkey/MIDI/wall-clock). Eso pide
// controles pintados a mano que Secuence no tiene todavía — entran en una
// pasada posterior, cuando esos controles existan.
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
using Windows.System;
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

    private Cue? _cue;
    private bool _loading;   // suprime commits al repoblar (igual que en WPF)
    private readonly WaveformCanvas _wave = new();
    private string _waveLoadedPath = "";

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

    private readonly StackPanel _root = new() { Margin = new Thickness(0, 0, 0, 16) };

    // Registro clave → escribir valor al control (para poblar al seleccionar)
    private readonly Dictionary<string, Action<Cue>> _loaders = new();
    // Filas con visibilidad condicionada por tipo de cue (aquí, solo "Archivo")
    private readonly List<(FrameworkElement El, Func<Cue, bool> VisibleIf)> _conditional = new();

    private const double VolPosSteps = 1000.0;

    public InspectorPanel()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _root,
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_header, 0);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(_header);
        layout.Children.Add(scroll);
        Content = layout;

        Header(Loc.T("IDENTIDAD", "IDENTITY"));
        Txt("number", Loc.T("Número:", "Number:"), c => c.Number);
        Txt("name", Loc.T("Nombre:", "Name:"), c => c.Name);
        FileRow();

        Header(Loc.T("TEMPORIZACIÓN", "TIMING"));
        Cmb("continue_mode", Loc.T("Al terminar:", "On finish:"), c => c.ContinueMode.ToString(),
            ("None", Loc.T("Nada (GO manual)", "None (manual GO)")),
            ("AutoContinue", Loc.T("Auto-continue (tras post-wait)", "Auto-continue (after post-wait)")),
            ("AutoFollow", Loc.T("Auto-follow (al acabar la acción)", "Auto-follow (when the action ends)")));
        TimeF("pre_wait", "Pre-wait:", c => c.PreWait);
        TimeF("duration", Loc.T("Duración:", "Duration:"), c => c.Duration);
        TimeF("post_wait", "Post-wait:", c => c.PostWait);

        Header(Loc.T("FUNDIDOS", "FADES"));
        TimeF("fade_in", "Fade in:", c => c.FadeIn);
        TimeF("fade_out", "Fade out:", c => c.FadeOut);

        Header(Loc.T("REPRODUCCIÓN", "PLAYBACK"));
        Chk("loop", Loc.T("Loop infinito (hasta STOP)", "Infinite loop (until STOP)"), c => c.Loop);
        Int("play_count", Loc.T("Repeticiones (play count):", "Repeats (play count):"), c => c.PlayCount);
        TimeF("trim_start", Loc.T("Trim: inicio:", "Trim: start:"), c => c.TrimStart);
        TimeF("trim_end", Loc.T("Trim: fin (0 = final):", "Trim: end (0 = end):"), c => c.TrimEnd);
        WaveformRow();
        VolumeField();

        GeometrySection();

        IsEnabled = false;   // sin cue seleccionado
        _header.Text = Loc.T("— sin cue seleccionado —", "— no cue selected —");
    }

    /// <summary>Muestra (y edita) el cue dado. null → inspector deshabilitado.
    /// Mismo contrato que WinCue.App.InspectorPanel.SetCue, sin el parámetro
    /// allCues (target_id queda fuera de esta pasada — ver el encargo).</summary>
    public void SetCue(Cue? cue)
    {
        _cue = cue;
        IsEnabled = cue is not null;
        if (cue is null)
        {
            _header.Text = Loc.T("— sin cue seleccionado —", "— no cue selected —");
            _wave.Clear();
            _waveLoadedPath = "";
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
    }

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
        var block = new StackPanel { Margin = new Thickness(10, 12, 10, 4) };
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

    private StackPanel LabeledRow(string label)
    {
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 10, 3) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 150,
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

    private void Chk(string key, string label, Func<Cue, bool> read)
    {
        var cb = new CheckBox
        {
            Content = label,
            Foreground = Theme.TextPrimaryBrush,
            Margin = new Thickness(10, 3, 10, 3),
        };
        cb.Checked += (_, _) => Commit(key, true);
        cb.Unchecked += (_, _) => Commit(key, false);
        _root.Children.Add(cb);
        _loaders[key] = c => cb.IsChecked = read(c);
    }

    private void Cmb(string key, string label, Func<Cue, string> read,
                     params (string Value, string Label)[] items)
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
}
