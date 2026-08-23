// =============================================================================
// FaderScaleCanvas.cs — escala en dB de un fader vertical, en Win2D.
// =============================================================================
// Equivalente de src/WinCue.App/FaderScaleControl.cs (WPF) para WinUI 3: el
// CÁLCULO (qué valores rotula la escala y a qué coordenada Y cae cada uno) es
// EXACTAMENTE el mismo que usa WinCue — vive en
// WinCue.Presentation.FaderScaleLayout (posición→píxel) y WinCue.Core.
// AudioLevels (dB→posición, ScaleTicks), sin un solo tipo de framework de
// interfaz. Este archivo SOLO pinta — mismo reparto que VuMeterCanvas.cs (el
// piloto del traslado, ver su cabecera para la traducción DrawingContext→
// Win2D control por control) y mismo motivo de UserControl+CanvasControl en
// vez de herencia (CanvasControl está sellada).
//
// Va junto al mixer (MixerPanel.cs) porque son la MISMA pieza visual: un
// fader sin su escala, o una escala sin su fader, no dice nada por sí sola.
// =============================================================================

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Escala en dB al costado de un fader vertical, en Win2D.
/// Equivalente de <c>WinCue.App.FaderScaleControl</c> (WPF).</summary>
public sealed class FaderScaleCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();

    public FaderScaleCanvas()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        using var format = new CanvasTextFormat
        {
            FontSize = 9,
            FontFamily = "Consolas",
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Right,
        };

        foreach (double db in AudioLevels.ScaleTicks)
        {
            if (FaderScaleLayout.TickY(db, h) is not double y)
                continue;
            // La unidad se marca más clara: es la referencia que se busca —
            // mismo criterio que WPF (UnityPen frente a TickPen).
            bool unity = Math.Abs(db) < 0.001;
            var color = unity ? ToColor(Palette.TextSecondary) : ToColor(Palette.TextDimmer);
            ds.DrawLine((float)(w - 5), (float)y, (float)w, (float)y, color, 1f);

            string text = db > 0 ? $"+{db:0}" : $"{db:0}";
            using var layout = new CanvasTextLayout(ds, text, format, (float)(w - 7), 20f);
            ds.DrawTextLayout(layout, 0, (float)(y - layout.LayoutBounds.Height / 2.0), color);
        }
    }

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
}
