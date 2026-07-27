// =============================================================================
// Ramp.cs — Fundidos en el reloj del compositor (capa L2/L3)
// =============================================================================
// Hoy un fundido es BeginAnimation(OpacityProperty) sobre un elemento WPF: lo
// gobierna el reloj de animación de WPF, que no tiene nada que ver con el
// instante en que se presenta el fotograma. De ahí que un cruce pueda verse a
// tirones aunque la GPU vaya sobrada.
//
// Aquí un fundido es una FUNCIÓN DEL TIEMPO evaluada en cada fotograma con el
// mismo instante que usan las fuentes para elegir su imagen. Todo el frame
// —qué fotograma de cada capa y con cuánta opacidad— corresponde a UN instante.
//
// Es una struct pura: se prueba sin GPU y sin hilos.
//
// CURVA LINEAL a propósito. En vídeo de teatro el cruce lineal es lo que
// espera el técnico y lo que hace la ruta actual; cambiar a una curva en S
// sería una decisión artística, no técnica, y no toca tomarla al portar.
// =============================================================================

namespace WinCue.Video.Gpu;

public readonly record struct Ramp(double StartTime, double Duration, float From, float To)
{
    /// <summary>Valor de la rampa en el instante <paramref name="t"/>. Antes
    /// de empezar vale From; después de acabar, To. Duración cero = salto
    /// inmediato (un corte, no un fundido).</summary>
    public float At(double t)
    {
        if (Duration <= 0.0)
            return t < StartTime ? From : To;
        double p = (t - StartTime) / Duration;
        if (p <= 0.0)
            return From;
        if (p >= 1.0)
            return To;
        return (float)(From + ((To - From) * p));
    }

    /// <summary>true si en el instante dado la rampa ya terminó — el motor de
    /// cues lo usa para saber cuándo puede retirar el saliente del grafo.</summary>
    public bool IsDone(double t) => t >= StartTime + Duration;

    /// <summary>Fundido de entrada estándar.</summary>
    public static Ramp FadeIn(double startTime, double seconds) =>
        new(startTime, seconds, 0f, 1f);

    /// <summary>Fundido de salida estándar.</summary>
    public static Ramp FadeOut(double startTime, double seconds) =>
        new(startTime, seconds, 1f, 0f);
}
