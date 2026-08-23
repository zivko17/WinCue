// =============================================================================
// GrBarCanvas.cs — medidor de reducción de ganancia del limitador, en Win2D.
// =============================================================================
// Equivalente de la clase privada GrBar de src/WinCue.App/AudioMixerPanel.cs
// (WPF): barra naranja que crece de ARRIBA hacia abajo (como en una mesa),
// escala 0-12 dB. Solo la lleva el strip MASTER. No hay cálculo que extraer a
// WinCue.Presentation aparte del propio clamp — es tan poco (una fracción de
// un rango fijo) que separarlo en su propio archivo de cálculo con pruebas
// habría sido papeleo sin beneficio; el original tampoco lo separaba.
// =============================================================================

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Medidor de reducción de ganancia del limitador de bus, en Win2D.</summary>
public sealed class GrBarCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();
    private double _db;

    public GrBarCanvas()
    {
        Width = 7;
        Content = _canvas;
        _canvas.Draw += OnDraw;
    }

    public void SetDb(float db)
    {
        double clamped = Math.Clamp(db, 0.0, 12.0);
        if (Math.Abs(clamped - _db) < 0.05)
            return;
        _db = clamped;
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var frame = new Rect(0.5, 0.5, w - 1, h - 1);
        ds.FillRectangle(frame, ToColor(Palette.MeterBg));
        ds.DrawRectangle(frame, ToColor(Palette.BorderMeter), 1f);
        if (_db > 0.05)
            ds.FillRectangle(new Rect(1, 1, w - 2, (h - 2) * (_db / 12.0)), ToColor(Palette.Warn));
    }

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
}
