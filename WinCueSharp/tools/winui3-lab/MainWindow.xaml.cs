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
// "width"/"height" tras el loadfile (asíncrono) durante unos segundos. Si mpv
// llega a reportar dimensiones > 0, es evidencia de que está decodificando de
// verdad hacia la superficie del wid — sin depender de que alguien mire. Se
// mantiene mínimo a propósito: un solo sondeo con timeout, nada de manejo de
// eventos de libmpv (mpv_wait_event), que habría sido mucho más código para
// el mismo veredicto.
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

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(nint ctx, byte[] name, byte[] data);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(nint ctx, nint[] args);

    // MPV_FORMAT_INT64 = 4 en client.h de libmpv. Solo se usa para el sondeo
    // de "width"/"height" — evidencia, no parte del camino mínimo del encargo.
    private const int MpvFormatInt64 = 4;

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(nint ctx, byte[] name, int format, out long data);

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

            int rcAncho = mpv_get_property(_mpv, Utf8("width"), MpvFormatInt64, out long ancho);
            int rcAlto = mpv_get_property(_mpv, Utf8("height"), MpvFormatInt64, out long alto);

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
}
