// =============================================================================
// RtPool.cs — Render targets intermedias del grafo (capa L3)
// =============================================================================
// Un nodo `mix` necesita un sitio donde dejar su resultado para que el nodo de
// arriba lo muestree. Crear esa textura por fotograma sería absurdo (y la
// auditoría 05 ya enseñó lo que cuesta crear y destruir recursos en caliente),
// así que salen de aquí y vuelven al acabar el frame.
//
// Todas del tamaño del lienzo: un grafo de composición trabaja siempre a
// resolución de canvas y solo el sink final escala. Eso ahorra tener un pool
// por tamaño y hace el ping-pong trivial.
// =============================================================================

using Vortice.Direct3D11;

namespace WinCue.Video.Gpu;

/// <summary>Render target prestada del pool. Vuelve sola al liberarse.</summary>
public readonly record struct RtLease : IDisposable
{
    private readonly RtPool? _pool;
    private readonly int _index;

    internal RtLease(RtPool pool, int index, ID3D11RenderTargetView rtv,
        ID3D11ShaderResourceView srv, ID3D11Texture2D texture)
    {
        _pool = pool;
        _index = index;
        Rtv = rtv;
        Srv = srv;
        Texture = texture;
    }

    public ID3D11RenderTargetView Rtv { get; }
    public ID3D11ShaderResourceView Srv { get; }
    public ID3D11Texture2D Texture { get; }
    public bool IsValid => _pool is not null;

    public void Dispose() => _pool?.Return(_index);
}

internal sealed class RtPool : IDisposable
{
    private sealed class Entry
    {
        public required ID3D11Texture2D Texture;
        public required ID3D11RenderTargetView Rtv;
        public required ID3D11ShaderResourceView Srv;
        public bool InUse;
    }

    private readonly GpuDevice _gpu;
    private readonly List<Entry> _entries = new();
    private readonly object _sync = new();
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Máximo de RTs vivas a la vez que ha necesitado el grafo. Es la
    /// medida de su profundidad real; si crece sin parar, hay leases sin
    /// devolver.</summary>
    public int HighWaterMark { get; private set; }

    public RtPool(GpuDevice gpu, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
        Width = width;
        Height = height;
    }

    /// <summary>Presta una RT del tamaño del lienzo, creándola solo si no hay
    /// ninguna libre (en régimen permanente no se crea ninguna).</summary>
    public RtLease Rent()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].InUse)
                    continue;
                _entries[i].InUse = true;
                return Lease(i);
            }

            ID3D11Texture2D tex = _gpu.CreateWorkTexture(Width, Height);
            _entries.Add(new Entry
            {
                Texture = tex,
                Rtv = _gpu.Device.CreateRenderTargetView(tex, null),
                Srv = _gpu.Device.CreateShaderResourceView(tex, null),
                InUse = true,
            });

            int inUse = _entries.Count(e => e.InUse);
            if (inUse > HighWaterMark)
                HighWaterMark = inUse;

            return Lease(_entries.Count - 1);
        }
    }

    private RtLease Lease(int i) =>
        new(this, i, _entries[i].Rtv, _entries[i].Srv, _entries[i].Texture);

    internal void Return(int index)
    {
        lock (_sync)
        {
            if (!_disposed && index >= 0 && index < _entries.Count)
                _entries[index].InUse = false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (Entry e in _entries)
            {
                e.Srv.Dispose();
                e.Rtv.Dispose();
                e.Texture.Dispose();
            }
            _entries.Clear();
        }
    }
}
