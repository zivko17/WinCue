// =============================================================================
// FrameRing.cs — Anillo de fotogramas con PTS (capa L1)
// =============================================================================
// Cada fuente publica AQUÍ. No es una optimización: es corrección.
//
//   · Mientras el productor escribe un slot, el compositor NO puede leerlo.
//     En las fuentes mpv esto es literal: mientras GL tiene el lock del
//     interop, la textura es SUYA y D3D no puede muestrearla (verificado en
//     tools/mpv-d3d11-lab). Con una sola textura, el compositor lee lo que el
//     productor está escribiendo.
//   · Cada slot lleva su PTS, porque la sincronía es por tiempo, no por
//     "el último que llegó" (ver FrameCadence).
//
// PROTOCOLO
//   Productor: BeginWrite() → escribe en la textura del slot → Commit(pts).
//              Mientras escribe, el PTS del slot es NaN y los lectores lo
//              saltan.
//   Lector:    TryAcquire(t) → FrameLease (cuenta una referencia) → Dispose.
//              El productor nunca elige un slot con referencias vivas ni el
//              último publicado (así siempre hay algo que enseñar).
//
// ALCANCE DEL CONTEO DE REFERENCIAS: protege la ventana de GRABACIÓN del
// comando de dibujo, no su ejecución en GPU. Para eso está el tamaño del
// anillo (≥3): el productor tendría que adelantar tres fotogramas enteros
// para alcanzar a la GPU, y produce a la cadencia del contenido. Es la
// práctica habitual; si algún día hace falta exactitud, la pieza que falta es
// una fence por slot.
// =============================================================================

using Vortice.Direct3D11;

namespace WinCue.Video.Gpu;

/// <summary>Préstamo de un fotograma. Mientras viva, el productor no reutiliza
/// ese slot. SIEMPRE dentro de un using.</summary>
public readonly record struct FrameLease : IDisposable
{
    private readonly FrameRing? _ring;
    private readonly int _index;

    // Campo de validez EXPLICITO, separado de _ring. Antes IsValid miraba
    // _ring is not null, lo que declaraba invalido TODO prestamo sin anillo
    // — incluido el puente D3D9Ex→D3D11 (SharedSurfaceSource), que es un
    // prestamo perfectamente real, solo que sin slot que devolver. Sin este
    // campo, cualquier consumidor futuro que confiara en IsValid (las Tasks
    // 5 y 7 la necesitan) descartaria fotogramas validos del puente.
    private readonly bool _valid;

    internal FrameLease(FrameRing ring, int index, ID3D11ShaderResourceView srv,
        ID3D11Texture2D texture, double pts)
    {
        _ring = ring;
        _index = index;
        _valid = true;
        View = srv;
        Texture = texture;
        Pts = pts;
    }

    /// <summary>Prestamo SIN anillo: un unico recurso que vive fuera (una
    /// superficie compartida de otro device, como el puente D3D9Ex→D3D11).
    /// Dispose es no-op porque no hay slot que devolver — _ring queda null y
    /// Dispose ya es null-safe. IsValid sigue siendo true: este prestamo SI
    /// representa un fotograma real, solo que sin anillo detras.</summary>
    internal FrameLease(ID3D11ShaderResourceView srv, ID3D11Texture2D texture, double pts)
    {
        _ring = null;
        _index = -1;
        _valid = true;
        View = srv;
        Texture = texture;
        Pts = pts;
    }

    /// <summary>Vista muestreable del fotograma (lo que usa el compositor).</summary>
    public ID3D11ShaderResourceView View { get; }

    /// <summary>El recurso en sí. Lo necesita quien tenga que COPIARLO en vez
    /// de muestrearlo — la lectura de vuelta de los sinks (grabación, NDI,
    /// multiview) y la verificación por píxel de las pruebas.</summary>
    public ID3D11Texture2D Texture { get; }

    /// <summary>Marca de tiempo del fotograma, en segundos del medio.</summary>
    public double Pts { get; }

    /// <summary>true si este prestamo representa un fotograma de verdad
    /// (con anillo o sin él). false solo en el default(FrameLease) que
    /// devuelven los TryAcquire que fallan.</summary>
    public bool IsValid => _valid;

    public void Dispose() => _ring?.Release(_index);
}

public sealed class FrameRing : IDisposable
{
    private sealed class Slot
    {
        public required ID3D11Texture2D Texture;
        public required ID3D11ShaderResourceView Srv;
        public double Pts = double.NaN;   // NaN = en escritura o sin estrenar
        public int RefCount;
    }

    private readonly Slot[] _slots;
    private readonly object _writeLock = new();
    private int _writeIndex = -1;
    private int _newestIndex = -1;
    private bool _disposed;

    public int Capacity => _slots.Length;
    public int Width { get; }
    public int Height { get; }

    /// <summary>Fotogramas publicados desde la creación.</summary>
    public long Published { get; private set; }

    /// <summary>Veces que el productor no encontró slot libre (el compositor
    /// va tan retrasado que retiene todo el anillo). Si esto sube, el anillo
    /// se queda corto o el compositor está atascado.</summary>
    public long WriteStalls { get; private set; }

    public FrameRing(GpuDevice gpu, int width, int height, int capacity = 3,
        bool sharedTextures = false)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "El anillo necesita al menos 2 slots (uno se escribe, otro se lee).");

        Width = width;
        Height = height;
        _slots = new Slot[capacity];
        try
        {
            for (int i = 0; i < capacity; i++)
            {
                ID3D11Texture2D tex = gpu.CreateWorkTexture(width, height, sharedTextures);
                _slots[i] = new Slot
                {
                    Texture = tex,
                    Srv = gpu.Device.CreateShaderResourceView(tex, null),
                };
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Textura del slot — la necesitan las fuentes que registran cada
    /// slot en un interop externo (mpv/GL) una sola vez, al arrancar.</summary>
    public ID3D11Texture2D TextureAt(int index) => _slots[index].Texture;

    // ------------------------------------------------------------------
    // Productor
    // ------------------------------------------------------------------

    /// <summary>Reserva un slot para escribir. Devuelve -1 si no hay ninguno
    /// libre (todos prestados) — el productor debe saltarse ese fotograma, no
    /// esperar: nada bloquea nunca al compositor, ni al revés.</summary>
    public int BeginWrite()
    {
        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (int n = 1; n <= _slots.Length; n++)
            {
                int i = (_writeIndex + n) % _slots.Length;
                // Nunca el último publicado: si no, en el hueco entre BeginWrite
                // y Commit el compositor se quedaría sin nada que enseñar.
                if (i == _newestIndex)
                    continue;
                if (Volatile.Read(ref _slots[i].RefCount) != 0)
                    continue;

                _writeIndex = i;
                Volatile.Write(ref _slots[i].Pts, double.NaN);   // invisible al lector
                return i;
            }

            WriteStalls++;
            return -1;
        }
    }

    /// <summary>Publica el slot con su marca de tiempo. A partir de aquí es
    /// visible para el compositor.</summary>
    public void Commit(int index, double pts)
    {
        if (double.IsNaN(pts))
            throw new ArgumentException("Un fotograma publicado necesita PTS.", nameof(pts));
        lock (_writeLock)
        {
            Volatile.Write(ref _slots[index].Pts, pts);
            _newestIndex = index;
            Published++;
        }
    }

    // ------------------------------------------------------------------
    // Consumidor
    // ------------------------------------------------------------------

    /// <summary>Fotograma que corresponde al instante <paramref name="target"/>
    /// (segundos del medio): el último cuyo PTS no lo supera. false si la
    /// fuente aún no ha llegado ahí.</summary>
    public bool TryAcquire(double target, out FrameLease lease)
    {
        lease = default;
        if (_disposed)
            return false;

        Span<double> snapshot = stackalloc double[_slots.Length];

        // Hasta 3 intentos: si el productor da la vuelta justo entre la
        // elección y la reserva, se reintenta con la foto nueva.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            for (int i = 0; i < _slots.Length; i++)
                snapshot[i] = Volatile.Read(ref _slots[i].Pts);

            int pick = FrameCadence.SelectLatestNotAfter(snapshot, target);
            if (pick < 0)
                return false;

            Interlocked.Increment(ref _slots[pick].RefCount);
            if (Volatile.Read(ref _slots[pick].Pts) == snapshot[pick])
            {
                lease = new FrameLease(this, pick, _slots[pick].Srv,
                    _slots[pick].Texture, snapshot[pick]);
                return true;
            }
            Interlocked.Decrement(ref _slots[pick].RefCount);   // nos lo pisaron
        }
        return false;
    }

    internal void Release(int index) => Interlocked.Decrement(ref _slots[index].RefCount);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (Slot? s in _slots)
        {
            s?.Srv?.Dispose();
            s?.Texture?.Dispose();
        }
    }
}
