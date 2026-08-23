// =============================================================================
// SettingsPanel.cs — Panel de configuración (WinUI 3), primera pasada.
// =============================================================================
// Equivalente de src/WinCue.App/SettingsWindow.xaml.cs para esta interfaz, NO
// una copia (regla del proyecto: lo compartido sube a WinCue.Core/Controller/
// Audio, no se copia entre productos — y SettingsManager, AudioEngine y
// MainController YA son compartidos; este archivo solo es el FORMULARIO, que
// sí es de framework y por eso se reescribe).
//
// Construido en código, igual que InspectorPanel.cs/MixerPanel.cs (WinUI 3 no
// tiene aquí ningún control declarativo que ahorre nada): un registro
// Dictionary<string,(Get,Set)> por clave de settings, con helpers Group/Row/
// Combo/Spin/Check/PathField — mismo reparto que los helpers Python-mirror de
// WPF, con los tipos de Microsoft.UI.Xaml.Controls y, donde WPF necesitaba un
// TextBox validado a mano (Spin), el NumberBox nativo de WinUI 3 (con
// decimales de verdad: panic_fade/fade_all_seconds son double en
// SettingsManager.CreateDefaults, y aquí se guardan como tales — WPF los trata
// como enteros con Convert.ToInt32, una pérdida ya existente en WinCue que
// esta pasada no repite ni corrige allí — "WinCue no puede cambiar de
// comportamiento").
//
// SettingsPanel no sabe abrirse solo: MainWindow.xaml.cs.OnSettingsClick lo
// mete en un ContentDialog (WinUI 3 no tiene Window modal ligera como WPF) y
// es quien conecta el guardado con el resto de la app (motor de audio, mixer,
// salida de vídeo) — mismo reparto que WinCue.App.MainWindow.OnSettingsClick
// (dlg.Saved) frente a WinCue.App.SettingsWindow (el formulario).
//
// ALCANCE de esta pasada — CINCO pestañas (la quinta, Red/Control, llega en
// la tarea de disparo remoto: hasta entonces RemoteInputService/RemoteRouter
// ni siquiera estaban conectados en Secuence.App.xaml.cs, así que un
// desplegable de dispositivo MIDI no habría disparado nada — ver el informe
// de esa tarea). Cada campo incluido es uno que de verdad hace algo hoy en
// Secuence (ver el informe para el porqué exacto de cada exclusión):
//   - Vídeo:   monitor de salida (lo que obligaba a editar settings.json a
//     mano), motor ProRes propio, techo del audio espejo de vídeo.
//   - Audio:   dispositivo/driver de salida, frecuencia de muestreo, volumen
//     master, dispositivo de audición.
//   - Red/Control: OSC (puerto de escucha, host/puerto de envío, ping de
//     prueba) y MIDI (dispositivo de entrada, notas de GO/STOP, MIDI Learn) —
//     RemoteInputService (WinCue.Remote) ya está enchufado en App.xaml.cs;
//     Apply() se llama en caliente al guardar, igual que WinCue.
//   - Rutas:   solo ffmpeg_path (el único campo de la pestaña Rutas de WPF
//     que algo lee — ShowFileService.ResolveFfmpegPath).
//   - Interfaz: idioma (recién enchufado, ver App.xaml.cs), comportamiento de
//     GO y PANIC — todo MainController, el mismo motor que WinCue.
// FUERA a propósito, todavía: video_compositor/canvas — sin
// GetCompositorSurface conectado (SetGpuLayerSurfaceProvider nunca se llama
// en Secuence.App.xaml.cs), el interruptor no cambiaría nada visible.
// video_default_volume/video_loop_default/audio_buffer_size/
// default_show_path/default_media_path/show_latency_column/
// show_next_cue_label/confirm_last_cue/ui_font_scale/keyboard_* — ninguno de
// estos lo lee NADA fuera de la propia ventana de Ajustes de WPF (ni
// siquiera WinCue.App los aplica en otro sitio): son decorativos en los dos
// productos, y esta pasada no los añade aquí tampoco.
// =============================================================================

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Midi;
using WinCue.Audio;
using WinCue.Core;
using WinCue.Presentation;
using WinCue.Remote;

namespace Secuence.App;

// CA1001: el MidiIn del MIDI Learn (_learnInput) se libera deterministamente
// en StopMidiLearn(), llamado desde Unloaded (ver el constructor) — mismo
// motivo y mismo pragma que WinCue.App.SettingsWindow.
#pragma warning disable CA1001
public sealed class SettingsPanel : ContentControl
{
#pragma warning restore CA1001
    private readonly SettingsManager _sm;
    private readonly Dictionary<string, (Func<object?> Get, Action<object?> Set)> _fields = new();

    private ComboBox _audioDevices = null!;
    private ComboBox _midiDevices = null!;
    private TextBlock _oscTestResult = null!;
    private TextBlock _midiLearnStatus = null!;
    private (NumberBox Go, NumberBox Stop)? _midiNotes;

    /// <summary>Selector de archivo — MainWindow.BrowseFileAsync (necesita el
    /// hwnd propio de la ventana, que este panel no tiene por qué conocer;
    /// mismo reparto que InspectorPanel.BrowseFile).</summary>
    public Func<Task<string?>>? BrowseFile;

    /// <summary>audio_sample_rate al abrir — para que MainWindow sepa si hay
    /// que avisar de "se aplica al reiniciar" (mismo aviso que WPF).</summary>
    public int InitialSampleRate { get; private set; }

    public SettingsPanel(SettingsManager settings)
    {
        _sm = settings;

        var tabs = new TabView
        {
            IsAddTabButtonVisible = false,
            TabWidthMode = TabViewWidthMode.SizeToContent,
            Background = Theme.PanelBgBrush,
        };
        tabs.TabItems.Add(MakeTab(Loc.T("Vídeo", "Video"), BuildVideo()));
        tabs.TabItems.Add(MakeTab("Audio", BuildAudio()));
        tabs.TabItems.Add(MakeTab(Loc.T("Red / Control", "Network / Control"), BuildNetwork()));
        tabs.TabItems.Add(MakeTab(Loc.T("Rutas", "Paths"), BuildPaths()));
        tabs.TabItems.Add(MakeTab(Loc.T("Interfaz", "Interface"), BuildInterface()));

        Content = tabs;
        LoadValues();
        InitialSampleRate = _sm.Get("audio_sample_rate", 48000);

        // MIDI Learn deja un MidiIn abierto y un timeout de 5 s pendientes si
        // el operador cierra el diálogo (Cancelar/clic fuera) a mitad de
        // gesto — Unloaded es el equivalente en WinUI 3 de OnClosed en
        // WinCue.App.SettingsWindow (que llama a StopMidiLearn ahí mismo):
        // este ContentControl se desmonta cuando el ContentDialog que lo aloja
        // se cierra, con o sin Guardar.
        Unloaded += (_, _) => StopMidiLearn();
    }

    private static TabViewItem MakeTab(string title, UIElement content) => new()
    {
        Header = title,
        IsClosable = false,
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 12, 16, 12),
            Content = content,
        },
    };

    // ==================================================================
    // Vídeo — máxima prioridad del encargo: el monitor de salida.
    // ==================================================================

    private UIElement BuildVideo()
    {
        var root = new StackPanel();

        var scr = Group(root, Loc.T("SALIDA DE VÍDEO", "VIDEO OUTPUT"));
        Combo("video_output_screen", scr, Loc.T("Monitor de salida:", "Output monitor:"),
            BuildScreenItems());
        Note(scr, Loc.T(
            "La salida se abre a pantalla completa en este monitor la primera vez que un cue de vídeo la necesita.",
            "The output opens full-screen on this monitor the first time a video cue needs it."));

        var pr = Group(root, Loc.T("MOTOR ProRes", "ProRes ENGINE"));
        Check("video_prores_engine", pr,
            Loc.T("Motor propio para ProRes (.mov)", "Native engine for ProRes (.mov)"));
        Note(pr, Loc.T(
            "Desmárcalo si este equipo da problemas: los ProRes vuelven a ir por mpv, igual que en WinCue.",
            "Uncheck it if this machine misbehaves: ProRes goes back through mpv, same as in WinCue."));

        var pb = Group(root, Loc.T("REPRODUCCIÓN", "PLAYBACK"));
        SpinDouble("video_audio_max_minutes", pb,
            Loc.T("Audio de vídeo por el rack nativo hasta (min, 0 = sin límite):",
                  "Video audio through the native rack up to (min, 0 = no limit):"), 0, 240, 1);

        return root;
    }

    /// <summary>Mismo contrato de índices que VideoOutputWindow.
    /// ResolveDisplayArea: 0 = SIEMPRE la principal (DisplayArea.Primary,
    /// explícito, no por orden); i&gt;0 = la (i-1)-ésima NO-principal, en el
    /// orden que da DisplayArea.FindAll(). Construir aquí la MISMA
    /// enumeración con el MISMO orden es lo que hace que la opción elegida en
    /// Ajustes sea la pantalla que de verdad se enciende.</summary>
    private static (object Value, string Label)[] BuildScreenItems()
    {
        var primary = DisplayArea.Primary;
        var items = new List<(object, string)>
        {
            ((object)0, Loc.T(
                $"Pantalla 0 (principal) — {primary.OuterBounds.Width}×{primary.OuterBounds.Height}",
                $"Screen 0 (main) — {primary.OuterBounds.Width}×{primary.OuterBounds.Height}")),
        };
        List<DisplayArea> otras;
        try
        {
            otras = DisplayArea.FindAll()
                .Where(a => a.DisplayId.Value != primary.DisplayId.Value)
                .ToList();
        }
        catch (Exception) { otras = new List<DisplayArea>(); }
        for (int i = 0; i < otras.Count; i++)
            items.Add(((object)(i + 1), Loc.T(
                $"Pantalla {i + 1} — {otras[i].OuterBounds.Width}×{otras[i].OuterBounds.Height}",
                $"Screen {i + 1} — {otras[i].OuterBounds.Width}×{otras[i].OuterBounds.Height}")));
        return items.ToArray();
    }

    // ==================================================================
    // Audio
    // ==================================================================

    private UIElement BuildAudio()
    {
        var root = new StackPanel();

        var dev = Group(root, Loc.T("DISPOSITIVO DE SALIDA", "OUTPUT DEVICE"));
        _audioDevices = Combo("audio_device_name", dev, Loc.T("Dispositivo:", "Device:"));
        RefreshAudioDevices();
        var refreshRow = Row(dev, "");
        var btnRefresh = new Button
        {
            Content = $"{Icons.Refresh} {Loc.T("Refrescar dispositivos", "Refresh devices")}",
            FontFamily = IconFont.Font,
        };
        btnRefresh.Click += (_, _) =>
        {
            AudioEngine.RefreshAsioDrivers();
            RefreshAudioDevices();
        };
        refreshRow.Children.Add(btnRefresh);
        Combo("audio_driver", dev, Loc.T("Driver:", "Driver:"),
            ("wasapi", "WASAPI"), ("asio", "ASIO"));
        Note(dev, Loc.T(
            "Con driver ASIO elige arriba una entrada «[ASIO] …»: ese es el driver que se abre. Si no eliges ninguna se prueban los instalados por orden.",
            "With the ASIO driver pick an «[ASIO] …» entry above: that's the driver that gets opened. If you pick none, the installed ones are tried in order."));

        var q = Group(root, Loc.T("CALIDAD", "QUALITY"));
        Combo("audio_sample_rate", q, Loc.T("Frecuencia de muestreo:", "Sample rate:"),
            (44100, "44100 Hz"), (48000, "48000 Hz"), (96000, "96000 Hz"));
        Note(q, Loc.T("El cambio de frecuencia de muestreo se aplica al reiniciar la app.",
                      "Sample-rate change applies after restarting the app."));

        var vol = Group(root, Loc.T("VOLUMEN", "VOLUME"));
        SpinInt("master_volume", vol, Loc.T("Volumen master (%):", "Master volume (%):"), 0, 100);

        var aud = Group(root, Loc.T("AUDICIÓN (PREVIEW LOCAL)", "AUDITION (LOCAL PREVIEW)"));
        var audCombo = Combo("audition_device_name", aud, Loc.T("Dispositivo de audición:", "Audition device:"));
        audCombo.Items.Add(new ComboBoxItem { Content = Loc.T("(salida principal)", "(main output)"), Tag = "" });
        foreach (var audDev in AudioEngine.ListDevices())
            audCombo.Items.Add(new ComboBoxItem { Content = audDev.Name, Tag = audDev.Name });
        // Repoblado a mano: la entrada Combo() de más abajo ya registró el
        // campo con una lista vacía de items — aquí solo se añaden opciones,
        // el registro en _fields sigue sirviendo (lee/escribe por Tag).
        Note(aud, Loc.T("Los cues se pre-escuchan por aquí (cascos) sin salir a la sala.",
                        "Cues are pre-listened here (headphones) without going to the room."));

        return root;
    }

    private void RefreshAudioDevices()
    {
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
            foreach (var obj in _audioDevices.Items)
                if (obj is ComboBoxItem item && Equals(item.Tag?.ToString(), selected))
                {
                    _audioDevices.SelectedItem = item;
                    break;
                }
    }

    // ==================================================================
    // Red / Control — OSC + MIDI. Equivalente de
    // WinCue.App.SettingsWindow.BuildNetwork, no una copia: el formulario es
    // de framework (WinUI 3 aquí, WPF allá) y por eso se reescribe, igual que
    // el resto de esta clase — ver la cabecera del archivo. Lo que dispara de
    // verdad (RemoteInputService.ApplyOsc/ApplyMidi, WinCue.Remote) SÍ es
    // compartido, sin una línea copiada.
    // ==================================================================

    private UIElement BuildNetwork()
    {
        var root = new StackPanel();

        var osc = Group(root, "OSC (Open Sound Control)");
        Check("osc_enabled", osc, Loc.T("Habilitar OSC", "Enable OSC"));
        SpinInt("osc_listen_port", osc, Loc.T("Puerto de escucha:", "Listen port:"), 1, 65535);
        Text("osc_send_host", osc, Loc.T("Host de envío:", "Send host:"));
        SpinInt("osc_send_port", osc, Loc.T("Puerto de envío:", "Send port:"), 1, 65535);
        var testRow = Row(osc, "");
        var btnPing = new Button { Content = Loc.T("Enviar ping de prueba", "Send test ping") };
        btnPing.Click += OnOscTest;
        _oscTestResult = new TextBlock
        {
            Foreground = Theme.TextDimBrush,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        testRow.Children.Add(btnPing);
        testRow.Children.Add(_oscTestResult);
        Note(osc, Loc.T(
            "Guardar recarga el listener en caliente (RemoteInputService.Apply): no hace falta reiniciar Secuence tras cambiar el puerto.",
            "Saving reloads the listener on the fly (RemoteInputService.Apply): no need to restart Secuence after changing the port."));

        var midi = Group(root, "MIDI");
        Check("midi_enabled", midi, Loc.T("Habilitar disparo por MIDI", "Enable MIDI triggering"));
        _midiDevices = Combo("midi_device_name", midi, Loc.T("Dispositivo de entrada:", "Input device:"));
        RefreshMidiDevices();
        var goNote = SpinInt("midi_go_note", midi, Loc.T("Nota MIDI para GO:", "MIDI note for GO:"), 0, 127);
        var stopNote = SpinInt("midi_stop_note", midi, Loc.T("Nota MIDI para STOP:", "MIDI note for STOP:"), 0, 127);
        _midiNotes = (goNote, stopNote);
        var learnRow = Row(midi, "");
        var btnLearnGo = new Button
        {
            Content = $"{Icons.Midi} Learn GO (5 s)",
            FontFamily = IconFont.Font,
        };
        var btnLearnStop = new Button
        {
            Content = $"{Icons.Midi} Learn STOP (5 s)",
            FontFamily = IconFont.Font,
        };
        btnLearnGo.Click += (_, _) => MidiLearn(isGo: true);
        btnLearnStop.Click += (_, _) => MidiLearn(isGo: false);
        _midiLearnStatus = new TextBlock
        {
            Foreground = Theme.TextDimBrush,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        learnRow.Children.Add(btnLearnGo);
        learnRow.Children.Add(btnLearnStop);
        learnRow.Children.Add(_midiLearnStatus);

        return root;
    }

    /// <summary>Repuebla el desplegable de dispositivos MIDI IN —
    /// RemoteInputService.ListDevices (WinCue.Remote), compartida con
    /// WinCue.App.SettingsWindow: antes vivía solo allí por accidente.</summary>
    private void RefreshMidiDevices()
    {
        _midiDevices.Items.Clear();
        foreach (var dev in RemoteInputService.ListDevices())
            _midiDevices.Items.Add(new ComboBoxItem { Content = dev.Name, Tag = dev.Name });
        if (_midiDevices.Items.Count == 0)
            _midiDevices.Items.Add(new ComboBoxItem
            { Content = Loc.T("(sin dispositivos MIDI)", "(no MIDI devices)"), Tag = "" });
        _midiDevices.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------
    // OSC test ping — puerto del _OscTester Python / WinCue.App.SettingsWindow.OnOscTest
    // ------------------------------------------------------------------

    private async void OnOscTest(object sender, RoutedEventArgs e)
    {
        string host = _fields["osc_send_host"].Get()?.ToString() ?? "127.0.0.1";
        int port = Convert.ToInt32(_fields["osc_send_port"].Get() ?? 9000);
        _oscTestResult.Text = Loc.T("Enviando…", "Sending…");
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
                    return $"{Icons.Check} " + Loc.T($"Respuesta de {remote}", $"Response from {remote}");
                }
                catch (SocketException)
                {
                    return Loc.T("Sin respuesta en 1 s  (ping enviado correctamente)",
                                 "No response in 1 s  (ping sent successfully)");
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
        byte[] addr = Pad4(Encoding.ASCII.GetBytes("/test/ping\0"));
        byte[] tags = Pad4(Encoding.ASCII.GetBytes(",\0"));
        return addr.Concat(tags).ToArray();
    }

    // ------------------------------------------------------------------
    // MIDI learn — puerto del _MidiLearnWorker Python / WinCue.App.SettingsWindow.MidiLearn
    // ------------------------------------------------------------------

    private MidiIn? _learnInput;

    private void MidiLearn(bool isGo)
    {
        StopMidiLearn();
        string wanted = (_midiDevices.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        int devIndex = RemoteInputService.ListDevices()
            .FirstOrDefault(d => d.Name == wanted)?.Index ?? -1;
        if (devIndex < 0)
        {
            _midiLearnStatus.Text = Loc.T("Sin dispositivo MIDI seleccionado", "No MIDI device selected");
            return;
        }

        _midiLearnStatus.Text = Loc.T($"Toca una nota para {(isGo ? "GO" : "STOP")}…",
                                       $"Play a note for {(isGo ? "GO" : "STOP")}…");
        try
        {
            _learnInput = new MidiIn(devIndex);
            _learnInput.MessageReceived += (_, ev) =>
            {
                if (ev.MidiEvent is NoteOnEvent { Velocity: > 0 } note)
                {
                    // Llega en el hilo de callback de winmm → remitir al hilo de UI
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var (goNb, stopNb) = _midiNotes!.Value;
                        (isGo ? goNb : stopNb).Value = note.NoteNumber;
                        _midiLearnStatus.Text = $"{Icons.Check} " + Loc.T(
                            $"Nota {note.NoteNumber} ({MidiNoteName(note.NoteNumber)})",
                            $"Note {note.NoteNumber} ({MidiNoteName(note.NoteNumber)})");
                        StopMidiLearn();
                    });
                }
            };
            _learnInput.Start();

            // Timeout de 5 s — DispatcherQueue.CreateTimer necesita el control
            // ya en el árbol visual (mismo motivo que MixerPanel.StartGrTimer):
            // aquí es seguro porque el botón que llama a MidiLearn solo existe
            // dentro de un ContentDialog ya mostrado.
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromSeconds(5);
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_learnInput is not null)
                {
                    StopMidiLearn();
                    _midiLearnStatus.Text = Loc.T("Tiempo agotado (5 s)", "Timed out (5 s)");
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
    // Rutas — solo ffmpeg_path (ver la cabecera del archivo para el porqué
    // de dejar fuera default_show_path/default_media_path).
    // ==================================================================

    private UIElement BuildPaths()
    {
        var root = new StackPanel();
        var grp = Group(root, Loc.T("RUTAS", "PATHS"));
        PathField("ffmpeg_path", grp, Loc.T("Ejecutable de FFmpeg (vacío = PATH):", "FFmpeg executable (empty = PATH):"));
        Note(grp, Loc.T(
            "Usado al exportar/convertir vídeo (WinCue.Controller.ShowFileService). Vacío = se busca ffmpeg en el PATH del sistema.",
            "Used when exporting/converting video (WinCue.Controller.ShowFileService). Empty = looks for ffmpeg on the system PATH."));
        return root;
    }

    // ==================================================================
    // Interfaz — idioma + comportamiento de GO/PANIC (MainController, el
    // mismo motor que WinCue: estos campos SÍ hacen algo aquí hoy).
    // ==================================================================

    private UIElement BuildInterface()
    {
        var root = new StackPanel();

        var lang = Group(root, Loc.T("IDIOMA", "LANGUAGE"));
        Combo("ui_language", lang, Loc.T("Idioma de la interfaz:", "Interface language:"),
            ("en", "English"), ("es", "Español"));
        Note(lang, Loc.T("El cambio de idioma se aplica al reiniciar la aplicación.",
                         "The language change takes effect after restarting the app."));

        var go = Group(root, Loc.T("COMPORTAMIENTO DE GO", "GO BEHAVIOUR"));
        Combo("go_mode", go, Loc.T("Al disparar un media:", "When triggering media:"),
            ("concurrent", Loc.T("Concurrente (QLab): los audios se solapan", "Concurrent (QLab): audios overlap")),
            ("sequential", Loc.T("Secuencial: cada GO detiene los demás", "Sequential: each GO stops the others")),
            ("per_cue", Loc.T("Por cue: según la casilla \"detener los demás\"", "Per cue: according to the \"stop others\" checkbox")));
        SpinInt("go_min_interval_ms", go, Loc.T("Anti doble-GO (ms; 0 = desactivado):", "Anti double-GO (ms; 0 = off):"), 0, 2000);
        SpinDouble("fade_all_seconds", go, Loc.T("Duración del FADE ALL (s):", "FADE ALL duration (s):"), 1, 60, 0.5);

        var panic = Group(root, Loc.T("EMERGENCIA (PANIC)", "EMERGENCY (PANIC)"));
        SpinDouble("panic_fade", panic, Loc.T("Fundido del PANIC (s):", "PANIC fade (s):"), 0, 10, 0.5);
        Combo("panic_action", panic, Loc.T("Tras el fundido:", "After the fade:"),
            ("black", Loc.T("Negro (parada normal)", "Black (normal stop)")),
            ("image", Loc.T("Imagen de respaldo en las salidas", "Backup image on outputs")));
        PathField("panic_image_path", panic, Loc.T("Imagen de respaldo:", "Backup image:"));
        Check("panic_double_hard", panic, Loc.T("Doble pulsación (<2 s) = corte duro inmediato", "Double press (<2 s) = immediate hard cut"));

        return root;
    }

    // ==================================================================
    // Helpers de construcción
    // ==================================================================

    private static StackPanel Group(StackPanel parent, string title)
    {
        var inner = new StackPanel { Spacing = 2 };
        var header = new TextBlock
        {
            Text = title,
            Foreground = Theme.TextTertiaryBrush,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var outer = new StackPanel { Children = { header, inner } };
        var border = new Border
        {
            Background = Theme.PanelBgBrush,
            BorderBrush = Theme.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 10),
            Child = outer,
        };
        parent.Children.Add(border);
        return inner;
    }

    private static StackPanel Row(StackPanel parent, string label)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2),
            Spacing = 8,
        };
        if (label.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = label,
                Width = 260,
                Foreground = Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
        parent.Children.Add(row);
        return row;
    }

    private static void Note(StackPanel parent, string text) =>
        parent.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Theme.TextDimBrush,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        });

    private CheckBox Check(string key, StackPanel parent, string label)
    {
        var cb = new CheckBox { Content = label, Foreground = Theme.TextPrimaryBrush };
        parent.Children.Add(cb);
        _fields[key] = (() => cb.IsChecked == true, v => cb.IsChecked = AsBool(v));
        return cb;
    }

    /// <summary>Spin de un entero (redondea al cargar/guardar) — go_min_interval_ms,
    /// master_volume, video_audio_max_minutes: enteros de verdad en
    /// SettingsManager.CreateDefaults.</summary>
    private NumberBox SpinInt(string key, StackPanel parent, string label, int min, int max)
    {
        var row = Row(parent, label);
        var nb = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 110,
        };
        row.Children.Add(nb);
        _fields[key] = (
            () => (int)Math.Clamp(double.IsNaN(nb.Value) ? min : Math.Round(nb.Value), min, max),
            v => nb.Value = v is null ? min : Convert.ToDouble(v));
        return nb;
    }

    /// <summary>Spin con decimales — fade_all_seconds/panic_fade son double en
    /// SettingsManager.CreateDefaults (5.0/1.5): a diferencia del Spin de WPF
    /// (int.TryParse, trunca a entero), aquí el NumberBox nativo de WinUI 3
    /// guarda el valor real sin redondear.</summary>
    private NumberBox SpinDouble(string key, StackPanel parent, string label, double min, double max, double step)
    {
        var row = Row(parent, label);
        var nb = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 110,
        };
        row.Children.Add(nb);
        _fields[key] = (
            () => Math.Clamp(double.IsNaN(nb.Value) ? min : nb.Value, min, max),
            v => nb.Value = v is null ? min : Convert.ToDouble(v));
        return nb;
    }

    /// <summary>Texto libre plano — osc_send_host es el único campo de esta
    /// clase que no es ni ruta (PathField) ni numérico (Spin*): un host
    /// (nombre o IP) no tiene min/max ni selector de archivo.</summary>
    private TextBox Text(string key, StackPanel parent, string label)
    {
        var row = Row(parent, label);
        var tb = new TextBox { Width = 220 };
        row.Children.Add(tb);
        _fields[key] = (() => tb.Text, v => tb.Text = v?.ToString() ?? "");
        return tb;
    }

    /// <summary>Combo con pares (valor, etiqueta). Sin items → editable por
    /// código (audition_device_name, poblado tras esta llamada en BuildAudio).</summary>
    private ComboBox Combo(string key, StackPanel parent, string label,
                           params (object Value, string Label)[] items)
    {
        var row = Row(parent, label);
        var combo = new ComboBox { Width = 300 };
        foreach (var (value, text) in items)
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        row.Children.Add(combo);
        _fields[key] = (
            () => (combo.SelectedItem as ComboBoxItem)?.Tag,
            v =>
            {
                foreach (var obj in combo.Items)
                    if (obj is ComboBoxItem item && Equals(item.Tag?.ToString(), v?.ToString()))
                    {
                        combo.SelectedItem = item;
                        return;
                    }
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            });
        return combo;
    }

    private void PathField(string key, StackPanel parent, string label)
    {
        var row = Row(parent, label);
        var tb = new TextBox { Width = 300 };
        var btn = new Button { Content = Icons.Browse, FontFamily = IconFont.Font };
        btn.Click += async (_, _) =>
        {
            if (BrowseFile is null)
                return;
            string? picked = await BrowseFile();
            if (picked is not null)
                tb.Text = picked;
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
    // Cargar / guardar / restaurar
    // ==================================================================

    private void LoadValues()
    {
        var all = _sm.GetAll();
        foreach (var (key, field) in _fields)
            field.Set(SettingsManager.CoerceForField(all[key]));
    }

    /// <summary>Repuebla los campos con los valores de fábrica SIN guardar
    /// todavía — el operador puede seguir editando o cancelar sin perder el
    /// settings.json actual. Diferencia deliberada frente a WPF (donde
    /// «Restaurar valores de fábrica» guarda al instante): ContentDialog de
    /// WinUI 3 no admite un MessageBox de confirmación anidado sin salir del
    /// propio diálogo, así que el candado pasa a ser "hace falta pulsar
    /// Guardar" en vez de un ¿seguro? previo — ver el informe.</summary>
    public void LoadDefaults()
    {
        var defaults = SettingsManager.CreateDefaults();
        foreach (var (key, field) in _fields)
            field.Set(SettingsManager.CoerceForField(defaults[key]));
    }

    /// <summary>Valores actuales de todos los campos, para SettingsManager.SetMany.</summary>
    public Dictionary<string, object?> CollectValues()
    {
        var updates = new Dictionary<string, object?>();
        foreach (var (key, field) in _fields)
            updates[key] = field.Get();
        return updates;
    }
}
