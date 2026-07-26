// =============================================================================
// MuteSoloBoard.cs — El SILENCIO y el SOLO de la mesa, sin interfaz
// =============================================================================
//
// Vivía metido en AudioMixerPanel, entre Sliders y Brushes, y por eso no había
// forma de probarlo: para comprobar que soltar un solo devuelve los strips
// correctos hacía falta levantar WPF entero. Aquí dentro no hay ni un control:
// entran pulsaciones, salen ganancias.
//
// SON DOS SILENCIOS INDEPENDIENTES, y esa es toda la clave:
//
//   · EL DE LA MANO   — el botón M de ese strip. Es del operador y no lo toca
//                       nadie más.
//   · EL DEL SOLO     — impuesto porque OTRO strip está en solo. Lo pone y lo
//                       quita el reparto del solo, y solo él.
//
// Lo que suena es la ganancia del fader si NO hay ninguno de los dos; 0 si hay
// cualquiera. Tenerlos en un único flag —que es como estaba— hacía que pulsar M
// en un strip enmudecido por el solo ajeno lo volviera AUDIBLE y reventara el
// solo. Con dos flags, pulsar M ahí pone su propio silencio y sigue callado,
// que es lo que hace una mesa.
//
// EL RESTO DE REGLAS:
//   · Quitar un silencio devuelve EXACTAMENTE la ganancia de la que se venía —
//     no la unidad, no un valor por defecto: donde estaba.
//   · Mover el fader levanta el silencio DE LA MANO (mandar tiene que mandar),
//     pero NO el del solo ajeno: si no, subir un fader cualquiera desharía el
//     solo de otro sin querer.
//   · Un strip que aparece con un solo puesto NACE MUDO, o el siguiente GO se
//     colaría por encima y el solo duraría hasta el próximo disparo en vez de
//     hasta que se suelte.
//   · El que se va se lleva su solo: si termina el último cue en solo, los
//     demás vuelven solos — o la mesa se queda muda sin que nadie toque nada.
//
// El MASTER y la AUDICIÓN se dan de alta con canSolo:false: se pueden
// silenciar, pero no hay nada contra lo que solearlos.
// =============================================================================

namespace WinCue.Controller;

/// <summary>Estado de silencio y solo de los strips de la mesa. Sin WPF: las
/// claves son ids de cue (o las reservadas de MASTER/AUDICIÓN) y lo único que
/// sale es <see cref="GainChanged"/>.</summary>
public sealed class MuteSoloBoard
{
    private sealed class StripState
    {
        /// <summary>La ganancia que pide el FADER (lo que quiere el operador),
        /// independiente de que ahora mismo se esté oyendo o no.</summary>
        public int Level = 100;
        public bool MutedByHand;
        public bool MutedBySolo;
        public bool Soloed;
        public bool CanSolo;
        public int LastEmitted = -1;

        public bool Muted => MutedByHand || MutedBySolo;

        /// <summary>Lo que de verdad debe sonar.</summary>
        public int Effective => Muted ? 0 : Level;
    }

    private readonly Dictionary<int, StripState> _strips = new();

    /// <summary>(clave, ganancia por ciento) cuando cambia lo que debe sonar.
    /// NO salta al mover el fader: eso ya lo sabe quien lo movió.</summary>
    public Action<int, int>? GainChanged;

    /// <summary>Da de alta un strip. Si hay un solo puesto y este no lo tiene,
    /// nace MUDO.</summary>
    public void Add(int key, int gain = 100, bool canSolo = true)
    {
        _strips[key] = new StripState
        { Level = gain, CanSolo = canSolo, LastEmitted = gain };
        ApplySolo();
    }

    /// <summary>Da de baja un strip (su cue dejó de sonar). Se lleva su solo.</summary>
    public void Remove(int key)
    {
        if (_strips.Remove(key))
            ApplySolo();
    }

    public bool Knows(int key) => _strips.ContainsKey(key);
    public bool IsMuted(int key) => _strips.TryGetValue(key, out var s) && s.Muted;
    public bool IsSoloed(int key) => _strips.TryGetValue(key, out var s) && s.Soloed;
    public bool CanSolo(int key) => _strips.TryGetValue(key, out var s) && s.CanSolo;

    /// <summary>Silenciado POR EL OPERADOR (botón M de ese strip).</summary>
    public bool IsMutedByHand(int key) =>
        _strips.TryGetValue(key, out var s) && s.MutedByHand;

    /// <summary>Silenciado porque OTRO strip está en solo.</summary>
    public bool IsMutedBySolo(int key) =>
        _strips.TryGetValue(key, out var s) && s.MutedBySolo;

    /// <summary>Ganancia por ciento que debe sonar (0 si está en silencio).</summary>
    public int GainOf(int key) => _strips.TryGetValue(key, out var s) ? s.Effective : 0;

    /// <summary>Ganancia que pide el fader, se esté oyendo o no.</summary>
    public int LevelOf(int key) => _strips.TryGetValue(key, out var s) ? s.Level : 0;

    /// <summary>Hay al menos un strip en solo.</summary>
    public bool AnySolo => _strips.Values.Any(s => s.CanSolo && s.Soloed);

    /// <summary>Botón M: pone o quita el silencio DE LA MANO. No toca el del
    /// solo, así que en un strip enmudecido por el solo ajeno sigue callado.</summary>
    public void ToggleMute(int key)
    {
        if (!_strips.TryGetValue(key, out var s))
            return;
        s.MutedByHand = !s.MutedByHand;
        Emit(key, s);
    }

    /// <summary>Botón S. En un strip sin solo (MASTER/AUDICIÓN) no hace nada.</summary>
    public void SetSolo(int key, bool on)
    {
        if (!_strips.TryGetValue(key, out var s) || !s.CanSolo || s.Soloed == on)
            return;
        s.Soloed = on;
        ApplySolo();
    }

    /// <summary>El operador movió el fader: esa pasa a ser su ganancia y se
    /// levanta el silencio DE LA MANO. Devuelve lo que debe sonar de verdad,
    /// que con un solo ajeno puesto sigue siendo 0 — quien lo llama tiene que
    /// mandar al motor ESTO y no el valor del control.</summary>
    public int FaderMoved(int key, int gain)
    {
        if (!_strips.TryGetValue(key, out var s))
            return gain;
        s.Level = gain;
        s.MutedByHand = false;
        s.LastEmitted = s.Effective;
        return s.Effective;
    }

    /// <summary>Coloca la ganancia SIN que cuente como maniobra del operador
    /// (cargar ajustes, refrescar desde el modelo). No levanta ningún silencio.</summary>
    public void SetGainSilently(int key, int gain)
    {
        if (!_strips.TryGetValue(key, out var s))
            return;
        s.Level = gain;
        s.LastEmitted = s.Effective;
    }

    // ------------------------------------------------------------------

    private void Emit(int key, StripState s)
    {
        if (s.Effective == s.LastEmitted)
            return;
        s.LastEmitted = s.Effective;
        GainChanged?.Invoke(key, s.Effective);
    }

    /// <summary>Reparte el silencio del solo. Toca ÚNICAMENTE el flag del solo:
    /// el de la mano es del operador y no se pisa.</summary>
    private void ApplySolo()
    {
        bool any = AnySolo;
        // Copia: los callbacks de GainChanged pueden dar de alta o de baja
        // strips (un cue que arranca o termina) y reventarían el recorrido.
        foreach (var (key, s) in _strips.ToList())
        {
            if (!s.CanSolo)
                continue;
            bool shouldMute = any && !s.Soloed;
            if (s.MutedBySolo == shouldMute)
                continue;
            s.MutedBySolo = shouldMute;
            Emit(key, s);
        }
    }
}
