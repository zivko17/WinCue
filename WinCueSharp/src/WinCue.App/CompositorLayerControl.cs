// =============================================================================
// CompositorLayerControl.cs — Una superficie PERSISTENTE del compositor GPU
// en la ventana de salida (D3DImage sobre la superficie compartida de mpv)
// =============================================================================
// Implementa IVideoLayerSurface para un SLOT del pool de pipelines (auditoría
// 05): el binding D3DImage↔superficie se hace UNA vez (SetSurface) y vive
// toda la sesión — recrearlo por cue fugaba la VRAM del ciclo entero (interop
// + dirty-rects). Por cue solo se Assign/Release: mapear el cueId para
// opacidad/z y mostrar/ocultar el control. Lock/AddDirtyRect/Unlock en el
// hilo de UI (coalescido). El DWM compone las capas con la Opacity.
// =============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WinCue.Core;

namespace WinCue.App;

public sealed class CompositorLayerControl : Image, IVideoLayerSurface
{
    private D3DImage? _d3d;
    private nint _surface;
    private int _width, _height;
    private int _framePending;   // 0/1 — coalescer de invalidaciones
    private Action? _copyFrame;  // copia interop→presentación (DENTRO del Lock)

    /// <summary>Un cue tomó esta superficie (la ventana mapea cueId→control
    /// para opacidad y sube el z de la pista).</summary>
    public Action<int>? CueAssigned;

    /// <summary>El cue soltó la superficie (la ventana desmapea; el control
    /// y su binding SOBREVIVEN para el siguiente cue).</summary>
    public Action<int>? CueReleased;

    /// <summary>El pipeline murió de verdad (salida de la app / cambio de
    /// spec): la ventana retira el control del árbol.</summary>
    public Action? Cleared;

    public CompositorLayerControl()
    {
        Stretch = Stretch.Uniform;
        Visibility = Visibility.Collapsed;
        SnapsToDevicePixels = true;
    }

    // ------------------------------------------------------------------
    // IVideoLayerSurface (SetSurface/ClearSurface/Assign/Release: main
    // thread; InvalidateFrame: hilo de render de mpv)
    // ------------------------------------------------------------------

    /// <summary>Binding ÚNICO por vida del pipeline (no por cue).</summary>
    public void SetSurface(nint d3d9Surface, int width, int height)
    {
        _surface = d3d9Surface;
        _width = width;
        _height = height;
        _d3d = new D3DImage();
        _d3d.Lock();
        _d3d.SetBackBuffer(D3DResourceType.IDirect3DSurface9, d3d9Surface);
        _d3d.Unlock();
        Source = _d3d;
        // Oculto hasta que un cue lo tome (AssignCue)
        Visibility = Visibility.Collapsed;
    }

    public void AssignCue(int cueId)
    {
        // Cortar cualquier animación de opacidad heredada del cue anterior
        BeginAnimation(OpacityProperty, null);
        Opacity = 1.0;
        Visibility = Visibility.Visible;
        CueAssigned?.Invoke(cueId);
    }

    public void ReleaseCue(int cueId)
    {
        Visibility = Visibility.Collapsed;
        CueReleased?.Invoke(cueId);
    }

    public void SetFrameCopier(Action copyFrame) => _copyFrame = copyFrame;

    public void InvalidateFrame()
    {
        // Coalescer: si ya hay una actualización en vuelo, no encolar otra
        if (Interlocked.CompareExchange(ref _framePending, 1, 0) != 0)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            _framePending = 0;
            if (_d3d is null || _surface == 0 || !_d3d.IsFrontBufferAvailable)
                return;
            _d3d.Lock();
            // La escritura al back buffer va DENTRO del Lock (contrato
            // D3DImage) — fuera provocaba renaming del driver: una asignación
            // de VRAM POR FRAME (la fuga de la auditoría 05)
            _copyFrame?.Invoke();
            _d3d.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            _d3d.Unlock();
        });
    }

    public void ClearSurface()
    {
        _surface = 0;
        if (_d3d is not null)
        {
            _d3d.Lock();
            _d3d.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0);
            _d3d.Unlock();
            _d3d = null;
        }
        Source = null;
        Visibility = Visibility.Collapsed;
        Cleared?.Invoke();
    }
}
