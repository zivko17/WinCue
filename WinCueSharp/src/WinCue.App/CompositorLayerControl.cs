// =============================================================================
// CompositorLayerControl.cs — Una pista del compositor GPU en la ventana de
// salida (D3DImage sobre la superficie compartida donde dibuja mpv)
// =============================================================================
// Implementa IVideoLayerSurface: el motor le entrega el puntero
// IDirect3DSurface9 compartido y avisa en cada frame; aquí solo se hace
// Lock/AddDirtyRect/Unlock en el hilo de UI (coalescido: nunca se encola más
// de una actualización). El DWM compone las capas con la Opacity del control.
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

    /// <summary>El motor soltó la superficie (fin del cue): la ventana lo usa
    /// para retirar el control del árbol (superficies POR CUE).</summary>
    public Action? Cleared;

    public CompositorLayerControl()
    {
        Stretch = Stretch.Uniform;
        Visibility = Visibility.Collapsed;
        SnapsToDevicePixels = true;
    }

    // ------------------------------------------------------------------
    // IVideoLayerSurface (SetSurface/ClearSurface: main thread;
    // InvalidateFrame: hilo de render de mpv)
    // ------------------------------------------------------------------

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
        Visibility = Visibility.Visible;
    }

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
