// =============================================================================
// MainWindow.xaml.cs — ventana del banco de pruebas.
// El botón crea un HWND hijo y le mete libmpv dentro por «wid».
//
// POR QUÉ libmpv Y NO mpv.exe: el ejecutable habría sido más corto, pero NO
// está en el PATH de esta máquina — sí está libmpv-2.dll en libmpv-lgpl\, que
// es además lo que usa el producto. El P/Invoke de abajo es el mínimo
// imprescindible (crear, wid, inicializar, cargar) y NO pretende parecerse al
// de WinCue.Video: aquí solo se comprueba que un HWND hijo de un AppWindow de
// WinUI 3 acepta vídeo.
//
// INSTRUMENTACIÓN AÑADIDA SOBRE EL ENCARGO (task-3-brief.md, resolución de la
// ambigüedad del Paso 3): quien ejecuta esto no puede ver la pantalla ni
// arrastrar la ventana entre monitores. Así que, además de mostrar los HWND y
// los códigos de retorno de mpv_create/mpv_initialize/mpv_command, se sondea
// "width"/"height" tras el loadfile (asíncrono) durante unos segundos.
//
// CONTROL NEGATIVO (revisión de la tarea 3, hallazgo "Importante"): el
// revisor señaló, con razón, que width/height por sí solos NO discriminan
// "mpv decodificó hacia NUESTRO HWND" de "mpv decodificó hacia cualquier
// sitio" — son metadatos del demuxer, se pueblan en cuanto se abre el
// archivo, con wid válido, inválido o ausente. EjecutarControlNegativoAsync
// añade justo eso: la misma prueba con (A) wid = nuestro HWND hijo real,
// (B) sin wid, y (C) wid inválido (0xDEADBEEF), comparando en los tres casos
// width/height, "video-out-params/w" (que solo se puebla con una cadena de
// render activa) y — el discriminador real — cuántas ventanas hijas aparecen
// bajo nuestro HWND real con EnumChildWindows. Se dispara solo con
// LAB_CONTROL=1, para no interferir con la verificación humana normal
// (LAB_MEDIA a solas), que necesita que el vídeo de la Prueba A del botón se
// quede reproduciendo sin que nada lo destruya a los pocos segundos.
// =============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WinUi3Lab;

public sealed partial class MainWindow : Window
{
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint mpv_create();

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_initialize(nint ctx);

    // Usada solo por el control negativo: las instancias de usar-y-tirar de
    // EjecutarControlNegativoAsync se destruyen entre sí para no interferir.
    // El _mpv del botón (Prueba A humana) NO se destruye — tiene que seguir
    // reproduciendo en bucle para que Andrés pueda mirar el airspace y
    // arrastrar la ventana entre monitores (ver PENDIENTE en INFORME.md).
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_terminate_destroy(nint ctx);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(nint ctx, byte[] name, byte[] data);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(nint ctx, nint[] args);

    // MPV_FORMAT_INT64 = 4, MPV_FORMAT_STRING = 1 (client.h de libmpv).
    private const int MpvFormatInt64 = 4;
    private const int MpvFormatString = 1;

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    private static extern int mpv_get_property_int64(nint ctx, byte[] name, int format, out long data);

    // Mismo símbolo nativo que el de arriba, con la firma que corresponde a
    // MPV_FORMAT_STRING: mpv escribe un puntero a char* que hay que liberar
    // con mpv_free. Se usa para leer "current-vo" (qué salida de vídeo eligió).
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    private static extern int mpv_get_property_string(nint ctx, byte[] name, int format, out nint dataPtr);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free(nint data);

    // --- user32: el discriminador real del control negativo -----------------
    // Si mpv creó su superficie de render DENTRO de nuestro HWND hijo, tiene
    // que aparecer como descendiente suyo. EnumChildWindows es recursivo
    // (enumera también los hijos de los hijos), así que sirve aunque mpv
    // anide su ventana de contexto GPU un nivel más abajo.
    private delegate bool EnumChildProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hwndPadre, EnumChildProc callback, nint lParam);

    private static int ContarDescendientes(nint hwndPadre)
    {
        int n = 0;
        EnumChildWindows(hwndPadre, (h, l) => { n++; return true; }, 0);
        return n;
    }

    private static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");

    private nint _mpv;

    public MainWindow()
    {
        InitializeComponent();
        BotonVideo.Click += ProbarVideo;

        // Instrumentación añadida sobre el encargo: quien ejecuta esto no
        // tiene ojos ni manos para pulsar el botón. Si LAB_MEDIA ya está
        // definida, se dispara la misma prueba sola al arrancar, para que
        // los códigos de mpv y el sondeo de dimensiones queden en stdout sin
        // depender del clic. El botón se deja igual, para la verificación
        // humana del Paso 3 (airspace y DPI), que sigue siendo manual.
        if (Environment.GetEnvironmentVariable("LAB_MEDIA") is not null)
            DispatcherQueue.TryEnqueue(() => ProbarVideo(this, null!));

        // Control negativo (revisión de la tarea 3). Solo con LAB_CONTROL=1
        // — deliberadamente NO ligado a LAB_MEDIA a secas, para no destruir
        // ni tocar la reproducción de la Prueba A que un humano pueda estar
        // mirando en ese mismo momento.
        if (Environment.GetEnvironmentVariable("LAB_CONTROL") == "1")
            DispatcherQueue.TryEnqueue(async () => await EjecutarControlNegativoAsync());
    }

    private void ProbarVideo(object sender, RoutedEventArgs e)
    {
        string media = Environment.GetEnvironmentVariable("LAB_MEDIA")
            ?? throw new InvalidOperationException("Define LAB_MEDIA con la ruta a un vídeo.");

        nint padre = WindowNative.GetWindowHandle(this);
        nint hijo = HwndHijo.Crear(padre, 0, 80, 640, 360);

        _mpv = mpv_create();
        if (_mpv == 0)
        {
            string msg = $"mpv_create falló (HWND hijo {hijo}, padre {padre})" +
                " — ¿está libmpv-2.dll al lado del exe?";
            Estado.Text = msg;
            Console.WriteLine($"[winui3-lab] {msg}");
            return;
        }

        // «wid» hay que fijarlo ANTES de mpv_initialize: después no tiene efecto.
        mpv_set_option_string(_mpv, Utf8("wid"), Utf8(hijo.ToString()));
        mpv_set_option_string(_mpv, Utf8("loop"), Utf8("inf"));

        int rcInit = mpv_initialize(_mpv);
        if (rcInit < 0)
        {
            string msg = $"mpv_initialize falló ({rcInit}). HWND hijo {hijo}, padre {padre}.";
            Estado.Text = msg;
            Console.WriteLine($"[winui3-lab] {msg}");
            return;
        }

        nint pCarga = Marshal.StringToCoTaskMemUTF8("loadfile");
        nint pRuta = Marshal.StringToCoTaskMemUTF8(media);
        int rcCommand;
        try
        {
            rcCommand = mpv_command(_mpv, [pCarga, pRuta, 0]);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pCarga);
            Marshal.FreeCoTaskMem(pRuta);
        }

        string estadoInicial = $"mpv en HWND {hijo} (padre {padre}). " +
            $"create=OK, initialize={rcInit}, command={rcCommand}.";
        Estado.Text = estadoInicial;
        Console.WriteLine($"[winui3-lab] {estadoInicial}");

        // «loadfile» es asíncrono: en este punto el archivo aún no está
        // decodificado. Se sondea en segundo plano, sin bloquear el hilo de UI.
        _ = SondearDimensionesAsync(hijo, padre, rcInit, rcCommand);
    }

    private async Task SondearDimensionesAsync(nint hijo, nint padre, int rcInit, int rcCommand)
    {
        for (int intento = 0; intento < 15; intento++)
        {
            await Task.Delay(400);

            int rcAncho = mpv_get_property_int64(_mpv, Utf8("width"), MpvFormatInt64, out long ancho);
            int rcAlto = mpv_get_property_int64(_mpv, Utf8("height"), MpvFormatInt64, out long alto);

            if (rcAncho == 0 && rcAlto == 0 && ancho > 0 && alto > 0)
            {
                string msg = $"mpv en HWND {hijo} (padre {padre}). " +
                    $"create=OK, initialize={rcInit}, command={rcCommand}. " +
                    $"Vídeo decodificando: {ancho}x{alto}.";
                DispatcherQueue.TryEnqueue(() => Estado.Text = msg);
                Console.WriteLine($"[winui3-lab] {msg}");
                return;
            }
        }

        string sinDatos = $"mpv en HWND {hijo} (padre {padre}). " +
            $"create=OK, initialize={rcInit}, command={rcCommand}. " +
            "Sin width/height tras ~6 s de sondeo — hace falta el ojo humano.";
        DispatcherQueue.TryEnqueue(() => Estado.Text = sinDatos);
        Console.WriteLine($"[winui3-lab] {sinDatos}");
    }

    // =========================================================================
    // Control negativo — responde al hallazgo del revisor.
    // =========================================================================

    private readonly record struct ResultadoPrueba(
        int RcInit, int RcCommand, long Ancho, long Alto,
        string? CurrentVo, long VoParamW, int Descendientes);

    private async Task EjecutarControlNegativoAsync()
    {
        string? media = Environment.GetEnvironmentVariable("LAB_MEDIA");
        if (media is null)
        {
            Console.WriteLine("[control] LAB_MEDIA no definida — no se puede ejecutar el control negativo.");
            return;
        }

        nint padre = WindowNative.GetWindowHandle(this);

        // El HWND hijo REAL, creado una sola vez. Las tres pruebas comprueban
        // cuántos descendientes le aparecen DESPUÉS de cada una — si mpv no
        // apunta aquí (casos B y C), este número no debería cambiar.
        nint hijoReal = HwndHijo.Crear(padre, 0, 460, 640, 360);
        int descendientesAntes = ContarDescendientes(hijoReal);
        Console.WriteLine($"[control] HWND hijo real = {hijoReal}. Descendientes antes de las pruebas: {descendientesAntes}.");

        var a = await ProbarConWidAsync("A (wid = hijo real, control positivo)", hijoReal, hijoReal, media);
        var b = await ProbarConWidAsync("B (sin wid)", null, hijoReal, media);
        var c = await ProbarConWidAsync("C (wid invalido = 0xDEADBEEF)", unchecked((nint)0xDEADBEEF), hijoReal, media);

        string resumen =
            $"[control] RESUMEN (descendientes de hijoReal antes de empezar: {descendientesAntes})\n" +
            $"  A: init={a.RcInit} command={a.RcCommand} width={a.Ancho} height={a.Alto} " +
            $"video-out-params/w={a.VoParamW} current-vo={a.CurrentVo ?? "(ninguno)"} " +
            $"descendientesDeHijoReal={a.Descendientes}\n" +
            $"  B: init={b.RcInit} command={b.RcCommand} width={b.Ancho} height={b.Alto} " +
            $"video-out-params/w={b.VoParamW} current-vo={b.CurrentVo ?? "(ninguno)"} " +
            $"descendientesDeHijoReal={b.Descendientes}\n" +
            $"  C: init={c.RcInit} command={c.RcCommand} width={c.Ancho} height={c.Alto} " +
            $"video-out-params/w={c.VoParamW} current-vo={c.CurrentVo ?? "(ninguno)"} " +
            $"descendientesDeHijoReal={c.Descendientes}";
        Console.WriteLine(resumen);
        DispatcherQueue.TryEnqueue(() => Estado.Text = resumen.Replace("\n", " | "));
    }

    // Crea una instancia de mpv de usar-y-tirar, con el wid indicado (o sin
    // fijarlo si es null), carga la media y sondea evidencia de decodificacion.
    // Devuelve los datos crudos - la interpretacion va en el informe, no aqui.
    private async Task<ResultadoPrueba> ProbarConWidAsync(string etiqueta, nint? wid, nint hijoRealParaContar, string media)
    {
        nint mpv = mpv_create();
        if (mpv == 0)
        {
            Console.WriteLine($"[control] {etiqueta}: mpv_create falló.");
            return new ResultadoPrueba(int.MinValue, int.MinValue, 0, 0, null, 0, ContarDescendientes(hijoRealParaContar));
        }

        if (wid.HasValue)
            mpv_set_option_string(mpv, Utf8("wid"), Utf8(wid.Value.ToString()));
        // Sin "loop=inf" aquí: estas instancias son de usar-y-tirar, no hace
        // falta seguir reproduciendo — se destruyen en cuanto se mide.

        int rcInit = mpv_initialize(mpv);

        int rcCommand = int.MinValue;
        if (rcInit >= 0)
        {
            nint pCarga = Marshal.StringToCoTaskMemUTF8("loadfile");
            nint pRuta = Marshal.StringToCoTaskMemUTF8(media);
            try { rcCommand = mpv_command(mpv, [pCarga, pRuta, 0]); }
            finally
            {
                Marshal.FreeCoTaskMem(pCarga);
                Marshal.FreeCoTaskMem(pRuta);
            }
        }

        long ancho = 0, alto = 0, voParamW = 0;
        string? currentVo = null;

        if (rcInit >= 0 && rcCommand >= 0)
        {
            for (int intento = 0; intento < 15; intento++)
            {
                await Task.Delay(400);
                int rcAncho = mpv_get_property_int64(mpv, Utf8("width"), MpvFormatInt64, out ancho);
                int rcAlto = mpv_get_property_int64(mpv, Utf8("height"), MpvFormatInt64, out alto);
                if (rcAncho == 0 && rcAlto == 0 && ancho > 0 && alto > 0)
                    break;
            }

            // "video-out-params/w" (sub-propiedad de un nodo) solo se puebla
            // con una cadena de render activa — a diferencia de width/height,
            // que son metadatos del demuxer disponibles con solo abrir el
            // archivo. Es el segundo discriminador que pidió el revisor.
            mpv_get_property_int64(mpv, Utf8("video-out-params/w"), MpvFormatInt64, out voParamW);

            int rcVo = mpv_get_property_string(mpv, Utf8("current-vo"), MpvFormatString, out nint pVo);
            if (rcVo == 0 && pVo != 0)
            {
                currentVo = Marshal.PtrToStringUTF8(pVo);
                mpv_free(pVo);
            }
        }

        int descendientes = ContarDescendientes(hijoRealParaContar);

        Console.WriteLine($"[control] {etiqueta}: init={rcInit} command={rcCommand} " +
            $"width={ancho} height={alto} video-out-params/w={voParamW} " +
            $"current-vo={currentVo ?? "(ninguno)"} descendientesDeHijoReal={descendientes}");

        mpv_terminate_destroy(mpv);

        return new ResultadoPrueba(rcInit, rcCommand, ancho, alto, currentVo, voParamW, descendientes);
    }
}
