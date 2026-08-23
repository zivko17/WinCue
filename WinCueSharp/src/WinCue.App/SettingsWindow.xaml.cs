// =============================================================================
// SettingsWindow.xaml.cs — Diálogo de configuración (port de settings_dialog.py)
// =============================================================================
// Cinco pestañas: Audio, Vídeo, Red/Control, Interfaz, Rutas. Lee y escribe a
// través de SettingsManager (mismo settings.json que la versión Python).
// El formulario se construye en código con helpers (equivalentes a los
// _spin_set/_chk_set de Python): cada control se declara en una línea con su
// clave de settings, y Load/Save recorren el registro de controles.
//
// Al guardar se invoca OnSaved para que la app reconfigure los motores en
// caliente (dispositivo/driver de audio, atajos de teclado).
// =============================================================================

using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using NAudio.Midi;
using WinCue.Audio;
using WinCue.Core;
using WinCue.Presentation;
using WinCue.Remote;

namespace WinCue.App;

// CA1001: ventana WPF — el MidiIn del MIDI Learn (_learnInput) se libera
// deterministamente en StopMidiLearn(), llamado desde OnClosed.
#pragma warning disable CA1001
public partial class SettingsWindow : Window
{
#pragma warning restore CA1001
    private readonly SettingsManager _sm;

    /// <summary>Invocado tras guardar, con el dict de cambios aplicados.</summary>
    public event Action? Saved;

    // Registro clave → (leer valor del control, escribir valor al control)
    private readonly Dictionary<string, (Func<object?> Get, Action<object?> Set)> _fields = new();

    private ComboBox _audioDevices = null!;
    private ComboBox _midiDevices = null!;
    private TextBlock _oscTestResult = null!;
    private TextBlock _midiLearnStatus = null!;
    private (TextBox Go, TextBox Stop)? _midiNotes;
    private int _initialSampleRate;

    public SettingsWindow(SettingsManager settings)
    {
        InitializeComponent();
        _sm = settings;
        BuildPages();
        LoadValues();
        _initialSampleRate = _sm.Get("audio_sample_rate", 48000);
    }

    // ==================================================================
    // Construcción de pestañas
    // ==================================================================

    private void BuildPages()
    {
        Tabs.Items.Add(MakeTab("Audio", BuildAudio()));
        Tabs.Items.Add(MakeTab(Loc.T("Vídeo", "Video"), BuildVideo()));
        Tabs.Items.Add(MakeTab(Loc.T("Red / Control", "Network / Control"), BuildNetwork()));
        Tabs.Items.Add(MakeTab(Loc.T("Interfaz", "Interface"), BuildInterface()));
        Tabs.Items.Add(MakeTab(Loc.T("Rutas", "Paths"), BuildPaths()));
        Tabs.Items.Add(MakeTab($"{Icons.Info} {Loc.T("Acerca de", "About")}", BuildAbout()));
    }

    /// <summary>Acerca de: versión + atribuciones de terceros (antes era un
    /// MessageBox del botón ⓘ de la barra; Andrés lo quiso dentro de Settings).
    /// THIRD-PARTY-NOTICES.md se distribuye junto al exe (publish.ps1).</summary>
    private static UIElement BuildAbout()
    {
        var root = new StackPanel();
        string ver = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";

        var app = Group(root, "WINCUE");
        app.Children.Add(new TextBlock
        {
            Text = $"WinCue {ver}",
            FontSize = 20, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 2, 0, 4),
        });
        app.Children.Add(new TextBlock
        {
            Text = Loc.T("Reproductor de cues de audio y vídeo para directo.",
                         "Audio and video cue player for live shows."),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var lic = Group(root, Loc.T("LICENCIAS DE TERCEROS", "THIRD-PARTY LICENSES"));
        Note(lic, Loc.T(
            "Este software incluye componentes de terceros (NAudio, libmpv, runtime de .NET); las atribuciones y licencias completas están en THIRD-PARTY-NOTICES.md, junto al ejecutable.",
            "This software includes third-party components (NAudio, libmpv, .NET runtime); full attributions and licenses are in THIRD-PARTY-NOTICES.md, next to the executable."));
        var btnRow = Row(lic, "");
        var btn = new Button
        {
            Content = $"{Icons.Document} {Loc.T("Abrir atribuciones", "Open attributions")}",
            Padding = new Thickness(12, 5, 12, 5),
        };
        var status = new TextBlock
        {
            Foreground = Theme.TextNoteBrush,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        btn.Click += (_, _) =>
        {
            string notices = System.IO.Path.Combine(AppContext.BaseDirectory,
                "THIRD-PARTY-NOTICES.md");
            if (System.IO.File.Exists(notices))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(notices) { UseShellExecute = true });
            else
                status.Text = Loc.T(
                    "THIRD-PARTY-NOTICES.md no está junto al ejecutable (se incluye al publicar con publish.ps1).",
                    "THIRD-PARTY-NOTICES.md is not next to the executable (it's included when publishing with publish.ps1).");
        };
        btnRow.Children.Add(btn);
        lic.Children.Add(status);

        return root;
    }

    private static TabItem MakeTab(string title, UIElement content) => new()
    {
        Header = title,
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Padding = new Thickness(12), Child = content },
        },
    };

    private UIElement BuildAudio()
    {
        var root = new StackPanel();

        var dev = Group(root, Loc.T("DISPOSITIVO DE SALIDA", "OUTPUT DEVICE"));
        _audioDevices = Combo("audio_device_name", dev, Loc.T("Dispositivo:", "Device:"));
        RefreshAudioDevices();
        var refreshRow = Row(dev, "");
        var btnRefresh = new Button { Content = $"{Icons.Refresh} {Loc.T("Refrescar dispositivos", "Refresh devices")}" };
        btnRefresh.Click += (_, _) =>
        {
            AudioEngine.RefreshAsioDrivers();   // la lista ASIO está cacheada
            RefreshAudioDevices();
        };
        refreshRow.Children.Add(btnRefresh);
        // DirectSound RETIRADO: nunca estuvo implementado (acababa en WASAPI) y
        // en Windows moderno se emula sobre WASAPI. Un desplegable que ofrece
        // algo que no hace es peor que uno corto. Ver SettingsManager.Migrate.
        Combo("audio_driver", dev, Loc.T("Driver:", "Driver:"),
            ("wasapi", "WASAPI"), ("asio", "ASIO"));
        Note(dev, Loc.T("Con driver ASIO elige arriba una entrada «[ASIO] …»: ese es el driver que se abre. Si no eliges ninguna se prueban los instalados por orden.",
                  "With the ASIO driver pick an «[ASIO] …» entry above: that's the driver that gets opened. If you pick none, the installed ones are tried in order."));

        var q = Group(root, Loc.T("CALIDAD", "QUALITY"));
        Combo("audio_sample_rate", q, Loc.T("Frecuencia de muestreo:", "Sample rate:"),
            (44100, "44100 Hz"), (48000, "48000 Hz"), (96000, "96000 Hz"));
        Combo("audio_buffer_size", q, Loc.T("Tamaño de buffer:", "Buffer size:"),
            (128, "128 samples"), (256, "256"), (512, "512"), (1024, "1024"), (2048, "2048"));
        Note(q, Loc.T("El cambio de frecuencia de muestreo se aplica al reiniciar la app.", "Sample-rate change applies after restarting the app."));

        var vol = Group(root, Loc.T("VOLUMEN", "VOLUME"));
        Spin("master_volume", vol, Loc.T("Volumen master (%):", "Master volume (%):"), 0, 100);

        var aud = Group(root, Loc.T("AUDICIÓN (PREVIEW LOCAL)", "AUDITION (LOCAL PREVIEW)"));
        var audCombo = Combo("audition_device_name", aud, Loc.T("Dispositivo de audición:", "Audition device:"));
        audCombo.Items.Add(new ComboBoxItem { Content = Loc.T("(salida principal)", "(main output)"), Tag = "" });
        foreach (var audDev in AudioEngine.ListDevices())
            audCombo.Items.Add(new ComboBoxItem { Content = audDev.Name, Tag = audDev.Name });
        Note(aud, Loc.T("Los cues se pre-escuchan por aquí (cascos) sin salir a la sala.", "Cues are pre-listened here (headphones) without going to the room."));

        return root;
    }

    private UIElement BuildVideo()
    {
        var root = new StackPanel();

        var scr = Group(root, Loc.T("SALIDAS DE VÍDEO (monitor por salida)", "VIDEO OUTPUTS (monitor per output)"));
        var screens = System.Windows.Forms.Screen.AllScreens
            .Select((s, i) => ((object)i,
                Loc.T($"Pantalla {i}{(s.Primary ? " (principal)" : "")} — {s.Bounds.Width}×{s.Bounds.Height}", $"Screen {i}{(s.Primary ? " (main)" : "")} — {s.Bounds.Width}×{s.Bounds.Height}")))
            .ToArray();
        // Salidas 2-4: opción "Deshabilitada" (−1) al principio, activa por
        // defecto → no aparecen en el botón de salidas hasta asignarles monitor.
        (object, string) off = ((object)(-1), Loc.T("Deshabilitada", "Disabled"));
        var screensOff = new[] { off }.Concat(screens).ToArray();
        Combo("video_output_screen", scr, Loc.T("Salida 1 (principal):", "Output 1 (main):"), screens);
        Combo("video_output_screen2", scr, Loc.T("Salida 2:", "Output 2:"), screensOff);
        Combo("video_output_screen3", scr, Loc.T("Salida 3:", "Output 3:"), screensOff);
        Combo("video_output_screen4", scr, Loc.T("Salida 4:", "Output 4:"), screensOff);
        Note(scr, Loc.T("Cada cue de vídeo elige su salida en el inspector (Geometry). Las ventanas de salida se abren solas al usarlas un cue. Una salida «Deshabilitada» no aparece en el botón de salidas.",
                  "Each video cue picks its output in the inspector (Geometry). Output windows open on their own when a cue uses them. A «Disabled» output doesn't appear in the outputs button."));
        Check("video_fullscreen_output", scr, Loc.T("Salidas a pantalla completa (blackout)", "Full-screen outputs (blackout)"));
        Note(scr, Loc.T("Desmárcalo solo para probar en ventanas con bordes.", "Uncheck only to test in bordered windows."));
        Spin("blackout_fade", scr, Loc.T("Fundido del blackout (s, tecla B):", "Blackout fade (s, key B):"), 0, 30);
        Check("video_compositor", scr, Loc.T("Compositor GPU (opacidad real entre capas)", "GPU compositor (real per-layer opacity)"));
        Note(scr, Loc.T("Las pistas se funden según su Opacidad % (Geometry). Requiere driver con interop OpenGL↔DirectX; si falla, cae al modo clásico.",
                  "Layers blend by their Opacity % (Geometry). Requires a driver with OpenGL↔DirectX interop; if it fails, it falls back to classic mode."));

        // ── CANVAS (estilo Watchout): recortes por salida ───────────────
        var canvas = Group(root, Loc.T("CANVAS (las salidas recortan un lienzo común)", "CANVAS (outputs crop a shared canvas)"));
        Check("video_canvas_mode", canvas,
            Loc.T("Modo canvas: los cues se colocan en el canvas y cada salida muestra su recorte",
            "Canvas mode: cues are placed on the canvas and each output shows its crop"));
        Note(canvas, Loc.T("Requiere el compositor GPU. Un vídeo puede cruzar varias salidas con sincronía perfecta (un solo render). En este modo la \"Salida\" del inspector se ignora: el vídeo vive en el canvas.",
                     "Requires the GPU compositor. A video can span several outputs with perfect sync (a single render). In this mode the inspector's \"Output\" is ignored: the video lives on the canvas."));
        var editor = new CanvasEditorControl { Margin = new Thickness(0, 6, 0, 0) };
        canvas.Children.Add(editor);
        _fields["surface_width"] = (
            () => editor.CanvasW,
            v => editor.CanvasW = v is null ? 1920 : Convert.ToInt32(v));
        _fields["surface_height"] = (
            () => editor.CanvasH,
            v => editor.CanvasH = v is null ? 1080 : Convert.ToInt32(v));
        for (int s = 0; s < 4; s++)
        {
            int idx = s;
            string regionKey = s == 0 ? "video_output_region" : $"video_output_region{s + 1}";
            _fields[regionKey] = (
                () => editor.GetRegion(idx),
                v => editor.SetRegion(idx, v?.ToString()));
            if (s > 0)
                _fields[$"video_output_enabled{s + 1}"] = (
                    () => editor.GetEnabled(idx),
                    v => editor.SetEnabled(idx, AsBool(v)));
        }

        var pb = Group(root, Loc.T("REPRODUCCIÓN", "PLAYBACK"));
        Spin("video_default_volume", pb, "Volumen por defecto (%):", 0, 100);
        Check("video_loop_default", pb, Loc.T("Loop por defecto en cues nuevos", "Loop by default on new cues"));
        Spin("video_audio_max_minutes", pb,
            Loc.T("Audio de vídeo por el rack nativo hasta (min, 0 = sin límite):", "Video audio through the native rack up to (min, 0 = no limit):"), 0, 240);
        Note(pb, Loc.T("El audio de los vídeos suena por el motor de audio (EQ, compresor, VU por cue) en STREAMING: RAM constante, sin límite de duración (0). Un valor > 0 lo limita (opt-out). Con el streaming desactivado (WINCUE_MIRROR_STREAMING=0) el audio se decodifica entero a RAM (~23 MB/min) y 0 vuelve a significar 20 min.",
                 "Video audio plays through the audio engine (EQ, compressor, per-cue VU) via STREAMING: constant RAM, no duration limit (0). A value > 0 caps it (opt-out). With streaming disabled (WINCUE_MIRROR_STREAMING=0) audio is fully decoded to RAM (~23 MB/min) and 0 falls back to 20 min."));

        // ── MOTOR ProRes PROPIO ─────────────────────────────────────────
        var pr = Group(root, Loc.T("MOTOR ProRes", "ProRes ENGINE"));
        Check("video_prores_engine", pr,
            Loc.T("Motor propio para ProRes (.mov)", "Native engine for ProRes (.mov)"));
        Note(pr, Loc.T("Los ProRes en .mov se reproducen con el motor de vídeo de WinCue en vez de con mpv: se decodifican UNA sola vez (salida y PGM comparten imagen) y su audio pasa por el rack completo. Desmárcalo si este equipo da problemas: entonces ProRes vuelve a ir por mpv exactamente como antes. En directo también se apaga con la variable WINCUE_PRORES_ENGINE=0.",
                 "ProRes files in .mov play through WinCue's own video engine instead of mpv: they are decoded ONCE (output and PGM share the image) and their audio goes through the full rack. Uncheck it if this machine misbehaves: ProRes then goes back through mpv exactly as before. During a show it can also be switched off with WINCUE_PRORES_ENGINE=0."));

        return root;
    }

    private UIElement BuildNetwork()
    {
        var root = new StackPanel();

        var osc = Group(root, Loc.T("OSC (Open Sound Control)", "OSC (Open Sound Control)"));
        Check("osc_enabled", osc, Loc.T("Habilitar OSC (backend en fase 6)", "Enable OSC (backend in phase 6)"));
        Spin("osc_listen_port", osc, Loc.T("Puerto de escucha:", "Listen port:"), 1, 65535);
        Text("osc_send_host", osc, Loc.T("Host de envío:", "Send host:"));
        Spin("osc_send_port", osc, Loc.T("Puerto de envío:", "Send port:"), 1, 65535);
        var testRow = Row(osc, "");
        var btnPing = new Button { Content = "Enviar ping de prueba" };
        btnPing.Click += OnOscTest;
        _oscTestResult = new TextBlock { Foreground = Theme.TextNoteBrush,
                                          Margin = new Thickness(8, 0, 0, 0) };
        testRow.Children.Add(btnPing);
        testRow.Children.Add(_oscTestResult);

        var midi = Group(root, "MIDI");
        Check("midi_enabled", midi, Loc.T("Habilitar disparo por MIDI (backend en fase 6)", "Enable MIDI triggering (backend in phase 6)"));
        _midiDevices = Combo("midi_device_name", midi, Loc.T("Dispositivo de entrada:", "Input device:"));
        RefreshMidiDevices();
        var goNote = Spin("midi_go_note", midi, Loc.T("Nota MIDI para GO:", "MIDI note for GO:"), 0, 127);
        var stopNote = Spin("midi_stop_note", midi, Loc.T("Nota MIDI para STOP:", "MIDI note for STOP:"), 0, 127);
        _midiNotes = (goNote, stopNote);
        var learnRow = Row(midi, "");
        var btnLearnGo = new Button { Content = $"{Icons.Midi} Learn GO (5 s)" };
        var btnLearnStop = new Button { Content = $"{Icons.Midi} Learn STOP (5 s)" };
        btnLearnGo.Click += (_, _) => MidiLearn(isGo: true);
        btnLearnStop.Click += (_, _) => MidiLearn(isGo: false);
        _midiLearnStatus = new TextBlock { Foreground = Theme.TextNoteBrush,
                                            Margin = new Thickness(8, 0, 0, 0) };
        learnRow.Children.Add(btnLearnGo);
        learnRow.Children.Add(btnLearnStop);
        learnRow.Children.Add(_midiLearnStatus);

        return root;
    }

    private UIElement BuildInterface()
    {
        var root = new StackPanel();

        var lang = Group(root, Loc.T("IDIOMA", "LANGUAGE"));
        Combo("ui_language", lang, Loc.T("Idioma de la interfaz:", "Interface language:"),
            ("en", "English"),
            ("es", "Español"));
        Note(lang, Loc.T(
            "El cambio de idioma se aplica al reiniciar la aplicación.",
            "The language change takes effect after restarting the app."));

        var go = Group(root, Loc.T("COMPORTAMIENTO DE GO", "GO BEHAVIOUR"));
        Combo("go_mode", go, Loc.T("Al disparar un media:", "When triggering media:"),
            ("concurrent", Loc.T("Concurrente (QLab): los audios se solapan", "Concurrent (QLab): audios overlap")),
            ("sequential", Loc.T("Secuencial: cada GO detiene los demás", "Sequential: each GO stops the others")),
            ("per_cue", Loc.T("Por cue: según la casilla \"detener los demás\"", "Per cue: according to the \"stop others\" checkbox")));
        Spin("go_min_interval_ms", go, Loc.T("Anti doble-GO (ms; 0 = desactivado):", "Anti double-GO (ms; 0 = off):"), 0, 2000);
        Spin("fade_all_seconds", go, Loc.T("Duración del FADE ALL (s):", "FADE ALL duration (s):"), 1, 60);

        var panic = Group(root, Loc.T("EMERGENCIA (PANIC)", "EMERGENCY (PANIC)"));
        Spin("panic_fade", panic, Loc.T("Fundido del PANIC (s):", "PANIC fade (s):"), 0, 10);
        Combo("panic_action", panic, Loc.T("Tras el fundido:", "After the fade:"),
            ("black", Loc.T("Negro (parada normal)", "Black (normal stop)")),
            ("image", Loc.T("Imagen de respaldo en las salidas", "Backup image on outputs")));
        PathField("panic_image_path", panic, Loc.T("Imagen de respaldo:", "Backup image:"), folder: false);
        Check("panic_double_hard", panic, Loc.T("Doble pulsación (<2 s) = corte duro inmediato", "Double press (<2 s) = immediate hard cut"));
        Note(panic, Loc.T("La imagen se quita sola al siguiente GO de vídeo. Atajos: clic derecho sobre el botón PANIC.",
                    "The image clears itself on the next video GO. Shortcuts: right-click the PANIC button."));

        var ui = Group(root, Loc.T("VISTA", "VIEW"));
        Check("show_latency_column", ui, Loc.T("Mostrar columna de latencia", "Show latency column"));
        Check("show_next_cue_label", ui, Loc.T("Mostrar etiqueta del siguiente cue", "Show next-cue label"));
        Check("confirm_last_cue", ui, Loc.T("Confirmar al llegar al último cue", "Confirm when reaching the last cue"));
        Spin("ui_font_scale", ui, Loc.T("Escala de fuente de la lista (%):", "List font scale (%):"), 80, 200);
        Note(ui, Loc.T("Agranda la tabla de cues y las tarjetas EN REPRODUCCIÓN (FOH a oscuras). Se aplica al guardar.",
                 "Enlarges the cue table and the PLAYING cards (dark FOH). Applied on save."));

        var kb = Group(root, Loc.T("ATAJOS DE TECLADO", "KEYBOARD SHORTCUTS"));
        Text("keyboard_go", kb, "GO:");
        Text("keyboard_stop", kb, Loc.T("Stop seleccionado:", "Stop selected:"));
        Text("keyboard_prev", kb, Loc.T("Cue anterior:", "Previous cue:"));
        Note(kb, Loc.T("Nombres de tecla WPF: Space, S, Left, F1… Se aplican al guardar.", "WPF key names: Space, S, Left, F1… Applied on save."));

        return root;
    }

    private UIElement BuildPaths()
    {
        var root = new StackPanel();
        var grp = Group(root, Loc.T("RUTAS POR DEFECTO", "DEFAULT PATHS"));
        PathField("default_show_path", grp, "Carpeta de shows:", folder: true);
        PathField("default_media_path", grp, "Carpeta de media:", folder: true);
        PathField("ffmpeg_path", grp, "Ejecutable de FFmpeg (vacío = PATH):", folder: false);
        return root;
    }

    // ==================================================================
    // Helpers de construcción (equivalentes a los helpers Python)
    // ==================================================================

    private static StackPanel Group(StackPanel parent, string title)
    {
        var inner = new StackPanel();
        parent.Children.Add(new GroupBox { Header = title, Content = inner });
        return inner;
    }

    private static StackPanel Row(StackPanel parent, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal,
                                   Margin = new Thickness(0, 2, 0, 2) };
        if (label.Length > 0)
            row.Children.Add(new TextBlock { Text = label, Width = 280 });
        parent.Children.Add(row);
        return row;
    }

    private static void Note(StackPanel parent, string text) =>
        parent.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Theme.TextNoteBrush,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        });

    private CheckBox Check(string key, StackPanel parent, string label)
    {
        var cb = new CheckBox { Content = label };
        parent.Children.Add(cb);
        _fields[key] = (() => cb.IsChecked == true, v => cb.IsChecked = AsBool(v));
        return cb;
    }

    /// <summary>Spin numérico (TextBox validado; WPF no trae SpinBox nativo).</summary>
    private TextBox Spin(string key, StackPanel parent, string label, int min, int max)
    {
        var row = Row(parent, label);
        var tb = new TextBox { Width = 90 };
        row.Children.Add(tb);
        _fields[key] = (
            () => int.TryParse(tb.Text, out int v) ? Math.Clamp(v, min, max) : min,
            v => tb.Text = Convert.ToInt32(v).ToString());
        return tb;
    }

    private TextBox Text(string key, StackPanel parent, string label)
    {
        var row = Row(parent, label);
        var tb = new TextBox { Width = 220 };
        row.Children.Add(tb);
        _fields[key] = (() => tb.Text, v => tb.Text = v?.ToString() ?? "");
        return tb;
    }

    /// <summary>Combo con pares (valor, etiqueta). Sin items → editable por código.</summary>
    private ComboBox Combo(string key, StackPanel parent, string label,
                           params (object Value, string Label)[] items)
    {
        var row = Row(parent, label);
        var combo = new ComboBox();
        foreach (var (value, text) in items)
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        row.Children.Add(combo);
        _fields[key] = (
            () => (combo.SelectedItem as ComboBoxItem)?.Tag,
            v =>
            {
                foreach (ComboBoxItem item in combo.Items)
                    if (Equals(item.Tag?.ToString(), v?.ToString()))
                    {
                        combo.SelectedItem = item;
                        return;
                    }
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            });
        return combo;
    }

    private void PathField(string key, StackPanel parent, string label, bool folder)
    {
        var row = Row(parent, label);
        var tb = new TextBox { Width = 260 };
        var btn = new Button { Content = Icons.Browse };
        btn.Click += (_, _) =>
        {
            if (folder)
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog();
                if (dlg.ShowDialog(this) == true) tb.Text = dlg.FolderName;
            }
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Ejecutable|*.exe|Todos|*.*" };
                if (dlg.ShowDialog(this) == true) tb.Text = dlg.FileName;
            }
        };
        row.Children.Add(tb);
        row.Children.Add(btn);
        _fields[key] = (() => tb.Text, v => tb.Text = v?.ToString() ?? "");
    }

    private static bool AsBool(object? v) => v switch
    {
        null => false,
        bool b => b,
        _ => bool.TryParse(v.ToString(), out bool r) && r,
    };

    // ==================================================================
    // Dispositivos
    // ==================================================================

    private void RefreshAudioDevices()
    {
        // Refrescar no puede PERDER la selección del operador (antes volvía
        // siempre a «por defecto» y se guardaba eso sin que se notara).
        string? selected = (_audioDevices.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _audioDevices.Items.Clear();
        _audioDevices.Items.Add(new ComboBoxItem { Content = Loc.T("(dispositivo por defecto)", "(default device)"), Tag = "" });
        foreach (var dev in AudioEngine.ListDevices())
            _audioDevices.Items.Add(new ComboBoxItem
            {
                Content = dev.IsDefault ? $"{dev.Name}  {Icons.Star}" : dev.Name,
                Tag = dev.Name,
            });
        foreach (string asio in AudioEngine.ListAsioDrivers())
            _audioDevices.Items.Add(new ComboBoxItem { Content = $"[ASIO] {asio}", Tag = asio });
        _audioDevices.SelectedIndex = 0;
        if (selected is { Length: > 0 })
            foreach (ComboBoxItem item in _audioDevices.Items)
                if (Equals(item.Tag?.ToString(), selected))
                {
                    _audioDevices.SelectedItem = item;
                    break;
                }
    }

    private void RefreshMidiDevices()
    {
        // Enumeración compartida con Secuence.App (WinCue.Remote.
        // RemoteInputService.ListDevices — antes vivía solo aquí por
        // accidente, igual que RemoteInputService antes de la Fase 6).
        _midiDevices.Items.Clear();
        foreach (var dev in RemoteInputService.ListDevices())
            _midiDevices.Items.Add(new ComboBoxItem { Content = dev.Name, Tag = dev.Name });
        if (_midiDevices.Items.Count == 0)
            _midiDevices.Items.Add(new ComboBoxItem
            { Content = "(sin dispositivos MIDI)", Tag = "" });
        _midiDevices.SelectedIndex = 0;
    }

    // ==================================================================
    // OSC test ping (port del _OscTester Python)
    // ==================================================================

    private async void OnOscTest(object sender, RoutedEventArgs e)
    {
        string host = _fields["osc_send_host"].Get()?.ToString() ?? "127.0.0.1";
        int port = Convert.ToInt32(_fields["osc_send_port"].Get() ?? 9000);
        _oscTestResult.Text = "Enviando…";
        try
        {
            string result = await Task.Run(() =>
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 1000;
                byte[] msg = BuildOscPing();
                udp.Send(msg, msg.Length, host, port);
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    udp.Receive(ref remote);
                    return $"{Icons.Check} Respuesta de {remote}";
                }
                catch (SocketException)
                {
                    return "Sin respuesta en 1 s  (ping enviado correctamente)";
                }
            });
            _oscTestResult.Text = result;
        }
        catch (Exception exc)
        {
            _oscTestResult.Text = $"Error: {exc.Message}";
        }
    }

    /// <summary>/test/ping OSC mínimo: address null-padded a 4 + type tag ",".</summary>
    private static byte[] BuildOscPing()
    {
        static byte[] Pad4(byte[] b)
        {
            int rem = b.Length % 4;
            return rem == 0 ? b : b.Concat(new byte[4 - rem]).ToArray();
        }
        byte[] addr = Pad4(System.Text.Encoding.ASCII.GetBytes("/test/ping\0"));
        byte[] tags = Pad4(System.Text.Encoding.ASCII.GetBytes(",\0"));
        return addr.Concat(tags).ToArray();
    }

    // ==================================================================
    // MIDI learn (port del _MidiLearnWorker Python)
    // ==================================================================

    private MidiIn? _learnInput;

    private void MidiLearn(bool isGo)
    {
        StopMidiLearn();
        string wanted = (_midiDevices.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        int devIndex = RemoteInputService.ListDevices()
            .FirstOrDefault(d => d.Name == wanted)?.Index ?? -1;
        if (devIndex < 0)
        {
            _midiLearnStatus.Text = "Sin dispositivo MIDI seleccionado";
            return;
        }

        _midiLearnStatus.Text = $"Toca una nota para {(isGo ? "GO" : "STOP")}…";
        try
        {
            _learnInput = new MidiIn(devIndex);
            _learnInput.MessageReceived += (_, ev) =>
            {
                if (ev.MidiEvent is NoteOnEvent { Velocity: > 0 } note)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var (goTb, stopTb) = _midiNotes!.Value;
                        (isGo ? goTb : stopTb).Text = note.NoteNumber.ToString();
                        _midiLearnStatus.Text =
                            $"{Icons.Check} Nota {note.NoteNumber} ({MidiNoteName(note.NoteNumber)})";
                        StopMidiLearn();
                    });
                }
            };
            _learnInput.Start();

            // Timeout de 5 s
            var t = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(5) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_learnInput is not null)
                {
                    StopMidiLearn();
                    _midiLearnStatus.Text = "Tiempo agotado (5 s)";
                }
            };
            t.Start();
        }
        catch (Exception exc)
        {
            _midiLearnStatus.Text = $"Error: {exc.Message}";
            StopMidiLearn();
        }
    }

    private void StopMidiLearn()
    {
        try { _learnInput?.Stop(); _learnInput?.Dispose(); } catch (Exception) { }
        _learnInput = null;
    }

    private static string MidiNoteName(int note)
    {
        string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        return $"{names[note % 12]}{note / 12 - 1}";
    }

    // ==================================================================
    // Cargar / guardar
    // ==================================================================

    private void LoadValues()
    {
        var all = _sm.GetAll();
        foreach (var (key, field) in _fields)
            // CoerceForField (WinCue.Core.SettingsManager): mismo saneado que
            // usa Secuence.App.SettingsPanel (WinUI 3) — extraído de aquí,
            // ver su comentario para el porqué exacto (GetValue<double> vs.
            // Deserialize<>).
            field.Set(SettingsManager.CoerceForField(all[key]));
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var updates = new Dictionary<string, object?>();
        foreach (var (key, field) in _fields)
            updates[key] = field.Get();
        _sm.SetMany(updates);

        if (Convert.ToInt32(updates["audio_sample_rate"]) != _initialSampleRate)
            MessageBox.Show(this,
                "La nueva frecuencia de muestreo se aplicará al reiniciar WinCue.",
                "Configuración", MessageBoxButton.OK, MessageBoxImage.Information);

        Saved?.Invoke();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "¿Restaurar TODOS los valores de fábrica?",
                "Configuración", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes)
        {
            _sm.ResetDefaults();
            LoadValues();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMidiLearn();
        base.OnClosed(e);
    }
}
