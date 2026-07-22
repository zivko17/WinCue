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
        var btnRefresh = new Button { Content = Loc.T("↻ Refrescar dispositivos", "↻ Refresh devices") };
        btnRefresh.Click += (_, _) => RefreshAudioDevices();
        refreshRow.Children.Add(btnRefresh);
        Combo("audio_driver", dev, Loc.T("Driver:", "Driver:"),
            ("wasapi", "WASAPI"), ("asio", "ASIO"), ("directsound", "DirectSound"));

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
        _oscTestResult = new TextBlock { Foreground = System.Windows.Media.Brushes.Gray,
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
        var btnLearnGo = new Button { Content = "🎹 Learn GO (5 s)" };
        var btnLearnStop = new Button { Content = "🎹 Learn STOP (5 s)" };
        btnLearnGo.Click += (_, _) => MidiLearn(isGo: true);
        btnLearnStop.Click += (_, _) => MidiLearn(isGo: false);
        _midiLearnStatus = new TextBlock { Foreground = System.Windows.Media.Brushes.Gray,
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
            Foreground = System.Windows.Media.Brushes.Gray,
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
        var btn = new Button { Content = "…" };
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
        _audioDevices.Items.Clear();
        _audioDevices.Items.Add(new ComboBoxItem { Content = Loc.T("(dispositivo por defecto)", "(default device)"), Tag = "" });
        foreach (var dev in AudioEngine.ListDevices())
            _audioDevices.Items.Add(new ComboBoxItem
            {
                Content = dev.IsDefault ? $"{dev.Name}  ★" : dev.Name,
                Tag = dev.Name,
            });
        foreach (string asio in AudioEngine.ListAsioDrivers())
            _audioDevices.Items.Add(new ComboBoxItem { Content = $"[ASIO] {asio}", Tag = asio });
        _audioDevices.SelectedIndex = 0;
    }

    private void RefreshMidiDevices()
    {
        _midiDevices.Items.Clear();
        try
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                string name = MidiIn.DeviceInfo(i).ProductName;
                _midiDevices.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            }
        }
        catch (Exception) { /* subsistema MIDI no disponible */ }
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
                    return $"✓ Respuesta de {remote}";
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
        int devIndex = -1;
        try
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
                if (MidiIn.DeviceInfo(i).ProductName == wanted)
                { devIndex = i; break; }
        }
        catch (Exception) { }
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
                            $"✓ Nota {note.NoteNumber} ({MidiNoteName(note.NoteNumber)})";
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
        foreach (var (key, field) in _fields)
        {
            var node = _sm.GetAll()[key];
            // OJO: GetValue<double>() LANZA si el JsonValue está respaldado por
            // un int de C# (defaults aún no guardados en settings.json);
            // Deserialize<> convierte con independencia del respaldo.
            object? value = node switch
            {
                null => null,
                _ => node.GetValueKind() switch
                {
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Number =>
                        System.Text.Json.JsonSerializer.Deserialize<double>(node) is double d
                            && d % 1 == 0
                        ? (object)(int)d : System.Text.Json.JsonSerializer.Deserialize<double>(node),
                    _ => node.ToString(),
                },
            };
            field.Set(value);
        }
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
