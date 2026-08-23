// =============================================================================
// RemoteInputService.cs — Fase 6: disparo remoto por OSC y MIDI IN
// =============================================================================
// Escucha según settings (Red → OSC / MIDI) y remite cada mensaje al hilo de
// UI, donde MainController.OnOscMessage/OnMidiMessage hacen el ruteo real.
// Apply() se puede llamar en caliente al guardar la configuración: cierra los
// listeners viejos y abre los nuevos. Nada de aquí puede tirar el show: todos
// los errores acaban en el evento Status (barra de estado).
// =============================================================================

using System.Net;
using System.Net.Sockets;
using NAudio.Midi;
using WinCue.Controller;
using WinCue.Core;

namespace WinCue.Remote;

/// <summary>Un dispositivo MIDI IN del sistema — Index es el que espera
/// <see cref="NAudio.Midi.MidiIn"/> (constructor y DeviceInfo).</summary>
public sealed record MidiDeviceInfo(int Index, string Name);

public sealed class RemoteInputService : IDisposable
{
    private readonly MainController _controller;
    private readonly CueModel _model;
    private readonly SettingsManager _settings;
    private readonly Action<Action> _dispatch;

    private UdpClient? _osc;
    private UdpClient? _oscOut;                    // feedback → Companion
    private System.Threading.Timer? _feedback;
    private MidiIn? _midi;

    // Ensamblado de MIDI Timecode (8 quarter-frames por posición)
    private readonly int[] _mtc = new int[8];
    private int _mtcMask;

    /// <summary>Mensajes de estado ("OSC escuchando en 8000", errores…).</summary>
    public event Action<string>? Status;

    public RemoteInputService(MainController controller, CueModel model,
        SettingsManager settings, Action<Action> dispatch)
    {
        _controller = controller;
        _model = model;
        _settings = settings;
        _dispatch = dispatch;
    }

    /// <summary>(Re)configura los listeners según los settings actuales.</summary>
    public void Apply()
    {
        ApplyOsc();
        ApplyMidi();
    }

    // ------------------------------------------------------------------
    // OSC (UDP)
    // ------------------------------------------------------------------

    private void ApplyOsc()
    {
        _osc?.Dispose();
        _osc = null;
        _feedback?.Dispose();
        _feedback = null;
        _oscOut?.Dispose();
        _oscOut = null;
        if (!_settings.Get("osc_enabled", false))
            return;

        // Feedback de SALIDA (playhead/activos) hacia Companion/TouchOSC
        string sendHost = _settings.Get("osc_send_host", "");
        int sendPort = _settings.Get("osc_send_port", 9000);
        if (sendHost.Length > 0 && sendPort > 0)
        {
            try
            {
                var outClient = new UdpClient();
                outClient.Connect(sendHost, sendPort);
                _oscOut = outClient;
                _feedback = new System.Threading.Timer(
                    _ => _dispatch(SendFeedback), null, 500, 500);
            }
            catch (SocketException exc)
            {
                Status?.Invoke($"OSC out: {exc.SocketErrorCode}");
            }
        }

        int port = _settings.Get("osc_listen_port", 8000);
        try
        {
            // Asignar directo al campo: el dueño es este servicio y Dispose/
            // Apply lo liberan (CA2000 no rastrea campos)
            _osc = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException exc)
        {
            Status?.Invoke($"OSC: no se pudo abrir el puerto {port} ({exc.SocketErrorCode})");
            return;
        }
        Status?.Invoke($"OSC escuchando en el puerto {port}");
        _ = ReceiveLoop(_osc);
    }

    private async Task ReceiveLoop(UdpClient client)
    {
        while (true)
        {
            byte[] datagram;
            try
            {
                datagram = (await client.ReceiveAsync().ConfigureAwait(false)).Buffer;
            }
            catch (Exception exc) when (exc is ObjectDisposedException or SocketException)
            {
                return;   // listener cerrado (Apply/Dispose) o red caída
            }
            foreach (var (address, args) in OscPacket.Parse(datagram))
                _dispatch(() => _controller.OnOscMessage(address, args));
        }
    }

    /// <summary>Estado del show por OSC (2 Hz, hilo de UI): el playhead, los
    /// cues sonando y la pausa. Companion los usa para iluminar botones.</summary>
    private void SendFeedback()
    {
        var outClient = _oscOut;
        if (outClient is null)
            return;
        try
        {
            var standby = _model.GetCue(_controller.Playhead);
            Send(OscPacket.Build("/wincue/playhead",
                standby?.DisplayNumber ?? "-", standby?.Name ?? ""));

            var numbers = _controller.RunningCueIds
                .Select(id => _model.GetById(id)?.DisplayNumber)
                .Where(n => n is not null).ToList();
            Send(OscPacket.Build("/wincue/running",
                numbers.Count, string.Join(",", numbers)));
            Send(OscPacket.Build("/wincue/paused", _controller.IsPaused ? 1 : 0));
        }
        catch (Exception exc) when (exc is SocketException or ObjectDisposedException)
        {
            // Red caída / apagado: el feedback nunca tira el show
        }

        void Send(byte[] datagram) => outClient.Send(datagram, datagram.Length);
    }

    // ------------------------------------------------------------------
    // MIDI IN (NAudio)
    // ------------------------------------------------------------------

    private void ApplyMidi()
    {
        if (_midi is not null)
        {
            try { _midi.Stop(); _midi.Dispose(); } catch (Exception) { }
            _midi = null;
        }
        if (!_settings.Get("midi_enabled", false))
            return;

        int index = FindMidiDevice();
        if (index < 0)
        {
            Status?.Invoke("MIDI: dispositivo de entrada no encontrado");
            return;
        }
        try
        {
            _midi = new MidiIn(index);
            _midi.MessageReceived += OnMidiIn;
            // NAudio remite los mensajes que no sabe parsear por ErrorReceived:
            // los quarter-frames de MTC (0xF1) pueden llegar por aquí
            _midi.ErrorReceived += (_, e) =>
            {
                if ((e.RawMessage & 0xFF) == 0xF1)
                    OnMtcQuarterFrame((e.RawMessage >> 8) & 0x7F);
            };
            _midi.Start();
            Status?.Invoke($"MIDI IN activo: {MidiIn.DeviceInfo(index).ProductName}");
        }
        catch (Exception exc) when (exc is NAudio.MmException or ArgumentException)
        {
            Status?.Invoke($"MIDI: no se pudo abrir el dispositivo ({exc.Message})");
            _midi = null;
        }
    }

    private int FindMidiDevice()
    {
        string name = _settings.Get("midi_device_name", "");
        var devices = ListDevices();
        if (name.Length > 0)
        {
            var match = devices.FirstOrDefault(d => d.Name == name);
            if (match is not null)
                return match.Index;
        }
        int idx = _settings.Get("midi_device_index", -1);
        if (idx >= 0 && idx < devices.Count)
            return idx;
        return devices.Count > 0 ? 0 : -1;
    }

    /// <summary>Dispositivos MIDI IN del sistema. Subida desde
    /// WinCue.App/SettingsWindow.xaml.cs (donde vivía por accidente, igual que
    /// RemoteInputService antes de la Fase 6): no es de framework — es la
    /// misma llamada a NAudio.Midi.MidiIn que hace falta desde Ajustes en
    /// cualquiera de los dos productos para poblar el desplegable de
    /// dispositivo de entrada. Nunca lanza: sin subsistema MIDI (o sin
    /// dispositivos) da lista vacía, igual que ListDevices() en
    /// WinCue.Audio.AudioEngine.</summary>
    public static List<MidiDeviceInfo> ListDevices()
    {
        var result = new List<MidiDeviceInfo>();
        try
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
                result.Add(new MidiDeviceInfo(i, MidiIn.DeviceInfo(i).ProductName));
        }
        catch (Exception) { /* subsistema MIDI no disponible */ }
        return result;
    }

    /// <summary>Quarter-frame de MIDI Timecode: 8 piezas = una posición.
    /// Al completarse se calcula el instante y se remite al controlador.</summary>
    private void OnMtcQuarterFrame(int data1)
    {
        int piece = (data1 >> 4) & 7;
        _mtc[piece] = data1 & 0xF;
        _mtcMask |= 1 << piece;
        if (piece != 7 || _mtcMask != 0xFF)
            return;
        _mtcMask = 0;

        int frames = _mtc[0] | ((_mtc[1] & 1) << 4);
        int secs = _mtc[2] | ((_mtc[3] & 3) << 4);
        int mins = _mtc[4] | ((_mtc[5] & 3) << 4);
        int hours = _mtc[6] | ((_mtc[7] & 1) << 4);
        double fps = ((_mtc[7] >> 1) & 3) switch
        {
            0 => 24.0, 1 => 25.0, 2 => 29.97, _ => 30.0,
        };
        // MTC describe la posición de hace 2 frames (llega en 8 mensajes)
        double t = hours * 3600.0 + mins * 60.0 + secs + (frames + 2) / fps;
        _dispatch(() => _controller.OnTimecode(t));
    }

    private void OnMidiIn(object? sender, MidiInMessageEventArgs e)
    {
        // MIDI Timecode (0xF1): ensamblar sin pasar por el parser de NAudio
        if ((e.RawMessage & 0xFF) == 0xF1)
        {
            OnMtcQuarterFrame((e.RawMessage >> 8) & 0x7F);
            return;
        }

        // Llega en el hilo de callback de winmm → remitir al hilo de UI
        (MidiTriggerType Type, int Channel, int Data1, int Data2)? msg = e.MidiEvent switch
        {
            NoteOnEvent on when on.Velocity > 0 =>
                (MidiTriggerType.NoteOn, on.Channel, on.NoteNumber, on.Velocity),
            NoteEvent off when off.CommandCode is MidiCommandCode.NoteOff or MidiCommandCode.NoteOn =>
                (MidiTriggerType.NoteOff, off.Channel, off.NoteNumber, off.Velocity),
            ControlChangeEvent cc =>
                (MidiTriggerType.ControlChange, cc.Channel, (int)cc.Controller, cc.ControllerValue),
            PatchChangeEvent pc =>
                (MidiTriggerType.ProgramChange, pc.Channel, pc.Patch, 0),
            _ => null,
        };
        if (msg is (MidiTriggerType t, int ch, int d1, int d2))
            _dispatch(() => _controller.OnMidiMessage(t, ch, d1, d2));
    }

    public void Dispose()
    {
        _feedback?.Dispose();
        _feedback = null;
        _oscOut?.Dispose();
        _oscOut = null;
        _osc?.Dispose();
        _osc = null;
        if (_midi is not null)
        {
            try { _midi.Stop(); _midi.Dispose(); } catch (Exception) { }
            _midi = null;
        }
    }
}
