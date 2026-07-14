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

namespace WinCue.App;

public sealed class RemoteInputService : IDisposable
{
    private readonly MainController _controller;
    private readonly SettingsManager _settings;
    private readonly Action<Action> _dispatch;

    private UdpClient? _osc;
    private MidiIn? _midi;

    /// <summary>Mensajes de estado ("OSC escuchando en 8000", errores…).</summary>
    public event Action<string>? Status;

    public RemoteInputService(MainController controller, SettingsManager settings,
        Action<Action> dispatch)
    {
        _controller = controller;
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
        if (!_settings.Get("osc_enabled", false))
            return;

        int port = _settings.Get("osc_listen_port", 8000);
        UdpClient client;
        try
        {
            client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException exc)
        {
            Status?.Invoke($"OSC: no se pudo abrir el puerto {port} ({exc.SocketErrorCode})");
            return;
        }
        _osc = client;
        Status?.Invoke($"OSC escuchando en el puerto {port}");
        _ = ReceiveLoop(client);
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
        if (name.Length > 0)
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
                if (MidiIn.DeviceInfo(i).ProductName == name)
                    return i;
        }
        int idx = _settings.Get("midi_device_index", -1);
        if (idx >= 0 && idx < MidiIn.NumberOfDevices)
            return idx;
        return MidiIn.NumberOfDevices > 0 ? 0 : -1;
    }

    private void OnMidiIn(object? sender, MidiInMessageEventArgs e)
    {
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
        _osc?.Dispose();
        _osc = null;
        if (_midi is not null)
        {
            try { _midi.Stop(); _midi.Dispose(); } catch (Exception) { }
            _midi = null;
        }
    }
}
