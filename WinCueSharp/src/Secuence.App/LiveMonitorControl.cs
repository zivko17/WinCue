// =============================================================================
// LiveMonitorControl.cs — Mini-monitor PGM/PVW EN VIVO (mpv embebido), WinUI 3
// =============================================================================
// Equivalente de src/WinCue.App/LiveMonitorControl.cs (WPF) para esta interfaz,
// NO una copia (regla 2 del proyecto). WinUI 3 no tiene WindowsFormsHost: el
// mpv embebido usa el MISMO patrón que VideoOutputWindow.xaml.cs — HwndHijo,
// una ventana hija Win32 a mano —, generalizado para poder vivir en un HUECO
// dentro de MainWindow (el previo PGM o PVW) en vez de llenar toda una
// ventana: Crear/Colocar ahora reciben también su posición (ver HwndHijo.cs).
//
// GEOMETRÍA DEL HUECO — se mide, no se asume (mismo espíritu que el aviso de
// WPF sobre "la barra se MIDE en vez de darla por supuesta"): el HWND hijo va
// bajo la cabecera de color (PgmHeader/PvwHeader), así que su alto se resta
// del hueco medido (PgmInner/PvwInner). Se resincroniza en el primer Loaded,
// en cada SizeChanged del hueco y en cada cambio de XamlRoot (DPI).
//
// ESPEJO DEL COMPOSITOR — YA CONECTADO. Este archivo decía hasta ahora que
// Secuence no espejaba «porque el compositor GPU no había llegado a este
// producto», y dejaba escrito que «si el compositor llega, esa rama sube
// entonces». Ha llegado (SalidaGpu.cs), así que la rama está aquí y este
// aviso deja de mentir. Igual que WinCue.App.PGM, el monitor tiene DOS ramas:
//
//   · ESPEJO — el cue en pantalla tiene capa de compositor: el PGM NO
//     decodifica nada. Un SwapChainSink propio, sobre un SEGUNDO HWND hijo,
//     se le entrega al compositor de una salida como Mirror; ese compositor
//     lo pinta en el MISMO fotograma y el MISMO hilo que la sala. Así el PGM
//     es lo que SALE (pila de pistas, opacidades y cruces incluidos) y además
//     sale gratis: con un 4K60 el mpv del monitor obligaba a decodificar y a
//     leer el archivo del disco DOS veces.
//
//   · mpv EMBEBIDO Y MUDO — todo lo demás, sin tocar una línea: ruta clásica
//     HWND, HAP/ProRes por sus pipelines propios, el PVW y el scrub de
//     edición. Esa rama es la que WinCue toma cuando no hay qué espejar y
//     NO se borra.
//
// QUIÉN LLAMA AL COPIADOR (regla dura de esta arquitectura): el copiador de
// una capa —el StretchRect interop→presentación, ver CapaCompositor.cs— lo
// invoca SIEMPRE el compositor DUEÑO de esa salida. El espejo solo MUESTREA,
// en lectura, y eso lo GARANTIZA EL MOTOR, no la casualidad: el pase de
// espejo va marcado SoloLectura y baja hasta
// IVideoSource.TryAcquireReadOnly, que ni consume la bandera de «fotograma
// nuevo» ni dispara copia ninguna.
//
// Y no es un detalle de limpieza. En modo LIENZO el espejo recorre nodos de
// OTRAS salidas; muestreándolos «de verdad», el hilo de la ANFITRIONA —que es
// un hilo que va A LA SALA— le robaría la copia a su dueño y, con el puente
// D3D9Ex, se quedaría bloqueado dentro de CopyPresentFenced esperando un fence
// de GPU de otro device, con el lock de su grafo tomado. O sea: el monitor de
// control metiéndole latencia a la sala. La regla de toda esta tanda es que el
// monitor NO le cuesta NADA a la sala.
//
// LO QUE SÍ QUEDA, y se acepta: al mirar salidas que no son la anfitriona hay
// una ventana de tearing TEÓRICA — se puede muestrear una superficie mientras
// su dueño la está copiando— y el espejo puede ir un fotograma por detrás de
// lo que el dueño acaba de copiar. Las dos cosas son invisibles en un monitor,
// y evitarlas costaría sincronizar entre sí los hilos que van a pantalla, que
// es justo lo que no se debe tocar (cada salida presenta al vsync de SU
// pantalla).
//
// LO QUE SÍ SE EVITA, porque no es tearing sino la aplicación abajo: recorrer
// la lista VIVA de hijos del grafo de OTRA salida. StackNode.Render la recorre
// POR ÍNDICE y su dueño la muta (Insertar/Quitar) bajo un lock DISTINTO del
// nuestro: un GO en la salida 2 en mitad de nuestro fotograma sería un
// ArgumentOutOfRangeException en un hilo de fondo, o sea la app entera abajo
// en mitad de una función. Por eso de las salidas AJENAS se toma una
// INSTANTÁNEA de sus hijos dentro de SU Mutate (ver Instantanea), y el espejo
// recorre esa copia, que nadie más toca. La anfitriona sí va en vivo: ahí el
// lock es el mismo.
//
// ShowPaused y Scrub YA ESTÁN: los pide el cursor de edición del timeline,
// que enseña en el PVW el fotograma bajo el cursor. El PVW es el visor de
// PREPARACIÓN, así que mirar ahí no toca la sala.
//
// FUERA DE ESTA PASADA todavía: SetGeometry y TryGrabStillAsync (ver el mismo
// aviso en GeometryCanvas.cs).
// =============================================================================

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCue.Video;
using WinCue.Video.Gpu;

namespace Secuence.App;

/// <summary>Un previo PGM o PVW: cabecera de color + hueco donde vive el mpv
/// embebido (muted, decode independiente) o, en el PGM con compositor, el
/// ESPEJO de lo que sale a sala — ver la cabecera del archivo para las dos
/// ramas y para quién llama al copiador. <paramref name="hwndPropio"/> es el
/// HWND de MainWindow (padre del hijo Win32); <paramref name="inner"/>/
/// <paramref name="header"/> son PgmInner/PgmHeader (o Pvw-) del XAML, para
/// medir el hueco; <paramref name="message"/>/<paramref name="status"/> son
/// PgmMessage/PgmStatus (o Pvw-).</summary>
internal sealed class LiveMonitorControl : IDisposable
{
    private readonly nint _hwndPropio;
    private readonly FrameworkElement _inner;
    private readonly FrameworkElement _header;
    private readonly TextBlock _message;
    private readonly TextBlock _status;

    private nint _hijo;
    private MpvPlayer? _player;
    private bool? _pauseState;        // último pause aplicado (null = re-asertar)
    private double _cachedDuration;
    private bool _durationAsked;
    private string _currentPath = "";
    private double _lastElapsed = -1;

    // ── Espejo del compositor (solo lo usa el PGM; el PVW nunca lo pide) ──
    // Un SEGUNDO HWND hijo, hermano del de mpv y con la misma geometría: el
    // swapchain del espejo no puede ir sobre el de mpv (dos dueños del mismo
    // HWND es negro garantizado, ver VideoOutputWindow.LiberarSalidaGpu). Se
    // enseña uno u otro según la rama activa.
    private nint _hijoEspejo;

    /// <summary>Hay un diálogo modal (el `ContentDialog` de Ajustes, una
    /// confirmación…) abierto ENCIMA de este monitor, así que sus dos HWND
    /// hijos están ocultos a propósito.
    ///
    /// POR QUÉ HACE FALTA: ahora que el hijo se sube por encima del puente XAML
    /// (ver HwndHijo), el vídeo se pinta también por encima del diálogo donde
    /// se solapen — está fotografiado: el hueco del PGM comiéndose los rótulos
    /// de Ajustes, y su velo de atenuación sin atenuar el hueco (luma 75,64
    /// con el diálogo abierto, idéntica a la de antes de abrirlo). Con la
    /// ventana ancha no se solapan; en la pantalla de 1024×600 del autor sí.
    /// Ocultar los hijos mientras dure el diálogo es el mismo camino que ya
    /// usan Clear y el blackout.
    ///
    /// El latch vive AQUÍ y no en quien abre el diálogo porque el tic del PGM
    /// sigue corriendo cada 500 ms por debajo: sin él, RefrescarEspejo o
    /// ShowVideo volverían a mostrar el hijo a media conversación.</summary>
    private bool _tapadoPorDialogo;

    private SwapChainSink? _sinkEspejo;
    private SalidaGpu? _anfitriona;   // salida cuyo compositor DIBUJA el espejo
    private int _espejoW, _espejoH;   // tamaño del sink vivo
    private int _huecoW, _huecoH;     // último tamaño MEDIDO del hueco
    private bool _espejando;

    /// <summary>Raíz que el compositor anfitrión pinta en el espejo. La LEE el
    /// HILO DE RENDER (una vez por fotograma, vía <see cref="LeerRaizEspejo"/>)
    /// y la ESCRIBE el hilo de interfaz. Es un campo volatile y NUNCA se muta
    /// en sitio: cada refresco construye un árbol nuevo y lo publica de un
    /// tirón, así que el hilo de render ve o el viejo entero o el nuevo entero.
    /// Mutar el que está puesto sería una carrera sobre una lista que se
    /// recorre por índice.</summary>
    private volatile GraphNode? _raizEspejo;

    public string CurrentPath => _currentPath;

    /// <summary>Qué mira el espejo: -1 = LIENZO (el canvas entero recompuesto),
    /// 0..3 = Salida 1..4. Solo lo guarda; quien lo traduce a un plan concreto
    /// es <see cref="PedirPlanEspejo"/>, que es de MainWindow (el único que
    /// conoce las salidas y los ajustes).</summary>
    public int FuenteEspejo { get; set; } = -1;

    /// <summary>true si el monitor está ESPEJANDO (sin decodificar nada).
    /// Equivalente de WinCue.App.LiveMonitorControl.IsMirroring.</summary>
    public bool Espejando => _espejando;

    /// <summary>El espejo HABÍA de poder montarse —hay plan, o sea que la
    /// fuente elegida tiene compositor— y NO se pudo: la fontanería del
    /// monitor (su HWND hijo o su swapchain) falló.
    ///
    /// Es un estado DISTINTO de «no hay nada que espejar», y quien decide qué
    /// enseñar tiene que separarlos: aquí la sala está perfectamente, lo que
    /// se ha roto es nuestro monitor, así que lo correcto es que el PGM caiga
    /// a la RAMA CLÁSICA y decodifique su copia muda. Puede diferir en
    /// opacidad o en blackout, sí — pero dejar al operador sin imagen porque
    /// nuestro swapchain no nació es peor, y el aviso de la barra de estado ya
    /// le dice que el espejo está caído.
    ///
    /// EL REARME DEL AVISO CUELGA DE AQUÍ, y no de montar el swapchain: lo que
    /// hace que el operador merezca un aviso nuevo es haber dejado de estar
    /// averiado, no que una pieza concreta de la fontanería haya nacido. Atado
    /// a lo segundo, una avería que se cura por otro camino (deja de haber plan
    /// que espejar, o el hueco vuelve a medirse) dejaba MUDA la siguiente.</summary>
    public bool EspejoAveriado
    {
        get => _espejoAveriado;
        private set
        {
            if (_espejoAveriado == value)
                return;
            _espejoAveriado = value;
            if (!value)
                _averiaAvisada = false;   // curado: la próxima sí se avisa
        }
    }

    private bool _espejoAveriado;

    /// <summary>Camino a la barra de estado de la ventana de control. Sin
    /// esto, un espejo que no arranca solo deja rastro en un archivo de log —
    /// mismo criterio (y mismo nombre) que SalidaGpu.AvisoEnPantalla.</summary>
    public Action<string>? AvisoEnPantalla { get; set; }

    /// <summary>Ya se avisó de ESTA avería: el tic del PGM vuelve a intentarlo
    /// cada medio segundo y la barra de estado no puede convertirse en un
    /// martilleo. Se rearma en cuanto el espejo vuelve a montarse.</summary>
    private bool _averiaAvisada;

    /// <summary>Una región del espejo: la salida cuyo compositor la pinta y el
    /// rectángulo que ocupa EN COORDENADAS DEL LIENZO. En modo LIENZO hay una
    /// por salida habilitada; mirando una sola salida hay una sola, que ocupa
    /// el lienzo entero. Cada vista llega YA RECORTADA desde MpvGpuRenderer
    /// (cada salida renderiza su propio recorte en su propia superficie), así
    /// que aquí no hay recorte que calcular: solo colocación.
    ///
    /// <c>Negro</c> = esa salida está en BLACKOUT. Su región NO se dibuja, y el
    /// Clear del compositor la deja negra — ver ConstruirRaizEspejo.</summary>
    internal readonly record struct RegionEspejo(
        SalidaGpu Salida, int X, int Y, int W, int H, bool Negro);

    /// <summary>Lo que hay que espejar AHORA, o null si no hay nada que
    /// espejar (sin compositor, o el cue en pantalla va por la ruta clásica).
    /// La PRIMERA región manda: su salida es la ANFITRIONA, la que presta su
    /// compositor —y su hilo, y su fotograma— para dibujar el espejo entero.
    /// </summary>
    internal Func<(int LienzoW, int LienzoH, IReadOnlyList<RegionEspejo> Regiones)?>?
        PedirPlanEspejo { get; set; }

    public LiveMonitorControl(nint hwndPropio, FrameworkElement inner,
        FrameworkElement header, TextBlock message, TextBlock status)
    {
        _hwndPropio = hwndPropio;
        _inner = inner;
        _header = header;
        _message = message;
        _status = status;

        _inner.SizeChanged += (_, _) => SyncGeometry();
        _inner.Loaded += (_, _) =>
        {
            SyncGeometry();
            if (_inner.XamlRoot is not null)
                _inner.XamlRoot.Changed += (_, _) => SyncGeometry();
        };
    }

    // ------------------------------------------------------------------
    // Geometría del hueco (medida, no asumida — ver la cabecera)
    // ------------------------------------------------------------------

    private void SyncGeometry()
    {
        var root = _inner.XamlRoot;
        if (root is null || _inner.ActualWidth <= 0 || _inner.ActualHeight <= 0)
            return;

        double scale = root.RasterizationScale;
        var origin = _inner.TransformToVisual(root.Content)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        double headerH = _header.ActualHeight;

        int x = (int)Math.Round(origin.X * scale);
        int y = (int)Math.Round((origin.Y + headerH) * scale);
        int w = Math.Max(1, (int)Math.Round(_inner.ActualWidth * scale));
        int h = Math.Max(1, (int)Math.Round((_inner.ActualHeight - headerH) * scale));

        _huecoW = w;
        _huecoH = h;

        if (_hijo == 0)
            _hijo = HwndHijo.Crear(_hwndPropio, w, h, x, y);
        else
            HwndHijo.Colocar(_hijo, x, y, w, h);

        // El hijo del espejo sigue al de mpv como una sombra. NO se recrea
        // aquí el swapchain aunque el hueco haya cambiado: SizeChanged llega
        // en ráfaga mientras el operador arrastra el separador, y recrear un
        // swapchain por cada píxel sería un castigo. Lo hace el tic del PGM
        // (RefrescarEspejo), que compara _huecoW/H con _espejoW/H.
        if (_hijoEspejo != 0)
            HwndHijo.Colocar(_hijoEspejo, x, y, w, h);
    }

    // ------------------------------------------------------------------
    // Espejo del compositor (ver la cabecera del archivo)
    // ------------------------------------------------------------------

    /// <summary>Pone al día el espejo: pide el plan, ajusta el swapchain al
    /// hueco, engancha el <c>Mirror</c> al compositor de la salida anfitriona
    /// y publica la raíz que ese compositor tiene que pintar. Si no hay nada
    /// que espejar, sale del modo espejo y deja el monitor libre para su rama
    /// clásica (el mpv mudo). Idempotente: se llama en cada tic del PGM.
    ///
    /// <paramref name="etiqueta"/> es lo que va en la cabecera (el nombre del
    /// archivo al aire); vacío la deja como esté.</summary>
    public void RefrescarEspejo(string etiqueta = "")
    {
        var plan = PedirPlanEspejo?.Invoke();
        if (plan is not { } p || p.Regiones.Count == 0)
        {
            // No hay NADA que espejar: ni avería ni fallo, simplemente esta
            // fuente no tiene compositor. Quien llama decide qué enseñar.
            EspejoAveriado = false;
            SalirDelEspejo();
            return;
        }

        bool recreado = AsegurarSinkEspejo();
        if (_sinkEspejo is null)
        {
            // Hay plan pero no hay fontanería: AVERÍA, no «sin señal». El PGM
            // se va a su rama clásica — ver EspejoAveriado.
            EspejoAveriado = true;
            SalirDelEspejo();
            return;
        }
        EspejoAveriado = false;

        // La ANFITRIONA es la primera región. Su compositor es el que dibuja
        // el espejo entero, en su hilo y en su fotograma — por eso el espejo
        // va SIEMPRE colgado de un compositor vivo y nunca de uno propio.
        var anfitriona = p.Regiones[0].Salida;

        // La raíz se publica ANTES de enganchar: si se enganchara primero, el
        // hilo de render podría pillar un fotograma con Mirror puesto y la
        // raíz todavía en la del plan anterior.
        //
        // Puede venir VACÍA (todas las salidas del plan en blackout) y eso es
        // correcto: el espejo se queda enganchado y pinta negro. Salir del
        // espejo aquí devolvería el monitor a su mpv propio, enseñando el
        // vídeo que la sala NO está viendo.
        _raizEspejo = ConstruirRaizEspejo(p, anfitriona);

        if (recreado || !ReferenceEquals(_anfitriona, anfitriona))
        {
            // Quitárselo a la anterior ANTES de dárselo a la nueva: dos
            // compositores presentando en el mismo swapchain son dos hilos
            // sobre la misma superficie de presentación.
            if (_anfitriona is not null && !ReferenceEquals(_anfitriona, anfitriona))
                _anfitriona.FijarEspejo(null, null);
            _anfitriona = anfitriona;
            // FijarEspejo entra en Mutate: asignar Mirror/MirrorRoot con el
            // bucle de render corriendo es una carrera (regla de
            // OutputCompositor).
            _anfitriona.FijarEspejo(_sinkEspejo, LeerRaizEspejo);
        }

        EntrarEnEspejo(etiqueta);
    }

    /// <summary>Lo que el hilo de render lee en cada fotograma. Sin
    /// asignaciones: solo devuelve el campo volatile.</summary>
    private GraphNode? LeerRaizEspejo() => _raizEspejo;

    /// <summary>Desengancha el espejo AHORA MISMO, sin esperar al siguiente tic
    /// del PGM. Es lo PRIMERO que hay que hacer antes de tocar nada de lo que el
    /// espejo cuelga.
    ///
    /// POR QUÉ EXISTE, y por qué no basta con «apagar el compositor y ya»: en
    /// modo LIENZO la raíz del espejo lleva INSTANTÁNEAS de los nodos de las
    /// salidas AJENAS (ver <see cref="Instantanea"/>), y el hilo del compositor
    /// anfitrión las recorre hasta medio segundo más —lo que tarda el siguiente
    /// tic—. Si en ese hueco alguien dispone las FUENTES de esas salidas
    /// (VideoEngine.DiscardCompositorPipelines), ese hilo —que es un hilo que va
    /// A LA SALA— muestrea un SRV ya liberado: AccessViolation o
    /// DXGI_ERROR_DEVICE_REMOVED en mitad de una función. Los dos locks son
    /// distintos, así que no hay nada que lo serialice.
    ///
    /// <see cref="Dispose"/> ya hacía esto mismo lo primero, y por eso cerrar la
    /// aplicación siempre fue seguro; apagar el compositor EN CALIENTE no
    /// pasaba por aquí. Idempotente: llamarlo sin espejo puesto no hace
    /// nada.</summary>
    public void SoltarEspejo() => SalirDelEspejo();

    /// <summary>Crea (o recrea, si el hueco cambió de tamaño) el swapchain del
    /// espejo sobre su HWND hijo. Devuelve true si ha quedado uno NUEVO —
    /// entonces hay que volver a dárselo al compositor—. Un fallo deja
    /// <c>_sinkEspejo</c> a null, y quien llama lo convierte en
    /// <see cref="EspejoAveriado"/>: el monitor cae a su rama clásica, que es
    /// mejor que un PGM muerto.</summary>
    private bool AsegurarSinkEspejo()
    {
        SyncGeometry();
        if (_huecoW <= 0 || _huecoH <= 0)
        {
            // AÚN SIN LAYOUT. Lo reintenta el siguiente tic, sí — pero hasta
            // que llegue, quien llama nos convierte en EspejoAveriado y el PGM
            // se va a su rama clásica SIN decir por qué. Un monitor que cambia
            // de comportamiento en silencio es indistinguible de uno roto: se
            // avisa igual que en los otros dos fallos de fontanería.
            Averiado("El hueco del PGM todavía no está medido (sin layout).");
            return false;
        }

        int w = Math.Max(16, _huecoW);
        int h = Math.Max(16, _huecoH);
        if (_sinkEspejo is not null && w == _espejoW && h == _espejoH)
            return false;

        SoltarSinkEspejo();
        if (_hijoEspejo == 0)
        {
            try
            {
                _hijoEspejo = HwndHijo.Crear(_hwndPropio, w, h);
                SyncGeometry();                  // a su sitio, sobre el hueco
            }
            catch (InvalidOperationException exc)
            {
                Averiado($"No se pudo crear el HWND hijo del espejo ({exc.Message}).");
                _hijoEspejo = 0;
                return false;
            }
        }

        // El swapchain nace con la ventana YA VISIBLE. Uno creado sobre una
        // ventana oculta presenta OCCLUDED desde el primer fotograma, y salir
        // de ese estado depende de que DXGI se recupere solo al mostrarla —
        // no hay por qué apostar a eso pudiendo mostrarla antes. Solo se ve
        // vacía durante las tres líneas que faltan para EntrarEnEspejo, todas
        // en el mismo turno del hilo de interfaz: no da tiempo a repintar.
        MostrarHijo(_hijoEspejo);

        try
        {
            _sinkEspejo = new SwapChainSink(GpuDevice.Shared, _hijoEspejo, w, h)
            {
                // SIN vsync, y no es un descuido. Present(1) BLOQUEA hasta el
                // vsync de su pantalla, y aquí quien presenta es el hilo del
                // compositor DE SALA: con intervalo 1 ese hilo esperaría dos
                // vsyncs por fotograma (el de la sala y el del monitor de
                // control) y la salida perdería la mitad del ritmo. El precio
                // es tearing en el PGM, que es un monitor.
                SyncInterval = 0,
            };
            _espejoW = w;
            _espejoH = h;
            // El rearme del aviso NO va aquí: cuelga de EspejoAveriado, que es
            // quien sabe si seguimos averiados. Ver su comentario.
            return true;
        }
        catch (Exception exc)
        {
            Averiado($"No se pudo crear el swapchain del espejo a {w}x{h} " +
                $"({exc.Message}).");
            _sinkEspejo = null;
            _espejoW = _espejoH = 0;
            return false;
        }
    }

    /// <summary>Deja constancia de una avería del espejo: al log SIEMPRE (sin
    /// rastro escrito, un fallo en la máquina de un cliente es indiagnosticable
    /// después de la función) y a la barra de estado UNA sola vez — el tic del
    /// PGM reintenta cada medio segundo y no puede martillear al operador.
    ///
    /// El texto dice lo que de verdad pasa a continuación: el monitor vuelve a
    /// su mpv propio. Que este aviso y el código digan lo mismo no es celo: la
    /// cabecera vieja de este archivo llegó a afirmar lo contrario de lo que
    /// hacía, y un comentario que miente es peor que no tenerlo.</summary>
    private void Averiado(string motivo)
    {
        string aviso = $"[PGM] {motivo} El monitor vuelve a su mpv propio " +
            "(puede diferir en opacidad y blackout de lo que sale a sala).";
        VideoOutputWindow.LogAviso(aviso);
        if (_averiaAvisada)
            return;
        _averiaAvisada = true;
        AvisoEnPantalla?.Invoke($"[AVISO] {aviso}");
    }

    /// <summary>El árbol que el compositor anfitrión pinta en el espejo: una
    /// StackNode con un TransformNode por región, colocado en el hueco del
    /// monitor. Donde no cubre ninguna región, el Clear del compositor deja
    /// negro.
    ///
    /// BLACKOUT: una región en negro simplemente NO SE DIBUJA, y ese hueco se
    /// queda con el Clear. Es lo que hace que el PGM no mienta justo cuando
    /// más importa (tecla B, PANIC): un monitor que dice «esto es lo que sale»
    /// y sigue enseñando vídeo con la sala a oscuras es peor que no tenerlo.
    /// Se resuelve POR REGIÓN, así que en modo LIENZO el blackout de una
    /// salida apaga SU trozo del lienzo y deja el resto: el operador ve
    /// exactamente qué pantallas están en negro. Y se resuelve SOLO EN EL
    /// ESPEJO — el blackout de la sala sigue siendo ocultar el HWND hijo
    /// (VideoOutputWindow.SetBlackout): instantáneo y con negro garantizado
    /// aunque el compositor se atasque, que en un teatro vale más que la
    /// elegancia de un nodo de velo en el grafo.
    ///
    /// Devuelve una StackNode VACÍA cuando todas las regiones están en negro,
    /// y no null: null haría salir del espejo, y el monitor volvería a su mpv
    /// propio enseñando el vídeo que la sala no está viendo. Vacía = negro,
    /// que es lo correcto.
    ///
    /// SOLO TransformNode/StackNode a propósito: el RtPool del compositor está
    /// dimensionado al sink PRINCIPAL, y esos dos nodos no piden render
    /// targets intermedias. Un nodo que sí las pidiera saldría aquí con el
    /// tamaño equivocado.</summary>
    private GraphNode ConstruirRaizEspejo(
        (int LienzoW, int LienzoH, IReadOnlyList<RegionEspejo> Regiones) plan,
        SalidaGpu anfitriona)
    {
        // Encaje UNIFORME, con bandas donde sobre: el lienzo (o la salida) no
        // tiene por qué ser 16:9 como la caja del PGM, y estirar mentiría
        // sobre el encuadre que ve la sala.
        double escala = Math.Min(
            (double)_espejoW / Math.Max(1, plan.LienzoW),
            (double)_espejoH / Math.Max(1, plan.LienzoH));
        double offX = (_espejoW - plan.LienzoW * escala) / 2.0;
        double offY = (_espejoH - plan.LienzoH * escala) / 2.0;

        var raiz = new StackNode();
        foreach (var region in plan.Regiones)
        {
            if (region.Negro)
                continue;   // blackout: su hueco se queda con el Clear
            // La ANFITRIONA va en VIVO (mismo lock que el hilo que dibuja);
            // de las AJENAS, una instantánea — ver la cabecera del archivo.
            GraphNode? contenido = ReferenceEquals(region.Salida, anfitriona)
                ? region.Salida.Compositor?.Root
                : Instantanea(region.Salida);
            if (contenido is null)
                continue;
            raiz.Children.Add(new TransformNode(contenido)
            {
                Dest = new DestRect(
                    (int)Math.Round(offX + region.X * escala),
                    (int)Math.Round(offY + region.Y * escala),
                    Math.Max(1, (int)Math.Round(region.W * escala)),
                    Math.Max(1, (int)Math.Round(region.H * escala))),
            });
        }
        return raiz;
    }

    /// <summary>Copia de los hijos del grafo de una salida AJENA, tomada
    /// DENTRO de su propio Mutate. Es lo que impide que el hilo del compositor
    /// anfitrión recorra por índice una lista que otro dueño está mutando —
    /// ver la cabecera del archivo.
    ///
    /// EL RETRASO QUE ESTO CUESTA, ANOTADO A PROPÓSITO: la copia se congela
    /// hasta el siguiente refresco, o sea hasta medio segundo (el tic del
    /// PGM). Lo que se retrasa es QUÉ CAPAS hay en esa salida ajena — un cue
    /// que acaba de entrar puede tardar ese medio segundo en aparecer en su
    /// trozo del lienzo—; los PÍXELES de cada capa van al día, porque se
    /// muestrea la superficie viva. Con una sola salida habilitada no hay
    /// instantánea ninguna y no hay retraso.
    ///
    /// Y NO se refresca más a menudo a propósito: hacerlo en el tic de 100 ms
    /// del controlador metería un Mutate a 10 Hz sobre los locks de los hilos
    /// que van a SALA, y esos no se tocan por un monitor.</summary>
    private static GraphNode? Instantanea(SalidaGpu salida)
    {
        StackNode? copia = null;
        salida.Compositor?.Mutate(c =>
        {
            if (c.Root is not StackNode viva)
                return;
            var nueva = new StackNode();
            for (int i = 0; i < viva.Children.Count; i++)
                nueva.Children.Add(viva.Children[i]);
            copia = nueva;
        });
        return copia;
    }

    private void EntrarEnEspejo(string etiqueta)
    {
        if (!_espejando)
        {
            StopPlayback();                       // suelta el decode del monitor
            if (_hijo != 0)
                HwndHijo.Ocultar(_hijo);          // el HWND de mpv taparía el espejo
            MostrarHijo(_hijoEspejo);
            _message.Visibility = Visibility.Collapsed;
            _espejando = true;
        }
        if (etiqueta.Length > 0 &&
            !string.Equals(_status.Text, etiqueta, StringComparison.Ordinal))
            _status.Text = etiqueta;
    }

    /// <summary>Sale del modo espejo y suelta lo suyo. El HWND hijo del espejo
    /// SOBREVIVE (crearlo cuesta y el siguiente cue lo reutiliza); lo que se
    /// suelta es el swapchain, porque un swapchain vivo sobre un HWND oculto
    /// es memoria de vídeo para nada.</summary>
    private void SalirDelEspejo()
    {
        if (!_espejando && _anfitriona is null && _sinkEspejo is null)
            return;
        SoltarSinkEspejo();
        _raizEspejo = null;
        _espejando = false;
        // Se oculta SIEMPRE, no solo al salir de un espejo en marcha: si el
        // swapchain no llegó a nacer, la ventana ya se había mostrado (ver
        // AsegurarSinkEspejo) y quedaría tapando la de mpv, vacía.
        if (_hijoEspejo != 0)
            HwndHijo.Ocultar(_hijoEspejo);
        // El mpv del monitor vuelve a mandar. No se muestra su HWND aquí:
        // StopPlayback dejó _currentPath vacío al entrar en el espejo, así que
        // el siguiente ShowVideo lo tratará como archivo nuevo y hará el
        // Mostrar él mismo — mismo camino que un arranque en frío.
    }

    private void SoltarSinkEspejo()
    {
        // ORDEN OBLIGATORIO: quitarle el espejo al compositor ANTES de soltar
        // el sink. FijarEspejo entra en Mutate, así que cuando vuelve el hilo
        // de render ya no lo tiene; al revés estaría presentando en una RTV
        // recién liberada. Mismo orden que SalidaGpu.Redimensionar.
        _anfitriona?.FijarEspejo(null, null);
        _anfitriona = null;
        _sinkEspejo?.Dispose();
        _sinkEspejo = null;
        _espejoW = _espejoH = 0;
    }

    // ------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------

    /// <summary>Reproduce (en silencio) el archivo dado desde startAt.
    /// Si ya es el archivo actual, solo re-sincroniza el reloj.</summary>
    public void ShowVideo(string path, double startAt, bool loop)
    {
        if (!VideoEngine.MpvAvailable)
            return;
        // Las dos ramas son EXCLUYENTES: los dos HWND hijos ocupan el MISMO
        // hueco, así que dejarlas vivas a la vez sería decodificar debajo de
        // una imagen que nadie ve (o al revés). Quien llama ya lo sabe —
        // MainWindow solo llega aquí si Espejando es false—; esto es el
        // cinturón, por si algún camino futuro entra por otro lado.
        SalirDelEspejo();
        if (!EnsurePlayer())
            return;

        if (path != _currentPath)
        {
            _currentPath = path;
            _lastElapsed = -1;
            _cachedDuration = 0.0;
            _durationAsked = false;
            // loadfile arranca REPRODUCIENDO — mismo motivo que WinCue.App
            // (ver su comentario en ShowVideo): el estado es "sonando", no
            // "desconocido".
            _pauseState = false;
            try
            {
                _player!.SetProperty("loop-file", loop ? "inf" : "no");
                _player.SetProperty("start", startAt > 0.2
                    ? startAt.ToString("F3", CultureInfo.InvariantCulture) : "none");
                _player.Command("loadfile", path);
            }
            catch (Exception) { return; }
            MostrarHijo(_hijo);
            _message.Visibility = Visibility.Collapsed;
            _status.Text = System.IO.Path.GetFileName(path);
        }
        else
        {
            SyncClock(startAt);
        }
    }

    /// <summary>Enseña un fotograma PARADO del archivo, para el scrub del
    /// cursor de edición del timeline. Mismo contrato que
    /// WinCue.App.LiveMonitorControl.ShowPaused.
    ///
    /// La diferencia con ShowVideo no es cosmética: aquí se carga con
    /// <c>pause</c> puesto ANTES del loadfile, así que el archivo nunca llega
    /// a sonar. Cargar y pausar después dejaría escapar unas décimas de audio
    /// y vídeo cada vez que el operador mueve el cursor.</summary>
    public void ShowPaused(string path, double at)
    {
        if (!VideoEngine.MpvAvailable || !EnsurePlayer())
            return;

        if (path != _currentPath)
        {
            _currentPath = path;
            _lastElapsed = -1;
            _cachedDuration = 0.0;
            _durationAsked = false;
            // Este camino carga PAUSADO, así que el estado se conoce.
            _pauseState = true;
            try
            {
                _player!.SetProperty("loop-file", "no");
                _player.SetProperty("pause", true);
                _player.SetProperty("start", at > 0.05
                    ? at.ToString("F3", CultureInfo.InvariantCulture) : "none");
                _player.Command("loadfile", path);
            }
            catch (Exception) { return; }
            MostrarHijo(_hijo);
            _message.Visibility = Visibility.Collapsed;
            _status.Text = System.IO.Path.GetFileName(path);
        }
        else
        {
            Scrub(at);
        }
    }

    /// <summary>Salta (pausado) al segundo dado — fotograma de referencia.
    /// Mismo contrato que WinCue.App.LiveMonitorControl.Scrub.</summary>
    public void Scrub(double seconds)
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
            _player.SetProperty("pause", true);
            _pauseState = true;
            _player.Command("seek",
                Math.Max(0.0, seconds).ToString("F3", CultureInfo.InvariantCulture),
                "absolute");
        }
        catch (Exception) { }
    }

    // ── Transporte del PVW (no toca la sala) ───────────────────────────

    public void PreviewPlay()
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try { _player.SetProperty("pause", false); _pauseState = false; }
        catch (Exception) { }
    }

    public void PreviewPause()
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try { _player.SetProperty("pause", true); _pauseState = true; }
        catch (Exception) { }
    }

    /// <summary>true si el previo está pausado (o no hay nada cargado).
    /// Propiedad OBSERVADA: no consulta al core.</summary>
    public bool PreviewIsPaused =>
        _player is null || _currentPath.Length == 0 || (_pauseState ?? true);

    /// <summary>Segundo por el que va el previo, o 0 si no hay nada.
    /// OBSERVADA (push de mpv) + extrapolada, NUNCA consultada al core en el
    /// hilo de UI — mismo motivo que WinCue.App (ver su ObservedTimePosSmooth):
    /// una llamada síncrona con el core ocupado bloquea la interfaz.</summary>
    public double PreviewPosition =>
        _player is null || _currentPath.Length == 0
            ? 0.0 : _player.ObservedTimePosSmooth() ?? 0.0;

    /// <summary>Duración del archivo cargado (0 = aún no se sabe). Cacheada
    /// POR ARCHIVO y pedida UNA vez — mismo motivo que WinCue.App.</summary>
    public double PreviewDuration
    {
        get
        {
            if (_player is null || _currentPath.Length == 0)
                return 0.0;
            if (_cachedDuration <= 0.0 && !_durationAsked)
            {
                _durationAsked = true;
                try { _cachedDuration = _player.GetPropertyDouble("duration") ?? 0.0; }
                catch (Exception) { return 0.0; }
            }
            return _cachedDuration;
        }
    }

    public void PreviewSeek(double seconds)
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
            _player.Command("seek",
                Math.Max(0.0, seconds).ToString("F3", CultureInfo.InvariantCulture),
                "absolute");
        }
        catch (Exception) { }
    }

    /// <summary>Re-sincroniza el monitor con el reloj real del motor (PGM).
    /// Posición = observada (push); duración = cacheada por archivo; pause =
    /// solo al CAMBIAR de estado — mismo contrato que WinCue.App.SyncClock
    /// (ver su comentario sobre por qué esto NUNCA puede llamar al core de
    /// forma síncrona desde el hilo de UI).</summary>
    public void SyncClock(double elapsed)
    {
        if (_player is null || _currentPath.Length == 0)
            return;
        try
        {
            bool advancing = _lastElapsed < 0 || elapsed > _lastElapsed + 0.02;
            bool wantPause = !advancing;
            if (_pauseState != wantPause)
            {
                _player.SetProperty("pause", wantPause);
                _pauseState = wantPause;
            }
            _lastElapsed = elapsed;

            if (_cachedDuration <= 0.0)
                _cachedDuration = _player.GetPropertyDouble("duration") ?? 0.0;

            if (_cachedDuration > 0.5)
            {
                double? pos = _player.ObservedTimePos;
                if (pos.HasValue && Math.Abs(pos.Value - elapsed) > 0.35)
                    _player.Command("seek",
                        elapsed.ToString("F3", CultureInfo.InvariantCulture), "absolute");
            }
        }
        catch (Exception) { /* el monitor nunca debe romper el show */ }
    }

    public void Clear(string message)
    {
        SalirDelEspejo();
        StopPlayback();
        _message.Text = message;
        _message.Visibility = Visibility.Visible;
        _status.Text = "";
        if (_hijo != 0)
            HwndHijo.Ocultar(_hijo);
    }

    // ------------------------------------------------------------------
    // Mitigación del diálogo modal (ver _tapadoPorDialogo)
    // ------------------------------------------------------------------

    /// <summary>Muestra el hijo… salvo que haya un diálogo modal encima. TODOS
    /// los caminos que enseñan un hijo pasan por aquí, y no por
    /// HwndHijo.Mostrar directamente: el tic del PGM corre cada 500 ms por
    /// debajo del diálogo y volvería a destaparlo sin esto.</summary>
    private void MostrarHijo(nint hijo)
    {
        if (hijo == 0 || _tapadoPorDialogo)
            return;
        HwndHijo.Mostrar(hijo);
    }

    /// <summary>Se abre un diálogo modal encima: los dos hijos se apartan.
    /// Idempotente (varios monitores, y el mismo monitor dos veces).</summary>
    public void OcultarPorDialogo()
    {
        if (_tapadoPorDialogo)
            return;
        _tapadoPorDialogo = true;
        if (_hijo != 0)
            HwndHijo.Ocultar(_hijo);
        if (_hijoEspejo != 0)
            HwndHijo.Ocultar(_hijoEspejo);
    }

    /// <summary>El diálogo se ha cerrado. Se vuelve a enseñar el hijo que toque
    /// AHORA, no el que estaba antes: mientras el diálogo estaba abierto el tic
    /// del PGM ha podido cambiar de rama (espejo ↔ mpv clásico) o dejar el
    /// monitor en NEGRO, y restaurar la foto vieja enseñaría un hueco que ya no
    /// corresponde. Si no hay nada que enseñar, no se enseña nada — que es lo
    /// mismo que hace Clear.</summary>
    public void MostrarTrasDialogo()
    {
        if (!_tapadoPorDialogo)
            return;
        _tapadoPorDialogo = false;
        if (_espejando)
            MostrarHijo(_hijoEspejo);
        else if (_currentPath.Length > 0)
            MostrarHijo(_hijo);
    }

    /// <summary>Para lo que estuviera reproduciendo (sin destruir el HWND ni
    /// el player: crearlos cuesta y el siguiente cue los reutiliza).</summary>
    private void StopPlayback()
    {
        _currentPath = "";
        _lastElapsed = -1;
        _cachedDuration = 0.0;
        _durationAsked = false;
        _pauseState = null;
        try { _player?.Command("stop"); } catch (Exception) { }
    }

    /// <summary>Libera la instancia mpv y destruye el HWND hijo (al cerrar la
    /// app — mismo momento que VideoOutputWindow.CloseForShutdown).</summary>
    public void Dispose()
    {
        // El espejo PRIMERO: mientras siga enganchado, un compositor de sala
        // está presentando en su swapchain, y ese swapchain vive sobre el HWND
        // que se destruye aquí abajo.
        try { SalirDelEspejo(); } catch (Exception) { }
        try { _player?.Dispose(); } catch (Exception) { }
        _player = null;
        if (_hijo != 0)
        {
            try { HwndHijo.Destruir(_hijo); } catch (Exception) { }
            _hijo = 0;
        }
        if (_hijoEspejo != 0)
        {
            try { HwndHijo.Destruir(_hijoEspejo); } catch (Exception) { }
            _hijoEspejo = 0;
        }
    }

    // ------------------------------------------------------------------
    // mpv embebido
    // ------------------------------------------------------------------

    private bool EnsurePlayer()
    {
        if (_player is not null)
            return true;
        SyncGeometry();
        if (_hijo == 0)
            return false;   // aún sin layout: reintentará el siguiente tick
        try
        {
            var options = new Dictionary<string, string>
            {
                ["wid"] = _hijo.ToString(),
                ["vo"] = "gpu",
                ["hwdec"] = "auto",
                ["ao"] = "null",              // monitor SIEMPRE mudo
                ["mute"] = "yes",
                ["keep-open"] = "yes",
                ["hr-seek"] = "yes",
                ["keepaspect"] = "yes",
                ["osc"] = "no",
                ["input-default-bindings"] = "no",
                ["input-vo-keyboard"] = "no",
            };
            _player = new MpvPlayer(options);
            return true;
        }
        catch (Exception)
        {
            _message.Text = "monitor no disponible";
            _message.Visibility = Visibility.Visible;
            _player = null;
            return false;
        }
    }
}
