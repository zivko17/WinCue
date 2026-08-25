// =============================================================================
// SalidaGpu.cs — El compositor GPU de UNA salida de video
// =============================================================================
// Un swapchain sobre el HWND HIJO que la ventana de salida ya crea, un
// OutputCompositor con su hilo y su reloj, y una StackNode como raiz. Uno por
// salida: el compositor presenta a vsync de SU pantalla, y dos pantallas no
// tienen por que ir al mismo ritmo.
//
// POR QUE SOBRE EL HWND HIJO Y NO UN SwapChainPanel: en modo compositor mpv
// arranca con vo=libmpv (sin ventana, ver VideoEngine.StartPlayback), asi que
// ese HWND —que en modo clasico es el wid de mpv— queda libre. Reutilizarlo
// mantiene intactos el blackout y el rotulo de identificar, que funcionan
// ocultando y mostrando ese mismo HWND (ver HwndHijo.cs): el swapchain se va
// con el, sin una sola linea nueva.
//
// UN SOLO DEVICE D3D11 EN TODA LA APP (GpuDevice.Shared): las texturas de las
// fuentes y las render targets de las salidas tienen que estar en el mismo
// device para poder muestrearse en un mismo paso. Esa es la regla que hace
// posible el compositor.
//
// HILOS:
//   · Todo lo publico de esta clase es del HILO PRINCIPAL.
//   · El hilo de render lo lleva OutputCompositor. La UNICA forma legal de
//     tocar el grafo desde fuera es Compositor.Mutate(...) — por eso Insertar,
//     Quitar y FijarOpacidad lo envuelven todo ahi dentro. Tocar
//     StackNode.Children fuera de Mutate es una carrera, y su propio
//     comentario lo dice.
//
// AL REDIMENSIONAR: se RECREA el swapchain (sink + compositor), no se
// redimensiona a mano. Un ResizeBuffers exige soltar antes TODAS las
// referencias al back buffer —RTV incluida—, y esa RTV la tiene cogida el
// hilo de render en mitad de un fotograma: recrear es la unica forma sin
// carrera de conseguirlo con esta arquitectura. La raiz del grafo sobrevive,
// asi que las capas siguen donde estaban.
// =============================================================================

using System.Diagnostics;
using WinCue.Video.Gpu;

namespace Secuence.App;

internal sealed class SalidaGpu : IDisposable
{
    private readonly nint _hwnd;

    /// <summary>Raiz del grafo. SOBREVIVE a las recreaciones del swapchain:
    /// las capas no se enteran de que la ventana ha cambiado de tamano.</summary>
    private readonly StackNode _raiz = new();

    /// <summary>Las capas que estan AHORA en el grafo, en el mismo orden que
    /// _raiz.Children. Sin esta lista paralela no se sabe la pista de cada
    /// hijo, y el orden de pista es todo el contrato de la StackNode.</summary>
    private readonly List<CapaCompositor> _enElGrafo = new();

    private readonly List<CapaCompositor> _capas = new();

    /// <summary>Reloj maestro de esta salida. Es el que evalua las rampas de
    /// opacidad Y el que reciben las fuentes: por eso un fotograma entero
    /// corresponde a UN instante. Nuestro y no el interno del compositor
    /// porque FijarOpacidad necesita LEERLO para agendar la rampa.</summary>
    private readonly Stopwatch _reloj = Stopwatch.StartNew();

    private SwapChainSink? _sink;
    private OutputCompositor? _compositor;

    /// <summary>Algun compositor de esta salida NO murio dentro de su Join:
    /// puede quedar un hilo zombi recorriendo _raiz.Children. Se pega para
    /// siempre a proposito — despues ya nadie puede certificar que ese hilo
    /// murio—, y lo unico que cuesta es no vaciar el grafo de una salida que
    /// de todas formas ya esta rota. Ver VaciarGrafo.</summary>
    private bool _hiloZombi;
    private int _ancho;
    private int _alto;
    private bool _dispuesta;

    /// <param name="hwnd">HWND hijo de la ventana de salida (el que en modo
    /// clasico seria el wid de mpv).</param>
    public SalidaGpu(nint hwnd, int width, int height)
    {
        if (hwnd == 0)
            throw new ArgumentException("HWND de salida no valido.", nameof(hwnd));
        _hwnd = hwnd;
        // Sin try/catch a proposito: si el swapchain no nace, quien pidio la
        // capa (VideoEngine) tiene que ENTERARSE para caer a la ruta clasica.
        Construir(width, height);
    }

    /// <summary>El compositor vivo, o null si una recreacion del swapchain
    /// fallo (salida rota: no pinta, pero la app sigue en pie). Nullable a
    /// proposito — ver Redimensionar.</summary>
    public OutputCompositor? Compositor => _compositor;

    public IReadOnlyList<CapaCompositor> Capas => _capas;

    /// <summary>Aviso VISIBLE (barra de estado). Lo pone la ventana de salida.
    /// Un fallo del compositor que solo deje rastro en un log es un fallo que
    /// el operador descubre cuando ya no hay imagen y no sabe por que — el
    /// resto del motor avisa por ErrorOccurred, y esto es su equivalente para
    /// lo que ocurre por debajo de VideoEngine (una recreacion de swapchain,
    /// que no cuelga de ningun cue). Se invoca en el hilo principal.</summary>
    public Action<string>? AvisoEnPantalla { get; set; }

    public int Ancho => _ancho;
    public int Alto => _alto;

    private void Construir(int width, int height)
    {
        _ancho = Math.Max(16, width);
        _alto = Math.Max(16, height);

        var sink = new SwapChainSink(GpuDevice.Shared, _hwnd, _ancho, _alto);
        try
        {
            var compositor = new OutputCompositor(
                GpuDevice.Shared, sink, () => _reloj.Elapsed.TotalSeconds);
            // Root y el espejo se fijan ANTES de Start(): con el bucle ya
            // corriendo serian una carrera (regla de OutputCompositor).
            compositor.Root = _raiz;
            compositor.Mirror = _espejo;
            compositor.MirrorRoot = _espejoRaiz;
            compositor.Start();
            _compositor = compositor;
            _sink = sink;
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }

    // ── Espejo (lo consume la Task 8; aqui solo se conserva) ──────────

    private IOutputSink? _espejo;
    private Func<GraphNode?>? _espejoRaiz;

    /// <summary>Sink secundario del compositor (el PGM). Se guarda aqui
    /// ademas de en el compositor para poder devolverselo al compositor NUEVO
    /// cuando una recreacion del swapchain se lleve por delante al viejo.
    /// La asignacion va dentro de Mutate: con el bucle corriendo, tocar
    /// Mirror es una carrera.</summary>
    public void FijarEspejo(IOutputSink? espejo, Func<GraphNode?>? raiz)
    {
        _espejo = espejo;
        _espejoRaiz = raiz;
        _compositor?.Mutate(c =>
        {
            c.Mirror = espejo;
            c.MirrorRoot = raiz;
        });
    }

    // ── Capas ─────────────────────────────────────────────────────────

    /// <summary>Una capa nueva para un slot del pool de VideoEngine. NO entra
    /// en el grafo hasta que un cue la tome (AssignCue).</summary>
    public CapaCompositor NuevaCapa(int layer, int slotSeq)
    {
        var capa = new CapaCompositor(this, layer, slotSeq);
        _capas.Add(capa);
        return capa;
    }

    /// <summary>Mete la capa en el grafo EN SU SITIO. El orden de la lista es
    /// el orden de dibujo y el ULTIMO queda encima, asi que las pistas se
    /// ordenan DESCENDENTE: la 8 (el fondo) primero y la 1 (la que tapa) al
    /// final. Dentro de una misma pista, la ultima en entrar queda encima —
    /// mismo criterio que el z de WinCue (VideoOutputWindow.NextZ).</summary>
    internal void Insertar(CapaCompositor capa)
    {
        if (_dispuesta || capa.Nodo is null)
            return;
        // Salida ROTA (una recreacion de swapchain que fallo): LANZAR, no
        // no-op silencioso. Asi VideoEngine marca _gpuFailed y el cue sale por
        // la ruta clasica, en vez de quedarse en un grafo que nadie pinta.
        if (_compositor is not { } comp)
            throw new InvalidOperationException(
                "Esta salida se quedo sin compositor GPU (el swapchain no se " +
                "pudo recrear).");
        comp.Mutate(_ =>
        {
            // Plena opacidad y sin rampa: la capa es un slot REUTILIZADO del
            // pool y puede traer la rampa del cue anterior — el porque entero
            // (y su prueba) viven en OpacityNode.Reset().
            capa.ReiniciarOpacidad();
            InsertarOrdenado(capa);
        });
    }

    /// <summary>LLAMAR SIEMPRE DENTRO DE Mutate. Idempotente: una capa que ya
    /// esta en el grafo no entra dos veces.</summary>
    private void InsertarOrdenado(CapaCompositor capa)
    {
        if (capa.Nodo is not { } nodo || _enElGrafo.Contains(capa))
            return;
        int i = 0;
        while (i < _enElGrafo.Count && _enElGrafo[i].Layer >= capa.Layer)
            i++;
        _enElGrafo.Insert(i, capa);
        _raiz.Children.Insert(i, nodo);
    }

    internal void Quitar(CapaCompositor capa)
    {
        if (_dispuesta)
            return;
        _compositor?.Mutate(_ => QuitarDelGrafo(capa));
    }

    /// <summary>LLAMAR SIEMPRE DENTRO DE Mutate.</summary>
    private bool QuitarDelGrafo(CapaCompositor capa)
    {
        int i = _enElGrafo.IndexOf(capa);
        if (i < 0)
            return false;
        _enElGrafo.RemoveAt(i);
        _raiz.Children.RemoveAt(i);
        return true;
    }

    /// <summary>La capa se murio del todo (su pipeline ya no existe): fuera
    /// del inventario, para que la lista no crezca durante toda la sesion.</summary>
    internal void Olvidar(CapaCompositor capa) => _capas.Remove(capa);

    // ── Opacidad ──────────────────────────────────────────────────────

    /// <summary>Opacidad (0..1) del video de un cue en ESTA salida, con
    /// fundido opcional. Un cue puede ocupar VARIAS capas aqui (modo canvas,
    /// o un cruce que todavia arrastra la saliente): todas se mueven juntas.
    ///
    /// El fundido es una rampa en el reloj del compositor, no una animacion
    /// de la interfaz — evaluada en el mismo instante que las fuentes.</summary>
    public void FijarOpacidad(int cueId, double opacidad, double fadeSegundos)
    {
        if (_dispuesta || _compositor is null)
            return;
        float destino = (float)Math.Clamp(opacidad, 0.0, 1.0);
        _compositor.Mutate(_ =>
        {
            // El instante se lee DENTRO del lock: fuera, el hilo de render
            // podria colarse un fotograma entre la lectura y el alta de la
            // rampa, y el fundido arrancaria un pelo en el pasado.
            double ahora = _reloj.Elapsed.TotalSeconds;
            for (int i = 0; i < _capas.Count; i++)
            {
                var capa = _capas[i];
                if (capa.CueId != cueId)
                    continue;
                if (fadeSegundos > 0.0)
                {
                    // DESDE lo que se esta viendo, no desde el valor final de
                    // un fundido anterior: si no, interrumpir un cruce daria
                    // un salto de luz.
                    float desde = capa.OpacidadEn(ahora);
                    capa.FijarOpacidad(destino, new Ramp(ahora, fadeSegundos, desde, destino));
                }
                else
                {
                    capa.FijarOpacidad(destino, null);   // corte, sin rampa
                }
            }
        });
    }

    /// <summary>Sube las capas de un cue al frente de SU pista. Lo llama el
    /// GO: con la precarga en STANDBY, una capa puede haber entrado en el
    /// grafo mucho antes de salir al aire, y por debajo de videos que
    /// arrancaron despues. Mismo papel que RaiseCueToFront en WinCue.</summary>
    public void SubirAlFrente(int cueId)
    {
        if (_dispuesta || _compositor is null)
            return;
        _compositor.Mutate(_ =>
        {
            for (int i = 0; i < _capas.Count; i++)
            {
                var capa = _capas[i];
                if (capa.CueId != cueId)
                    continue;
                if (QuitarDelGrafo(capa))
                    InsertarOrdenado(capa);   // vuelve al final de su pista
            }
        });
    }

    // ── Tamano de la ventana ──────────────────────────────────────────

    /// <summary>La ventana de salida cambio de tamano (o de monitor): se
    /// RECREA el swapchain. Ver la cabecera para el porque de recrear en vez
    /// de redimensionar.
    ///
    /// No propaga: aqui ya no hay a quien caerse. Si la recreacion falla, la
    /// salida se queda sin compositor (negra) y queda escrito en el log — que
    /// es mucho mejor que tirar la app en mitad de una funcion porque el
    /// operador movio una ventana.</summary>
    public void Redimensionar(int width, int height)
    {
        if (_dispuesta)
            return;
        int w = Math.Max(16, width);
        int h = Math.Max(16, height);
        if (w == _ancho && h == _alto && _compositor is not null)
            return;

        // ORDEN OBLIGATORIO: primero parar el hilo de render (Dispose del
        // compositor lo hace y espera a que termine), y solo despues soltar
        // el sink. Al reves, el hilo estaria pintando en una RTV liberada.
        //
        // EL GRAFO NO SE TOCA AQUI. La raiz sobrevive a la recreacion —es lo
        // que promete la cabecera de este fichero— y el compositor nuevo la
        // recibe con sus capas donde estaban. Vaciarla antes de Construir
        // (como llego a hacer este metodo) borraba el video AL AIRE: no queda
        // ningun camino que vuelva a meter las capas, porque InsertarOrdenado
        // solo se alcanza desde AssignCue —que no se repite para un cue que ya
        // esta sonando— y desde SubirAlFrente, que exige que la capa ya
        // estuviera dentro. Resultado: negro hasta el siguiente GO de esa
        // pista, con solo mover o redimensionar la ventana de salida.
        //
        // Se apunta si el hilo de render llego a MORIR de verdad: Dispose()
        // hace Join(2 s) y no mira el resultado, y del veredicto depende que el
        // grafo se pueda tocar sin Mutate ahi abajo, en el catch.
        if (_compositor is { } viejo)
        {
            viejo.Dispose();
            if (!viejo.RenderThreadStopped)
                _hiloZombi = true;
        }
        _compositor = null;
        _sink?.Dispose();
        _sink = null;

        try
        {
            Construir(w, h);
        }
        catch (Exception exc)
        {
            // AQUI SI: la salida se ha quedado ROTA, y el grafo tiene que
            // quedar coherente — sin nodos zombis dentro de _raiz.Children ni
            // capas creyendose dentro de un grafo que ya nadie pinta.
            //
            // Aqui NO hay compositor por construccion (se acaba de poner a
            // null y Construir ha fallado), asi que VaciarGrafo no puede usar
            // Mutate: toca la lista a pelo. Eso solo es legitimo si el hilo de
            // render del compositor VIEJO murio, y por eso se le pregunto
            // arriba en vez de darlo por hecho. Si no murio, el grafo se deja
            // como esta: son unos nodos de mas en una salida que ya no pinta
            // —memoria, hasta el siguiente Construir o el Dispose—, mientras
            // que vaciarlo bajo los pies de StackNode.Render seria un
            // IndexOutOfRange en un hilo de fondo, o sea la app entera abajo
            // en mitad de una funcion.
            bool vaciado = VaciarGrafo(hiloDeRenderParado: !_hiloZombi);
            // Y hay que decirlo por los tres sitios: al log, a la barra de
            // estado, y —lo importante— a quien pida capa a partir de ahora,
            // que se la encontrara lanzando (Insertar) y caera a la ruta
            // clasica en vez de quedarse en negro para siempre.
            string aviso = $"Compositor GPU de esta salida perdido al recrear el " +
                $"swapchain a {w}x{h} ({exc.Message}). Los cues nuevos volveran a " +
                "la ruta clasica." +
                (vaciado ? "" : " Ademas, el hilo de render anterior no respondio " +
                    "al Join: el grafo se deja intacto a proposito.");
            VideoOutputWindow.LogAviso(aviso);
            AvisoEnPantalla?.Invoke($"[AVISO] {aviso}");
        }
    }

    /// <summary>Vacia el grafo SIN carrera con el hilo de render. Devuelve
    /// false si NO se ha podido vaciar y el grafo se ha dejado intacto.
    ///
    /// Con compositor vivo va dentro de Mutate — la unica forma legal de tocar
    /// StackNode.Children — y entonces el parametro sobra.
    ///
    /// Sin compositor no hay Mutate con quien serializar, y «ya lo he
    /// dispuesto» NO es prueba de que su hilo haya muerto: OutputCompositor.
    /// Dispose() hace Join(2 s) y no mira el resultado (a proposito: colgar la
    /// app esperando a una GPU atascada seria peor). Por eso el veredicto lo
    /// trae quien llama, leido de OutputCompositor.RenderThreadStopped justo
    /// despues de disponer. Con el hilo muerto, tocar la lista a pelo es
    /// seguro; con el hilo vivo NO se toca — vaciarla mientras StackNode.
    /// Render la recorre por indice tira la aplicacion.</summary>
    /// <param name="hiloDeRenderParado">Solo cuenta cuando ya no hay
    /// compositor. Por defecto false: quien no pueda demostrarlo no vacia.
    /// </param>
    private bool VaciarGrafo(bool hiloDeRenderParado = false)
    {
        if (_compositor is { } c)
        {
            c.Mutate(_ =>
            {
                _raiz.Children.Clear();
                _enElGrafo.Clear();
            });
        }
        else if (hiloDeRenderParado)
        {
            _raiz.Children.Clear();
            _enElGrafo.Clear();
        }
        else
        {
            // Un hilo zombi sigue recorriendo esta lista: dejarla como esta es
            // lo unico seguro. Y coherente: las capas siguen DENTRO, asi que
            // tampoco se les dice lo contrario.
            return false;
        }
        // Las capas ya no estan en el grafo: que no se crean dentro.
        foreach (var capa in _capas)
            capa.OlvidarQueEstaEnElGrafo();
        return true;
    }

    /// <summary>Aviso a disco. Las capas lo usan cuando el puente no se puede
    /// montar: sin rastro escrito, un fallo del compositor en la maquina de un
    /// cliente es indiagnosticable despues de la funcion.</summary>
    internal void Avisar(string mensaje) =>
        VideoOutputWindow.LogAviso(
            $"[compositor hwnd=0x{_hwnd:X} {_ancho}x{_alto}] {mensaje}");

    public void Dispose()
    {
        if (_dispuesta)
            return;
        // EL ESPEJO PRIMERO, igual que en LiveMonitorControl.Dispose(). Si esta
        // salida es la ANFITRIONA del espejo del PGM, su MirrorRoot lleva nodos
        // de OTRAS salidas, y las salidas se disponen en orden de indice: la
        // anfitriona no tiene por que ser la 0, asi que para cuando le llega el
        // turno puede llevar rato recorriendo, en su hilo de render, nodos y
        // fuentes de salidas YA dispuestas. FijarEspejo entra en Mutate, o sea
        // que al volver de aqui el hilo de render ya no tiene espejo que pintar.
        if (_espejo is not null || _espejoRaiz is not null)
            FijarEspejo(null, null);
        // El grafo se vacia DENTRO de Mutate y con el compositor todavia
        // vivo: es la unica mutacion que quedaba fuera del lock, y no bastaba
        // con hacerla «despues del Dispose» porque ese Dispose hace Join(2 s)
        // sin comprobar el resultado (ver VaciarGrafo). Si la salida ya venia
        // rota —sin compositor—, VaciarGrafo devuelve false y no toca nada:
        // aqui abajo la raiz queda inalcanzable igual.
        _ = VaciarGrafo();
        _dispuesta = true;

        // Mismo orden que en Redimensionar: hilo de render parado antes de
        // soltar nada de lo que estaba usando.
        _compositor?.Dispose();
        _compositor = null;
        _sink?.Dispose();
        _sink = null;

        foreach (var capa in _capas.ToList())
            capa.Dispose();
        _capas.Clear();
    }
}
