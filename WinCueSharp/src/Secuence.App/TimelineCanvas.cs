// =============================================================================
// TimelineCanvas.cs — timeline del show en Win2D, DE SOLO LECTURA.
// =============================================================================
// Primera pasada del traslado del timeline a Secuence (ver el encargo,
// hitos-0-1): la regla de tiempo, las pistas, los bloques de cue (posición,
// duración, color por tipo, etiqueta), el playhead y el indicador de
// solapamiento (aviso de fundido incoherente). NADA de arrastre, zoom con
// Ctrl+rueda, selección ni menú contextual — eso sigue siendo, a propósito,
// exclusivo de src/WinCue.App/TimelinePanel.cs (segunda pasada).
//
// El CÁLCULO (proyección de bloques, geometría en píxeles, playhead) es
// EXACTAMENTE el mismo que usa WinCue: vive en WinCue.Presentation
// (TimelineLayout, TimelinePalette, TimelinePlayhead), sin un solo tipo de
// framework de interfaz. Este archivo SOLO pinta — mismo reparto que
// VuMeterCanvas.cs (el piloto de este traslado): CÁLCULO pedido, PINTADO con
// CanvasDrawingSession.
//
// UserControl + CanvasControl (no herencia): CanvasControl sigue sellada — ver
// la cabecera de VuMeterCanvas.cs para la razón completa.
//
// SIMPLIFICACIONES DELIBERADAS DE ESTA PASADA (documentadas, no descuidos):
//   · La etiqueta del bloque aquí es "número · nombre" a secas — sin los
//     prefijos ↗ (fantasma) / ∞ (bucle) / 📌 (pista fijada a mano) que sí
//     lleva WinCue. Esos son glifos de Icons (Phosphor), y Win2D/DirectWrite
//     no resuelve por NOMBRE una fuente cargada solo para XAML (ver
//     IconFont.cs): pintarlos aquí exige cargar Phosphor como CanvasFontSet,
//     que es trabajo de una pasada futura. TimelineLayout.BlockLabelText SÍ
//     existe y YA lo usa WinCue — Secuence podrá sumarse el día que cargue
//     la fuente en Win2D sin tener que tocar el cálculo.
//   · El indicador de solapamiento se pinta como una marca triangular lisa
//     (sin el glifo ⚠ de Icons.Warning), por el mismo motivo de fuente.
//   · No se pintan los recuadros de grupo (spans) ni los marcadores GO: no
//     están en la lista de "entra" del encargo para esta pasada — el cálculo
//     YA los proyecta (TimelineProjection.Spans/GoMarks), así que sumarlos
//     aquí el día que haga falta es pintar, no volver a calcular nada.
// =============================================================================

using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using WinCue.Controller;
using WinCue.Core;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Timeline de solo lectura, en Win2D. Equivalente de
/// <c>WinCue.App.TimelinePanel</c> (WPF) para WinUI 3, limitado a lo que este
/// hito pide: regla, pistas, bloques con color y etiqueta, playhead y aviso
/// de solapamiento. UserControl que aloja un CanvasControl.</summary>
public sealed class TimelineCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();
    private readonly TimelinePlayhead _playhead = new();

    private CueModel? _model;
    private TimelineProjection _projection = new();
    private double _pxPerSec = 20;
    private double _scrollX;
    private bool _fitDone;
    private bool _renderHooked;
    private DispatcherQueueTimer? _frameTimer;

    public TimelineCanvas()
    {
        MinHeight = 90;
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) =>
        {
            _fitDone = false;
            _canvas.Invalidate();
        };
        Unloaded += (_, _) => _frameTimer?.Stop();
    }

    /// <summary>Reloj del show (el del controlador). Sin él, el playhead no
    /// extrapola entre ticks — mismo contrato que TimelinePanel.Clock.</summary>
    public IShowClock? Clock { get; set; }

    public void SetModel(CueModel model)
    {
        _model = model;
        _fitDone = false;
        RefreshFromModel();
    }

    /// <summary>Recalcula la proyección tras cualquier cambio del modelo
    /// (carga de show). Mismo cálculo compartido que WinCue —
    /// TimelineLayout.Project(), sin override de arrastre: aquí no se
    /// arrastra nada.</summary>
    public void RefreshFromModel()
    {
        _projection = _model is null ? new TimelineProjection() : TimelineLayout.Project(_model);
        _canvas.Invalidate();
    }

    /// <summary>Cues activos del tick del controlador (playhead). Mismo
    /// reparto que TimelinePanel.SetActive: repinta a 60 Hz SOLO mientras hay
    /// actividad.</summary>
    public void SetActive(IReadOnlyList<ActiveCueInfo> rows)
    {
        bool wasEmpty = !_playhead.HasActive;
        _playhead.SetActive(rows, Clock?.Now ?? 0.0);
        HookRender(_playhead.HasActive);
        if (!(wasEmpty && rows.Count == 0))
            _canvas.Invalidate();
    }

    private void HookRender(bool on)
    {
        if (on == _renderHooked)
            return;
        _renderHooked = on;
        if (!on)
        {
            _frameTimer?.Stop();
            return;
        }
        _frameTimer ??= _canvas.DispatcherQueue?.CreateTimer();
        if (_frameTimer is null)
        {
            _renderHooked = false;   // sin DispatcherQueue todavía: reintentará en el próximo tick
            return;
        }
        _frameTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0);
        _frameTimer.Tick -= OnFrameTick;
        _frameTimer.Tick += OnFrameTick;
        _frameTimer.Start();
    }

    private void OnFrameTick(DispatcherQueueTimer sender, object args) => _canvas.Invalidate();

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double w = sender.ActualWidth, h = sender.ActualHeight;
        ds.Clear(ToColor(Palette.TimelineBg));
        if (w <= 0 || h <= 0 || _model is null)
            return;

        var blocks = _projection.Blocks;

        if (!_fitDone && w > 0 && blocks.Count > 0)
        {
            _fitDone = true;
            double total = Math.Max(10, TimelineLayout.TotalSeconds(blocks) + 2);
            _pxPerSec = Math.Clamp((w - 20) / total, TimelineLayout.MinPxPerSec, TimelineLayout.MaxPxPerSec);
            _scrollX = 0;
        }

        int layers = TimelineLayout.VisibleLayers(blocks);
        double lh = TimelineLayout.LayerHeight(h, layers);

        // Fondo de pistas alternado
        for (int l = 0; l < layers; l++)
        {
            double y = TimelineLayout.RulerH + l * lh;
            var brush = l % 2 == 0 ? ToColor(Palette.TimelineLayerA) : ToColor(Palette.TimelineLayerB);
            ds.FillRectangle(new Rect(0, y, w, lh), brush);
        }

        // Regla de tiempo
        ds.FillRectangle(new Rect(0, 0, w, TimelineLayout.RulerH), ToColor(Palette.TimelineRulerBg));
        using var tickFormat = new CanvasTextFormat { FontSize = 10 };
        foreach (double t in TimelineLayout.RulerTicks(_pxPerSec, _scrollX, w))
        {
            float x = (float)TimelineLayout.SecToX(t, _pxPerSec, _scrollX);
            ds.DrawLine(new Vector2(x, (float)(TimelineLayout.RulerH - 6)), new Vector2(x, (float)TimelineLayout.RulerH),
                ToColor(Palette.TimelineTick));
            ds.DrawLine(new Vector2(x, (float)TimelineLayout.RulerH), new Vector2(x, (float)h),
                ToColor(Palette.TimelineTickMinor));
            ds.DrawText(TimelineLayout.FormatTime(t), x + 3, 2, ToColor(Palette.TextDim), tickFormat);
        }

        if (blocks.Count == 0)
            return;

        foreach (var b in blocks)
            DrawBlock(ds, b, lh, w);

        // Playhead: cálculo compartido (WinCue.Presentation.TimelinePlayhead)
        double? cursor = _playhead.CursorSeconds(blocks, Clock?.Now ?? 0.0);
        if (cursor is double cs)
        {
            float x = (float)TimelineLayout.SecToX(cs, _pxPerSec, _scrollX);
            if (x >= 0 && x <= w)
                ds.DrawLine(new Vector2(x, 0), new Vector2(x, (float)h), ToColor(Palette.Pgm), 2f);
        }
    }

    private void DrawBlock(CanvasDrawingSession ds, TimelineBlock b, double lh, double w)
    {
        var geom = TimelineLayout.BlockGeometry(b, _pxPerSec, _scrollX, lh);
        var r = geom.Rect;
        if (r.X + r.Width < 0 || r.X > w)
            return;
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);

        uint colorToken = TimelinePalette.ResolveCueColor(b.Cue);
        var top = ToColor(colorToken);
        var bottom = ToColor(TimelinePalette.Darken(colorToken, TimelinePalette.BlockGradientDarken));

        using var layer = ds.CreateLayer(b.Ghost ? 0.45f : 1f);

        using (var fill = MakeVerticalGradient(ds, top, bottom, rect))
        using (var geometry = CanvasGeometry.CreateRoundedRectangle(ds, rect, 3, 3))
        {
            ds.FillGeometry(geometry, fill);

            if (b.Dimmed)
                ds.FillGeometry(geometry, ToColor(Palette.Black, 90));
        }

        // Indicador de solapamiento (aviso de fundido incoherente): sale del
        // propio cálculo (TimelineBlock.Issues, vía FadeSanity). Marca
        // triangular lisa — ver la cabecera del archivo para el porqué de no
        // usar aquí el glifo ⚠ de Icons.Warning.
        if (b.Issues != FadeIssues.None && rect.Width > 14)
        {
            const double size = 8;
            bool atStart = b.Issues.HasFlag(FadeIssues.CrossfadeTooLong)
                || b.Issues.HasFlag(FadeIssues.FadeInTooLong);
            double wx = atStart ? rect.X + 2 : rect.X + rect.Width - size - 2;
            using var warn = WarningTriangle(ds, wx, rect.Y + 2, size);
            ds.FillGeometry(warn, ToColor(Palette.Warn));
        }

        if (rect.Width > 30)
        {
            string text = $"{b.Cue.DisplayNumber} · {b.Cue.Name}";
            using var format = new CanvasTextFormat
            {
                FontSize = 11,
                WordWrapping = CanvasWordWrapping.NoWrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis,
            };
            using var layout = new CanvasTextLayout(ds, text, format,
                (float)Math.Max(1, rect.Width - 10), (float)rect.Height);
            double ty = rect.Y + (rect.Height - layout.LayoutBounds.Height) / 2;
            ds.DrawTextLayout(layout, (float)(rect.X + 5), (float)ty, ToColor(Palette.TextBright));
        }
    }

    private static CanvasLinearGradientBrush MakeVerticalGradient(
        ICanvasResourceCreator creator, Color top, Color bottom, Rect rect)
    {
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0f, Color = top },
            new CanvasGradientStop { Position = 1f, Color = bottom },
        };
        return new CanvasLinearGradientBrush(creator, stops)
        {
            StartPoint = new Vector2((float)rect.X, (float)rect.Y),
            EndPoint = new Vector2((float)rect.X, (float)(rect.Y + rect.Height)),
        };
    }

    /// <summary>Marca triangular lisa apuntando hacia arriba, en un cuadrado
    /// de lado <paramref name="size"/> con esquina superior izquierda en
    /// (x, y) — el mismo hueco donde WinCue pinta el glifo ⚠.</summary>
    private static CanvasGeometry WarningTriangle(ICanvasResourceCreator creator, double x, double y, double size)
    {
        using var pathBuilder = new CanvasPathBuilder(creator);
        pathBuilder.BeginFigure((float)x, (float)(y + size));
        pathBuilder.AddLine((float)(x + size), (float)(y + size));
        pathBuilder.AddLine((float)(x + size / 2), (float)y);
        pathBuilder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(pathBuilder);
    }

    private static Color ToColor(uint rgb, byte alpha = 0xff) => Color.FromArgb(
        alpha, (byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
}
