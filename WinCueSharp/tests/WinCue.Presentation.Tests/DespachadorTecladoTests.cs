using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class DespachadorTecladoTests
{
    private const int VkSpace = 0x20, VkEscape = 0x1B, VkEnter = 0x0D,
                      VkUp = 0x26, VkDown = 0x28, VkLeft = 0x25, VkRight = 0x27,
                      VkB = 0x42, VkS = 0x53, VkP = 0x50, VkZ = 0x5A,
                      VkDelete = 0x2E, Vk1 = 0x31, VkBack = 0x08;

    private static readonly IReadOnlyDictionary<int, int> SinHotkeys =
        new Dictionary<int, int>();

    private static EstadoTeclado Normal => new(
        DialogoAbierto: false, FocoEnCampoDeEscritura: false,
        FocoEnTablaDeCues: false, HayDigitosEnBuffer: false, ModoShow: false);

    [Fact]
    public void ConDialogoAbierto_NoPasaNiUnaTecla()
    {
        var estado = Normal with { DialogoAbierto = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkSpace, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkEscape, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void EscribiendoEnUnCampo_ElEspacioNoDispara()
    {
        // La mitad peor del fallo original: un TextBox no marca el espacio
        // como tratado, asi que escribir un espacio en el nombre de un cue
        // disparaba el GO del show.
        var estado = Normal with { FocoEnCampoDeEscritura = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkSpace, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkEscape, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void Blackout_GanaAlHotkeyDeCue()
    {
        // Sin esto, un show con un cue de hotkey B hace blackout en WinCue y
        // saca ese cue A SALA en Secuence, con el mismo archivo.
        var hotkeys = new Dictionary<int, int> { [VkB] = 42 };
        var d = DespachadorTeclado.Resolver(VkB, false, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.Blackout, d.Accion);
    }

    [Fact]
    public void ElTransporte_GanaAlHotkeyDeCue()
    {
        // Divergencia deliberada con WinCue: si un show puede asignar la
        // flecha abajo como hotkey, le roba al operador la navegacion del
        // playhead, que es el gesto principal de esta tanda.
        var hotkeys = new Dictionary<int, int> { [VkDown] = 7, [VkP] = 8, [VkS] = 9 };
        Assert.Equal(AccionTecla.PlayheadSiguiente,
            DespachadorTeclado.Resolver(VkDown, false, false, false, Normal, hotkeys).Accion);
        Assert.Equal(AccionTecla.PausaGlobal,
            DespachadorTeclado.Resolver(VkP, false, false, false, Normal, hotkeys).Accion);
        Assert.Equal(AccionTecla.StopSeleccionado,
            DespachadorTeclado.Resolver(VkS, false, false, false, Normal, hotkeys).Accion);
    }

    [Fact]
    public void UnHotkeyQueNoEsDeTransporte_SiDispara()
    {
        var hotkeys = new Dictionary<int, int> { [0x51] = 13 };   // Q
        var d = DespachadorTeclado.Resolver(0x51, false, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.DispararCue, d.Accion);
        Assert.Equal(13, d.CueId);
    }

    [Fact]
    public void ElBuffer_VaDESPUESDeLosHotkeys()
    {
        // Un cue cuya hotkey sea un digito dispara, y ese digito nunca llega
        // al buffer. Es el orden de WinCue, verificado en su codigo.
        var hotkeys = new Dictionary<int, int> { [Vk1] = 5 };
        var d = DespachadorTeclado.Resolver(Vk1, false, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.DispararCue, d.Accion);
        Assert.Equal(5, d.CueId);
    }

    [Fact]
    public void SinHotkeyEnEseDigito_AlimentaElBuffer()
    {
        var d = DespachadorTeclado.Resolver(Vk1, false, false, false, Normal, SinHotkeys);
        Assert.Equal(AccionTecla.BufferDigito, d.Accion);
        Assert.Equal('1', d.Caracter);
    }

    [Fact]
    public void ConBufferVacio_RetrocesoYEnterNoSonDelBuffer()
    {
        Assert.NotEqual(AccionTecla.BufferBorrar,
            DespachadorTeclado.Resolver(VkBack, false, false, false, Normal, SinHotkeys).Accion);
        Assert.NotEqual(AccionTecla.BufferSaltar,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void ConDigitosEnElBuffer_EnterSalta()
    {
        var estado = Normal with { HayDigitosEnBuffer = true, FocoEnTablaDeCues = true };
        Assert.Equal(AccionTecla.BufferSaltar,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void EnterEnLaTabla_SinBuffer_LlevaElPlayheadALaSeleccion()
    {
        var estado = Normal with { FocoEnTablaDeCues = true };
        Assert.Equal(AccionTecla.PlayheadASeleccion,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void EnterFueraDeLaTabla_NO_EsDelDespachador()
    {
        // De esto depende que Tab+Enter siga activando botones, que es la
        // concesion de accesibilidad que paga que Espacio ya no active el
        // control con foco.
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void EnModoShow_LasDeEdicionNoHacenNada()
    {
        var estado = Normal with { ModoShow = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkDelete, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(0x43, ctrl: true, false, false, estado, SinHotkeys).Accion);  // Ctrl+C
    }

    [Fact]
    public void EnModoShow_ElTransporteSIFunciona()
    {
        var estado = Normal with { ModoShow = true };
        Assert.Equal(AccionTecla.Go,
            DespachadorTeclado.Resolver(VkSpace, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Blackout,
            DespachadorTeclado.Resolver(VkB, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void CtrlZ_NoDisparaUnCueCuyaHotkeySeaZ()
    {
        var hotkeys = new Dictionary<int, int> { [VkZ] = 3 };
        var d = DespachadorTeclado.Resolver(VkZ, ctrl: true, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.Deshacer, d.Accion);
    }

    [Fact]
    public void LasCuatroFlechas_MuevenElPlayhead()
    {
        Assert.Equal(AccionTecla.PlayheadAnterior,
            DespachadorTeclado.Resolver(VkUp, false, false, false, Normal, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.PlayheadAnterior,
            DespachadorTeclado.Resolver(VkLeft, false, false, false, Normal, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.PlayheadSiguiente,
            DespachadorTeclado.Resolver(VkDown, false, false, false, Normal, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.PlayheadSiguiente,
            DespachadorTeclado.Resolver(VkRight, false, false, false, Normal, SinHotkeys).Accion);
    }
}
