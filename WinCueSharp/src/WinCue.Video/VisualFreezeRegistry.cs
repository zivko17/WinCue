// =============================================================================
// VisualFreezeRegistry.cs — Registro de superficies CONGELADAS del compositor
// =============================================================================
// PROBLEMA (medido, INFORME del hap-lab § "Fallo C"): si el vídeo SALIENTE de
// un crossfade termina su duración DENTRO del fundido, su superficie se
// ocultaba de golpe y la mezcla se quedaba en «opacidad_entrante × entrante»
// sobre NEGRO (luma 46 con las mesetas en 66 y 197).
//
// SOLUCIÓN: al morir el saliente durante un cruce, lo que consume (decoder,
// player, audio) termina como siempre; lo único que se DIFIERE es la
// liberación VISUAL de su superficie del pool, que ya contiene el último
// fotograma pintado. El cruce sigue mezclando contra ese fotograma congelado.
//
// ⚠ REGLA DE ORO (auditoría 05): una liberación diferida que NUNCA llegue deja
// el pool sin superficies. Este registro es el único dueño de esas
// liberaciones y las garantiza por CUATRO caminos independientes:
//   1. El plazo del propio cruce (timer del motor → ReleaseExpired/Release).
//   2. Barrido de caducados en CADA petición de slot (la demanda las reclama).
//   3. Liberación explícita en todo cierre: STOP, STOP ALL, PANIC, fin de
//      show, recarga, cambio de modo del compositor y Cleanup.
//   4. Tope de seguridad por tiempo: ningún congelado sobrevive a su plazo
//      (que ya incluye el margen del handover) por más de FreezeGraceMs.
// La clase es PURA (sin GPU ni WPF): así el «no se filtra ningún slot» se
// puede testear sin tarjeta gráfica.
// =============================================================================

namespace WinCue.Video;

/// <summary>Superficie del pool que sigue EN PANTALLA con el último fotograma
/// del cue ya terminado, hasta que el crossfade que la necesita acabe.</summary>
internal sealed class VisualFreezeRegistry
{
    /// <summary>Margen sobre el plazo del cruce tras el cual un congelado se
    /// considera CADUCADO pase lo que pase (tope de seguridad por tiempo).</summary>
    public const long FreezeGraceMs = 2_000;

    private sealed record Entry(int CueId, long DeadlineMs, Action Release);

    private readonly List<Entry> _entries = new();
    private readonly Action<string>? _trace;

    public VisualFreezeRegistry(Action<string>? trace = null) => _trace = trace;

    /// <summary>Congelados vivos (para diagnóstico y tests).</summary>
    public int Count => _entries.Count;

    public bool IsFrozen(int cueId) => _entries.Any(e => e.CueId == cueId);

    public IReadOnlyList<int> FrozenCues => _entries.Select(e => e.CueId).ToList();

    /// <summary>Difiere la liberación VISUAL de la superficie del cue hasta
    /// <paramref name="deadlineMs"/> (Environment.TickCount64). Un congelado
    /// previo del mismo cue se libera antes: nunca hay dos.</summary>
    public void Freeze(int cueId, long deadlineMs, Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        Release(cueId);
        _entries.Add(new Entry(cueId, deadlineMs, release));
        _trace?.Invoke($"freeze: cue={cueId} hasta t+{deadlineMs - Environment.TickCount64} ms " +
            $"(congelados={_entries.Count})");
    }

    /// <summary>Libera el congelado del cue (si lo hay). true si liberó.</summary>
    public bool Release(int cueId)
    {
        int i = _entries.FindIndex(e => e.CueId == cueId);
        if (i < 0)
            return false;
        var entry = _entries[i];
        _entries.RemoveAt(i);   // fuera del registro ANTES de invocar: una
                                // excepción del release no puede dejarlo dentro
        Invoke(entry, "release");
        return true;
    }

    /// <summary>Libera TODOS los congelados (STOP ALL, PANIC, recarga de show,
    /// cambio de modo del compositor, Cleanup). Devuelve cuántos liberó.</summary>
    public int ReleaseAll()
    {
        if (_entries.Count == 0)
            return 0;
        var all = _entries.ToList();
        _entries.Clear();
        foreach (var entry in all)
            Invoke(entry, "release-all");
        return all.Count;
    }

    /// <summary>Barrido del TOPE DE SEGURIDAD: libera los congelados cuyo plazo
    /// (más el margen de gracia) ya pasó. Se llama en cada petición de slot del
    /// pool, así que la demanda de superficies siempre puede reclamarlas.</summary>
    public int ReleaseExpired(long nowMs)
    {
        var due = _entries.Where(e => nowMs >= e.DeadlineMs + FreezeGraceMs).ToList();
        foreach (var entry in due)
        {
            _entries.Remove(entry);
            Invoke(entry, "expired");
        }
        return due.Count;
    }

    /// <summary>Libera los congelados que cumplan el filtro (p. ej. los de una
    /// (salida, pista) que otro vídeo acaba de tomar sin cruce).</summary>
    public int ReleaseWhere(Func<int, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var due = _entries.Where(e => match(e.CueId)).ToList();
        foreach (var entry in due)
        {
            _entries.Remove(entry);
            Invoke(entry, "release-where");
        }
        return due.Count;
    }

    private void Invoke(Entry entry, string reason)
    {
        _trace?.Invoke($"unfreeze: cue={entry.CueId} ({reason}, quedan={_entries.Count})");
        try
        {
            entry.Release();
        }
        catch (Exception exc)
        {
            // Nunca propagar: el registro ya soltó la entrada — un fallo del
            // release no puede bloquear los demás caminos de liberación.
            _trace?.Invoke($"unfreeze: cue={entry.CueId} FALLÓ: {exc.Message}");
        }
    }
}
