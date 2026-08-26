using System.Diagnostics;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class ProcesadorBufferIrACueTests
{
    private const int Vk1 = 0x31, Vk2 = 0x32, VkBack = 0x08, VkEnter = 0x0D,
                      VkEscape = 0x1B, VkPeriodOem = 0xBE, VkS = 0x53;

    private static readonly IReadOnlyDictionary<int, int> SinHotkeys =
        new Dictionary<int, int>();

    private static EstadoTeclado Normal => new(
        DialogoAbierto: false, FocoEnCampoDeEscritura: false,
        FocoEnTablaDeCues: false, HayDigitosEnBuffer: false, ModoShow: false);

    // Un "ahora" de partida cualquiera, lejos de 0: si Caducado tuviera un
    // desbordamiento o una resta al revés, arrancar en 0 lo taparía.
    private const long T0 = 5_000_000_000L;

    // Cuántos ticks de Stopwatch representan N segundos, calculado con la
    // MISMA frecuencia que Stopwatch.GetElapsedTime usa por dentro — así la
    // prueba no depende de a qué frecuencia corre esta máquina en concreto.
    private static long Segundos(double s) => (long)(s * Stopwatch.Frequency);

    [Fact]
    public void UnDigito_SeAcumulaYQuedaFechado()
    {
        var r = ProcesadorBufferIrACue.Procesar(
            EstadoBufferIrACue.Vacio, T0, Vk1, false, false, false, Normal, SinHotkeys);

        Assert.Equal(AccionTecla.BufferDigito, r.Accion);
        Assert.Equal("1", r.Estado.Texto);
        Assert.Equal(T0, r.Estado.UltimoTimestamp);
    }

    [Fact]
    public void VariosDigitosYElPunto_SeAcumulanEnOrden()
    {
        var estado = EstadoBufferIrACue.Vacio;
        estado = ProcesadorBufferIrACue.Procesar(estado, T0, Vk2, false, false, false, Normal, SinHotkeys).Estado;
        estado = ProcesadorBufferIrACue.Procesar(estado, T0, VkPeriodOem, false, false, false, Normal, SinHotkeys).Estado;
        estado = ProcesadorBufferIrACue.Procesar(estado, T0, Vk1, false, false, false, Normal, SinHotkeys).Estado;

        Assert.Equal("2.1", estado.Texto);
    }

    [Fact]
    public void MenosDeCuatroSegundosDespues_NoCaduca()
    {
        // Teclear "2" y, menos de 4 s después, "1": tiene que quedar "21".
        var r1 = ProcesadorBufferIrACue.Procesar(
            EstadoBufferIrACue.Vacio, T0, Vk2, false, false, false, Normal, SinHotkeys);

        long ahora = T0 + Segundos(3.9);
        var r2 = ProcesadorBufferIrACue.Procesar(r1.Estado, ahora, Vk1, false, false, false, Normal, SinHotkeys);

        Assert.Equal("21", r2.Estado.Texto);
    }

    [Fact]
    public void MasDeCuatroSegundosDespues_Caduca_YArrancaUnBufferNuevo()
    {
        // El caso de prueba explícito del brief: "2", esperar 5 s, "1" ->
        // tiene que ir al cue 1, no al 21. Aquí se comprueba la mitad que le
        // toca a este fichero: que el TEXTO acumulado sea "1", no "21".
        var r1 = ProcesadorBufferIrACue.Procesar(
            EstadoBufferIrACue.Vacio, T0, Vk2, false, false, false, Normal, SinHotkeys);

        long ahora = T0 + Segundos(5);
        var r2 = ProcesadorBufferIrACue.Procesar(r1.Estado, ahora, Vk1, false, false, false, Normal, SinHotkeys);

        Assert.Equal("1", r2.Estado.Texto);
        Assert.Equal(AccionTecla.BufferDigito, r2.Accion);
    }

    [Fact]
    public void ConDigitos_RetrocesoBorraElUltimo_YNoRefrescaElReloj()
    {
        var conDigitos = new EstadoBufferIrACue("12", T0);

        long ahora = T0 + Segundos(2);
        var r = ProcesadorBufferIrACue.Procesar(conDigitos, ahora, VkBack, false, false, false, Normal, SinHotkeys);

        Assert.Equal(AccionTecla.BufferBorrar, r.Accion);
        Assert.Equal("1", r.Estado.Texto);
        // El timestamp NO se toca: borrar no cuenta como "seguir tecleando".
        Assert.Equal(T0, r.Estado.UltimoTimestamp);
    }

    [Fact]
    public void ConElBufferVacio_RetrocesoNoHaceNada()
    {
        var r = ProcesadorBufferIrACue.Procesar(
            EstadoBufferIrACue.Vacio, T0, VkBack, false, false, false, Normal, SinHotkeys);

        Assert.Equal(AccionTecla.Nada, r.Accion);
        Assert.Equal("", r.Estado.Texto);
    }

    [Fact]
    public void ConDigitos_EnterSalta_DevuelveElTextoYVaciaElBuffer()
    {
        var conDigitos = new EstadoBufferIrACue("5.1", T0);

        var r = ProcesadorBufferIrACue.Procesar(
            conDigitos, T0 + Segundos(1), VkEnter, false, false, false, Normal, SinHotkeys);

        Assert.Equal(AccionTecla.BufferSaltar, r.Accion);
        Assert.Equal("5.1", r.CueObjetivo);
        Assert.Equal("", r.Estado.Texto);
    }

    [Fact]
    public void ConElBufferVacioYElFocoEnLaTabla_EnterEsPlayheadASeleccion()
    {
        var estado = Normal with { FocoEnTablaDeCues = true };

        var r = ProcesadorBufferIrACue.Procesar(
            EstadoBufferIrACue.Vacio, T0, VkEnter, false, false, false, estado, SinHotkeys);

        Assert.Equal(AccionTecla.PlayheadASeleccion, r.Accion);
        Assert.Null(r.CueObjetivo);
    }

    [Fact]
    public void EscapeConDigitosEnElBuffer_EsStopAll_YNoTocaElBuffer()
    {
        // La regla que no se puede equivocar: Escape sigue siendo STOP ALL
        // pase lo que pase, y el buffer se queda como estaba -- no lo vacía
        // Escape, lo vacía únicamente la caducidad de 4 s.
        var conDigitos = new EstadoBufferIrACue("1", T0);

        var r = ProcesadorBufferIrACue.Procesar(
            conDigitos, T0 + Segundos(1), VkEscape, false, false, false, Normal, SinHotkeys);

        Assert.Equal(AccionTecla.StopAll, r.Accion);
        Assert.Equal("1", r.Estado.Texto);
    }

    [Fact]
    public void UnHotkeyDeCue_GanaAlDigito_YNoTocaElBuffer()
    {
        // No es el camino real de MainWindow (que ya filtró los hotkeys antes
        // de llamar aquí y pasa un diccionario vacío), pero documenta que el
        // envoltorio no rompe la precedencia de Resolver si alguna vez se le
        // pasan hotkeys de verdad.
        var hotkeys = new Dictionary<int, int> { [Vk1] = 42 };

        var r = ProcesadorBufferIrACue.Procesar(
            EstadoBufferIrACue.Vacio, T0, Vk1, false, false, false, Normal, hotkeys);

        Assert.Equal(AccionTecla.DispararCue, r.Accion);
        Assert.Equal("", r.Estado.Texto);
    }

    [Fact]
    public void Caducado_ConBufferVacio_EsFalso()
    {
        Assert.False(EstadoBufferIrACue.Vacio.Caducado(T0 + Segundos(100)));
    }

    [Fact]
    public void Caducado_JustoEnElLimite_NoHaCaducado()
    {
        var estado = new EstadoBufferIrACue("1", T0);
        Assert.False(estado.Caducado(T0 + Segundos(4)));
    }

    [Fact]
    public void Caducado_PasadoElLimite_HaCaducado()
    {
        var estado = new EstadoBufferIrACue("1", T0);
        Assert.True(estado.Caducado(T0 + Segundos(4.01)));
    }

    // Un extra de sanidad para el "S" de StopSeleccionado (tarea 5, todavía
    // sin ejecucion): confirma que hoy Resolver YA lo distingue del dígito y
    // que el envoltorio lo deja pasar sin tocar el buffer, aunque nadie lo
    // ejecute todavía.
    [Fact]
    public void LaS_NoEsUnDigito_YNoTocaElBuffer()
    {
        var conDigitos = new EstadoBufferIrACue("9", T0);

        var r = ProcesadorBufferIrACue.Procesar(
            conDigitos, T0 + Segundos(1), VkS, false, false, false, Normal, SinHotkeys);

        Assert.Equal(AccionTecla.StopSeleccionado, r.Accion);
        Assert.Equal("9", r.Estado.Texto);
    }
}
