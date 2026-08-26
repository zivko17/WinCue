using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class DespachadorTecladoTests
{
    private const int VkSpace = 0x20, VkEscape = 0x1B, VkEnter = 0x0D,
                      VkUp = 0x26, VkDown = 0x28, VkLeft = 0x25, VkRight = 0x27,
                      VkB = 0x42, VkS = 0x53, VkP = 0x50, VkZ = 0x5A,
                      VkDelete = 0x2E, Vk1 = 0x31, VkBack = 0x08,
                      VkO = 0x4F, VkG = 0x47, VkY = 0x59, VkV = 0x56,
                      VkD = 0x44, VkC = 0x43, VkQ = 0x51, VkF2 = 0x71,
                      VkDecimal = 0x6E, VkPeriodOem = 0xBE;

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
    public void ConDialogoAbierto_CtrlSNoGuarda()
    {
        // Las guardas de dialogo y de campo de escritura van ANTES del
        // bloque de Ctrl: sin ese orden, un dialogo modal abierto dejaria
        // pasar Ctrl+S y guardaria el show por debajo del dialogo.
        var estado = Normal with { DialogoAbierto = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkS, ctrl: true, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void EscribiendoEnUnCampo_CtrlDNoDuplica()
    {
        // Misma razon que la prueba anterior, con la otra guarda: escribir
        // el nombre de un cue no debe dejar pasar Ctrl+D.
        var estado = Normal with { FocoEnCampoDeEscritura = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkD, ctrl: true, false, false, estado, SinHotkeys).Accion);
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
    public void CtrlB_NoHaceBlackout()
    {
        // El candado de "sin modificadores" tambien cubre Blackout: en una
        // aplicacion de teatro, un blackout disparado por una combinacion
        // que nadie pretendia pulsar es un apagon en escena.
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkB, ctrl: true, false, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void ShiftB_NoHaceBlackout()
    {
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkB, false, shift: true, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void Escape_ParaTodoElShow()
    {
        // La tecla de panico: sin ella cubierta, romper StopAll no lo detecta
        // ninguna otra prueba (se comprobo quitandolo: 15 seguian en verde).
        Assert.Equal(AccionTecla.StopAll,
            DespachadorTeclado.Resolver(VkEscape, false, false, false, Normal, SinHotkeys).Accion);
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
        var hotkeys = new Dictionary<int, int> { [VkQ] = 13 };
        var d = DespachadorTeclado.Resolver(VkQ, false, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.DispararCue, d.Accion);
        Assert.Equal(13, d.CueId);
    }

    [Fact]
    public void ShiftMasHotkey_NoDispara()
    {
        // El candado de "hotkeys solo sin modificadores" no es solo sobre
        // Ctrl: Shift+Q tampoco debe disparar un cue de hotkey Q.
        var hotkeys = new Dictionary<int, int> { [VkQ] = 13 };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkQ, false, shift: true, false, Normal, hotkeys).Accion);
    }

    [Fact]
    public void AltMasHotkey_NoDispara()
    {
        var hotkeys = new Dictionary<int, int> { [VkQ] = 13 };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkQ, false, false, alt: true, Normal, hotkeys).Accion);
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

    [Theory]
    [InlineData(0x60, '0')]
    [InlineData(0x61, '1')]
    [InlineData(0x62, '2')]
    [InlineData(0x63, '3')]
    [InlineData(0x64, '4')]
    [InlineData(0x65, '5')]
    [InlineData(0x66, '6')]
    [InlineData(0x67, '7')]
    [InlineData(0x68, '8')]
    [InlineData(0x69, '9')]
    public void DigitosDelNumerico_AlimentanElBuffer(int vk, char esperado)
    {
        // Regresion real encontrada por mutacion: en directo, un numero de
        // cue se teclea casi siempre en el teclado numerico, no en la fila
        // superior. La version original no lo aceptaba.
        var d = DespachadorTeclado.Resolver(vk, false, false, false, Normal, SinHotkeys);
        Assert.Equal(AccionTecla.BufferDigito, d.Accion);
        Assert.Equal(esperado, d.Caracter);
    }

    [Fact]
    public void PuntoDecimalDelNumerico_AlimentaElBufferConPunto()
    {
        var d = DespachadorTeclado.Resolver(VkDecimal, false, false, false, Normal, SinHotkeys);
        Assert.Equal(AccionTecla.BufferDigito, d.Accion);
        Assert.Equal('.', d.Caracter);
    }

    [Fact]
    public void PuntoDeLaFilaSuperior_AlimentaElBufferConPunto()
    {
        // El del numerico ya estaba probado; este es el que usa la mitad de
        // los operadores en un portatil sin numerico separado. Quitar
        // VkPeriodOem de la condicion no lo detectaba ninguna prueba.
        var d = DespachadorTeclado.Resolver(VkPeriodOem, false, false, false, Normal, SinHotkeys);
        Assert.Equal(AccionTecla.BufferDigito, d.Accion);
        Assert.Equal('.', d.Caracter);
    }

    [Fact]
    public void ConBufferVacio_RetrocesoYEnterNoSonDelBuffer()
    {
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkBack, false, false, false, Normal, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void ConDigitosEnElBuffer_RetrocesoBorra()
    {
        // BufferBorrar no se afirmaba nunca en positivo: la unica prueba de
        // Retroceso era con el buffer vacio, y esa la cumple igual un codigo
        // que no implemente el borrado. Quitar el bloque entero dejaba las
        // 46 anteriores en verde.
        var estado = Normal with { HayDigitosEnBuffer = true };
        Assert.Equal(AccionTecla.BufferBorrar,
            DespachadorTeclado.Resolver(VkBack, false, false, false, estado, SinHotkeys).Accion);
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
    public void Delete_FueraDeModoShow_Elimina()
    {
        // Sin esta prueba, "bloqueado en modo show" (mas abajo) es
        // indistinguible de "no implementado": borrar el bloque entero
        // dejaba las 15 originales en verde.
        Assert.Equal(AccionTecla.Eliminar,
            DespachadorTeclado.Resolver(VkDelete, false, false, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void F2_FueraDeModoShow_Renombra()
    {
        Assert.Equal(AccionTecla.Renombrar,
            DespachadorTeclado.Resolver(VkF2, false, false, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void HotkeyEnSupr_GanaAEliminar()
    {
        // Alineado con WinCue: a diferencia del transporte, que diverge
        // deliberadamente por encima de los hotkeys para proteger el
        // playhead, aqui no hay ninguna razon para que Secuence se comporte
        // distinto. Un cue con hotkey Supr debe dispararse en los dos.
        var hotkeys = new Dictionary<int, int> { [VkDelete] = 77 };
        var d = DespachadorTeclado.Resolver(VkDelete, false, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.DispararCue, d.Accion);
        Assert.Equal(77, d.CueId);
    }

    [Fact]
    public void EnModoShow_LasDeEdicionNoHacenNada()
    {
        var estado = Normal with { ModoShow = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkDelete, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkC, ctrl: true, false, false, estado, SinHotkeys).Accion);  // Ctrl+C
    }

    [Fact]
    public void CtrlG_EnModoShow_NoHaceNada()
    {
        // Fallo del brief original, no del codigo: Agrupar modifica la
        // estructura del show igual que Copiar/Pegar/Duplicar, y se quedo
        // fuera de la lista de acciones bloqueadas en modo show.
        var estado = Normal with { ModoShow = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkG, ctrl: true, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void CtrlP_EnModoShow_PausarCueSigueFuncionando()
    {
        // A diferencia de Agrupar: pausar UN cue durante el show es operar
        // el show, no editar su estructura, asi que no lleva el candado.
        var estado = Normal with { ModoShow = true };
        Assert.Equal(AccionTecla.PausarCue,
            DespachadorTeclado.Resolver(VkP, ctrl: true, false, false, estado, SinHotkeys).Accion);
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

    [Theory]
    [InlineData(VkO, false, AccionTecla.Abrir)]
    [InlineData(VkS, false, AccionTecla.Guardar)]
    [InlineData(VkS, true, AccionTecla.GuardarComo)]
    [InlineData(VkG, false, AccionTecla.Agrupar)]
    [InlineData(VkY, false, AccionTecla.Rehacer)]
    [InlineData(VkC, false, AccionTecla.Copiar)]
    [InlineData(VkV, false, AccionTecla.Pegar)]
    [InlineData(VkD, false, AccionTecla.Duplicar)]
    [InlineData(VkP, false, AccionTecla.PausarCue)]
    public void CtrlMasTecla_ResuelveArchivoOEdicion(int vk, bool shift, AccionTecla esperado)
    {
        // Solo Ctrl+Z tenia prueba antes de esta tanda; el resto de la
        // familia Ctrl que ya existia (Abrir, Guardar, GuardarComo, Agrupar,
        // Rehacer, Copiar, Pegar, Duplicar) se podia poner a Nada sin que
        // ninguna prueba lo detectara. PausarCue es distinto: no es que
        // faltara la prueba, es que antes de esta tanda no habia ningun
        // camino que lo produjera -- era el valor del enum que nada
        // alcanzaba.
        var d = DespachadorTeclado.Resolver(vk, ctrl: true, shift, false, Normal, SinHotkeys);
        Assert.Equal(esperado, d.Accion);
    }

    [Fact]
    public void AltGr_NoDisparaAtajosDeCtrl()
    {
        // En teclado espanol (y otros) AltGr se reporta como Ctrl+Alt (la
        // tecla Alt derecha). Sin el guardian "!alt" en el bloque de Ctrl,
        // AltGr+C -que debe escribir un caracter, no copiar- dispararia
        // Copiar.
        var d = DespachadorTeclado.Resolver(VkC, ctrl: true, false, alt: true, Normal, SinHotkeys);
        Assert.Equal(AccionTecla.Nada, d.Accion);
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
