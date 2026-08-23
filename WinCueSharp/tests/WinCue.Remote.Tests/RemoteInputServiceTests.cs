// =============================================================================
// RemoteInputServiceTests.cs — el contrato que más importa: "nada de aquí
// puede tirar el show". Ejercita Apply()/Dispose() de verdad (sockets UDP
// reales, no un doble) porque es la única forma de comprobar el camino de
// error que pide el encargo: puerto OSC ocupado → Status, sin excepción.
// =============================================================================
// Hereda de WinCue.Controller.Tests.ControllerTestBase para tener un
// MainController/CueModel/SettingsManager reales sin duplicar sus dobles
// (FakeView/FakeAudioEngine/FakeVideoEngine/VirtualScheduler) — el mismo
// motivo por el que ese proyecto se referencia en el .csproj. RemoteRouter en
// sí (qué hace OnOscMessage/OnMidiMessage) ya está probado en
// tests\WinCue.Controller.Tests\RemoteControlTests.cs: aquí solo importa la
// CAPA DE TRANSPORTE (sockets/MIDI IN, Apply/Dispose, Status).
// =============================================================================

using System.Net;
using System.Net.Sockets;
using WinCue.Controller.Tests;
using WinCue.Remote;
using Xunit.Abstractions;

namespace WinCue.Remote.Tests;

public sealed class RemoteInputServiceTests : ControllerTestBase
{
    private readonly ITestOutputHelper _out;

    public RemoteInputServiceTests(ITestOutputHelper output) => _out = output;

    /// <summary>Puerto UDP libre de verdad (el SO lo asigna con puerto 0 y
    /// lo suelta al hacer Dispose) — evita que un puerto fijo choque con otra
    /// cosa en la máquina que corre los tests.</summary>
    private static int FreePort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private RemoteInputService NewService() => new(Controller, Model, Settings, a => a());

    [Fact]
    public void ListDevices_NeverThrows()
    {
        var exception = Record.Exception(() => RemoteInputService.ListDevices());

        Assert.Null(exception);
    }

    [Fact]
    public void ListDevices_ReturnsUsableIndices()
    {
        var devices = RemoteInputService.ListDevices();

        // Sin hardware MIDI en la máquina de pruebas la lista viene vacía —
        // lo único exigible es que, si hay algo, los índices son los que
        // espera NAudio.Midi.MidiIn (0..N-1, sin huecos).
        for (int i = 0; i < devices.Count; i++)
            Assert.Equal(i, devices[i].Index);
    }

    [Fact]
    public void Apply_WithEverythingDisabled_RaisesNoStatus()
    {
        Settings.Set("osc_enabled", false);
        Settings.Set("midi_enabled", false);
        using var remote = NewService();
        var statuses = new List<string>();
        remote.Status += statuses.Add;

        remote.Apply();

        Assert.Empty(statuses);
    }

    [Fact]
    public void Apply_OscEnabled_ReportsTheListeningPort()
    {
        int port = FreePort();
        Settings.Set("osc_enabled", true);
        Settings.Set("osc_listen_port", port);
        Settings.Set("midi_enabled", false);
        using var remote = NewService();
        var statuses = new List<string>();
        remote.Status += statuses.Add;

        remote.Apply();

        Assert.Contains(statuses, s => s.Contains(port.ToString()));
    }

    /// <summary>El camino que más importa del encargo: ocupar el puerto OSC a
    /// propósito y comprobar que el servicio avisa por Status y NO tira el
    /// proceso — la cabecera de RemoteInputService.cs lo deja explícito
    /// ("nada de aquí puede tirar el show").</summary>
    [Fact]
    public void Apply_OscPortAlreadyInUse_ReportsStatusAndDoesNotThrow()
    {
        int port = FreePort();
        using var busy = new UdpClient(port);   // ocupa el puerto a propósito

        Settings.Set("osc_enabled", true);
        Settings.Set("osc_listen_port", port);
        Settings.Set("midi_enabled", false);
        using var remote = NewService();
        var statuses = new List<string>();
        remote.Status += statuses.Add;

        var exception = Record.Exception(() => remote.Apply());

        foreach (string s in statuses)
            _out.WriteLine($"Status real recibido: \"{s}\"");
        _out.WriteLine($"Excepción: {(exception is null ? "ninguna" : exception.ToString())}");

        Assert.Null(exception);
        Assert.Contains(statuses, s => s.Contains("no se pudo abrir el puerto"));
    }

    [Fact]
    public void Apply_Reload_ClosesThePreviousListener()
    {
        int port1 = FreePort();
        int port2 = FreePort();
        Settings.Set("osc_enabled", true);
        Settings.Set("midi_enabled", false);
        using var remote = NewService();

        Settings.Set("osc_listen_port", port1);
        remote.Apply();

        Settings.Set("osc_listen_port", port2);
        remote.Apply();

        // Si Apply() no cerró el listener viejo, port1 sigue ocupado y este
        // bind lanza SocketException.
        var exception = Record.Exception(() =>
        {
            using var probe = new UdpClient(port1);
        });
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_AfterApply_IsClean()
    {
        Settings.Set("osc_enabled", true);
        Settings.Set("osc_listen_port", FreePort());
        Settings.Set("midi_enabled", false);
        using var remote = NewService();
        remote.Apply();

        var exception = Record.Exception(remote.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_WithoutApply_IsSafe()
    {
        using var remote = NewService();

        var exception = Record.Exception(remote.Dispose);

        Assert.Null(exception);
    }

    /// <summary>MIDI habilitado sin dispositivo que lo abra (nombre que no
    /// existe, y ninguno instalado en la máquina de pruebas): FindMidiDevice
    /// debe caer con gracia — un único aviso por Status, ninguna excepción.</summary>
    [Fact]
    public void Apply_MidiEnabled_NeverThrows_AndReportsExactlyOneStatus()
    {
        Settings.Set("osc_enabled", false);
        Settings.Set("midi_enabled", true);
        Settings.Set("midi_device_name", "dispositivo-inexistente-para-pruebas");
        using var remote = NewService();
        var statuses = new List<string>();
        remote.Status += statuses.Add;

        var exception = Record.Exception(() => remote.Apply());

        Assert.Null(exception);
        // Exactamente un aviso: "no encontrado" (sin hardware), "no se pudo
        // abrir" (dispositivo presente pero ocupado/removido) o "activo" (si
        // la máquina de pruebas SÍ tiene algún MIDI IN real) — cualquiera de
        // los tres es correcto, lo que no puede pasar es ni cero ni una
        // excepción.
        Assert.Single(statuses);
    }
}
