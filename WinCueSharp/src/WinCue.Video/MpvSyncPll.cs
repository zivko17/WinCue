// =============================================================================
// MpvSyncPll.cs — PLL de disciplina del vídeo mpv al reloj del audio espejo
// =============================================================================
//
// RUTA HÍBRIDA (paso 3b): los cues de vídeo no-HAP reproducen su AUDIO por el
// AudioEngine (rack nativo) y mpv corre MUDO (aid=no). Sin pista de audio,
// mpv sincroniza contra su reloj de sistema → deriva respecto al audio real.
// Este PLL corrige esa deriva. Es LÓGICA PURA (sin timers ni mpv): recibe la
// posición del vídeo y el elapsed del audio en cada tick (~400 ms) y decide
// {speed, hard-seek o nada}. El VideoEngine aplica la decisión.
//
// SEMÁNTICA ELEGIDA (el AUDIO ES EL PROGRAMA — mpv es el esclavo; contrario
// al reloj HAP, donde el vídeo ignora al audio atrasado):
//   · audio null (espejo aún decodificando, sin pista, o terminado) →
//     speed 1.0, sin seeks: mpv free-run. NUNCA rebobinar por falta de reloj.
//   · |d| < 15 ms (d = vídeo − audio) → zona muerta: speed 1.0, no tocar.
//   · 15 ms ≤ |d| ≤ 0,25 s → corrección proporcional:
//     speed = clamp(1 − d·k, 0.98, 1.02) con k = 0.3. Sin audio en mpv, un
//     ±2 % de velocidad es invisible. "speed" solo se escribe si cambia
//     ≥ 0.001 (no spamear propiedades).
//   · vídeo ADELANTADO (d > 0,25 s; el caso típico: el espejo decodifica el
//     archivo ENTERO a RAM y arranca TARDE — lección cara del paso 3):
//       - d < 2 s → FRENADA suave: speed = clamp(1 − d·k, 0.90, 1.0). Sin
//         seek atrás visible; converge en pocos segundos.
//       - d ≥ 2 s → UN hard-seek atrás a la posición del audio (visible una
//         vez, pero deja el lipsync perfecto: el audio es el programa).
//   · vídeo ATRASADO (d < −0,25 s) sostenido ≥ 2 ticks → hard-seek adelante
//     (a 1.02× tardaría >12 s en recuperar 0,25 s; el salto adelante apenas
//     se ve). El sostenimiento filtra lecturas espurias de time-pos.
//   · GUARDA DE FRONTERA DE LOOP (solo cues en loop): |d| > min(1 s,
//     duración/2) huele a cruce de pasada (vídeo o audio acaban de dar la
//     vuelta y el otro aún no) → esperar 3 ticks antes de actuar; si al
//     tercero persiste, no era un cruce: corregir de verdad.
//   · Tras un hard-seek, 2 ticks de reposo (cooldown): mpv tarda en asentar
//     el seek y time-pos puede llegar rancia — corregir ahí oscilaría.
// =============================================================================

namespace WinCue.Video;

public sealed class MpvSyncPll
{
    /// <summary>Decisión de un tick. WriteSpeed = true → escribir la propiedad
    /// "speed" (cambió ≥ 0.001 respecto a la última escrita). HardSeekTo ≠
    /// null → fijar time-pos a esa posición DE CONTENIDO (el llamante suma su
    /// trim_start).</summary>
    public readonly record struct Decision(double Speed, bool WriteSpeed, double? HardSeekTo);

    // Umbrales de la tabla de decisiones (documentada arriba)
    public const double DeadZoneSeconds = 0.015;
    public const double SeekThresholdSeconds = 0.25;
    public const double BrakeMaxSeconds = 2.0;
    public const double Gain = 0.3;
    public const double SpeedMin = 0.98;
    public const double SpeedMax = 1.02;
    public const double BrakeFloor = 0.90;
    public const double MinSpeedDelta = 0.001;
    private const int SustainTicks = 2;      // drift grande: ticks antes de actuar
    private const int LoopGuardTicks = 3;    // frontera de loop: ticks extra
    private const int SeekCooldownTicks = 2; // reposo tras un hard-seek

    private double _lastWrittenSpeed = 1.0;  // mpv nace a speed 1.0
    private int _bigDriftTicks;
    private int _cooldown;

    /// <summary>Un tick del PLL (~400 ms). videoPos/audioElapsed en segundos
    /// DE CONTENIDO (ya sin trim_start); null = desconocido/no suena.
    /// durationSeconds = duración efectiva del cue (0 = desconocida);
    /// loop = el cue repite (activa la guarda de frontera de pasada).</summary>
    public Decision Tick(double? videoPos, double? audioElapsed,
        double durationSeconds, bool loop = false)
    {
        // Reposo tras un seek: dejar que mpv asiente antes de volver a medir
        if (_cooldown > 0)
        {
            _cooldown--;
            return SpeedDecision(1.0);
        }

        // Sin reloj de audio (o sin time-pos aún): free-run a velocidad 1
        if (videoPos is not double v || audioElapsed is not double a)
        {
            _bigDriftTicks = 0;
            return SpeedDecision(1.0);
        }

        double d = v - a;              // > 0 = vídeo adelantado al audio
        double ad = Math.Abs(d);

        if (ad < DeadZoneSeconds)
        {
            _bigDriftTicks = 0;
            return SpeedDecision(1.0);
        }

        if (ad <= SeekThresholdSeconds)
        {
            _bigDriftTicks = 0;
            return SpeedDecision(Math.Clamp(1.0 - d * Gain, SpeedMin, SpeedMax));
        }

        // ── Drift grande: frenada, o hard-seek sostenido ─────────────────
        bool loopSuspect = loop && durationSeconds > 0.0
            && ad > Math.Min(1.0, durationSeconds / 2.0);
        bool brake = d > 0.0 && d < BrakeMaxSeconds;
        // La frenada no es un seek visible → actúa al primer tick; el seek
        // espera 2 ticks (3 si huele a cruce de pasada de loop).
        int need = loopSuspect ? LoopGuardTicks : (brake ? 1 : SustainTicks);
        _bigDriftTicks++;
        if (_bigDriftTicks < need)
            return SpeedDecision(1.0);

        if (brake)
            return SpeedDecision(Math.Clamp(1.0 - d * Gain, BrakeFloor, 1.0));

        // Hard-seek a la posición del audio (atrás si d ≥ 2 s, adelante si
        // el vídeo va atrasado) y velocidad 1: el lipsync se restaura de golpe.
        _bigDriftTicks = 0;
        _cooldown = SeekCooldownTicks;
        var speed = SpeedDecision(1.0);
        return speed with { HardSeekTo = a };
    }

    private Decision SpeedDecision(double speed)
    {
        bool write = Math.Abs(speed - _lastWrittenSpeed) >= MinSpeedDelta;
        if (write)
            _lastWrittenSpeed = speed;
        return new Decision(speed, write, null);
    }
}
