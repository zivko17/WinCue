// =============================================================================
// CanvasLayout.cs — Colocación de vídeo: canvas, salidas y geometría
// (extraído de MainController, paso 5 del plan de docs/auditoria/02-arquitectura.md)
// =============================================================================
// Todo lo que responde "¿dónde y cómo se pinta el vídeo de este cue?":
// el modo canvas estilo Watchout (settings), el tamaño del canvas global,
// los recortes por salida, la geometría (canvas o superficie de la salida)
// y la factoría de VideoPlayParams — el GO y la precarga en pausa del
// standby DEBEN construir parámetros equivalentes, por eso viven juntos.
// Además, los helpers puros de "slot" de un cue de vídeo (pista, salida,
// velocidad), que usa también el núcleo.
//
// No muta nada: lee settings/vista y construye valores.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller;

public sealed class CanvasLayout
{
    private readonly SettingsManager _settings;
    private readonly IShowView _view;

    public CanvasLayout(SettingsManager settings, IShowView view)
    {
        _settings = settings;
        _view = view;
    }

    // ------------------------------------------------------------------
    // Helpers puros de slot (pista / salida / velocidad de un cue)
    // ------------------------------------------------------------------

    /// <summary>Pista de prioridad visual de un cue de vídeo: la del timeline
    /// (0 = arriba tapa a las demás); sin fijar (-1) → pista 2 por defecto,
    /// que preserva el comportamiento clásico "un vídeo sustituye al otro".</summary>
    public static int VideoLayerOf(Cue c) =>
        c.TimelineLayer < 0 ? 2 : Math.Min(c.TimelineLayer, 7);

    /// <summary>Salida de vídeo del cue (0 = Salida 1 … 3 = Salida 4).</summary>
    public static int ScreenOf(Cue c) => Math.Clamp(c.VideoOutput, 0, 3);

    /// <summary>Velocidad de reproducción efectiva (los timers de fin corren
    /// en tiempo de PARED: un media de 60 s a rate 0,5 tarda 120 s reales).</summary>
    public static double RateOf(Cue c) => Math.Clamp(c.Rate, 0.25, 4.0);

    // ------------------------------------------------------------------
    // Modo canvas (estilo Watchout)
    // ------------------------------------------------------------------

    /// <summary>true si el show opera en modo canvas (Settings → Vídeo).</summary>
    public bool CanvasMode =>
        _settings.Get("video_canvas_mode", false) &&
        _settings.Get("video_compositor", false);

    /// <summary>Tamaño del canvas global (settings surface_*; 0 → FHD).</summary>
    public (int W, int H) CanvasDims()
    {
        int w = _settings.Get("surface_width", 0);
        int h = _settings.Get("surface_height", 0);
        return (w > 0 ? w : 1920, h > 0 ? h : 1080);
    }

    /// <summary>Recortes de las salidas HABILITADAS (la 1 siempre lo está).
    /// Región por salida en settings "x,y,w,h"; ausente → canvas completo.</summary>
    public IReadOnlyList<CanvasView> CanvasViews()
    {
        var (cw, ch) = CanvasDims();
        var list = new List<CanvasView>();
        for (int s = 0; s < 4; s++)
        {
            if (s > 0 && !_settings.Get($"video_output_enabled{s + 1}", false))
                continue;
            string key = s == 0 ? "video_output_region" : $"video_output_region{s + 1}";
            list.Add(ParseRegion(_settings.Get(key, ""), s, cw, ch));
        }
        return list;
    }

    /// <summary>Parsea "x,y,w,h" y lo recorta al canvas; inválido → completo.</summary>
    public static CanvasView ParseRegion(string text, int screen, int cw, int ch)
    {
        var parts = (text ?? "").Split(',');
        if (parts.Length == 4
            && int.TryParse(parts[0].Trim(), out int x)
            && int.TryParse(parts[1].Trim(), out int y)
            && int.TryParse(parts[2].Trim(), out int w)
            && int.TryParse(parts[3].Trim(), out int h)
            && w > 0 && h > 0)
        {
            x = Math.Clamp(x, 0, Math.Max(0, cw - 16));
            y = Math.Clamp(y, 0, Math.Max(0, ch - 16));
            w = Math.Clamp(w, 16, cw - x);
            h = Math.Clamp(h, 16, ch - y);
            return new CanvasView(screen, x, y, w, h);
        }
        return new CanvasView(screen, 0, 0, cw, ch);
    }

    // ------------------------------------------------------------------
    // Geometría y parámetros de reproducción
    // ------------------------------------------------------------------

    /// <summary>Geometría del cue: en modo canvas es relativa al CANVAS global
    /// (el vídeo se coloca en el canvas y las salidas lo recortan); en
    /// clásico, a la superficie de la salida del cue.</summary>
    public VideoGeometry GeoFor(Cue cue)
    {
        double sw, sh;
        if (CanvasMode)
        {
            var (cw, ch) = CanvasDims();
            (sw, sh) = (cw, ch);
        }
        else
        {
            (sw, sh) = _view.SurfaceDimensions(ScreenOf(cue));
        }
        return new VideoGeometry(
            Mode: cue.GeoMode == GeoMode.Custom ? "CUSTOM" : "FULL",
            PreserveAspect: cue.PreserveAspect,
            TranslateX: cue.TranslateX, TranslateY: cue.TranslateY,
            ScaleX: cue.ScaleX, RotateZ: cue.RotateZ,
            SurfW: sw, SurfH: sh);
    }

    /// <summary>Parámetros de reproducción del vídeo de un cue. FACTORIZADO:
    /// los usa el GO (StartAction) y la precarga en pausa del standby — el
    /// GO instantáneo exige que ambos sean EQUIVALENTES.</summary>
    public VideoPlayParams ParamsFor(Cue cue, double offset) => new(
        Volume: cue.Volume, Loop: cue.Loop, Scale: cue.Scale,
        TrimStart: cue.TrimStart + offset, TrimEnd: cue.TrimEnd,
        Rate: cue.Rate, PlayCount: cue.PlayCount,
        PreservePitch: cue.PreservePitch,
        Geometry: GeoFor(cue),
        Layer: VideoLayerOf(cue),
        Screen: ScreenOf(cue),
        CanvasOutputs: CanvasMode ? CanvasViews() : null,
        HandoverSeconds: Math.Max(0.0, cue.FadeIn - offset) / RateOf(cue));
}
