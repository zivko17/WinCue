namespace WinCue.Presentation;

/// <summary>El historial de la barra de estado.
///
/// Los avisos pasan FUGACES por la barra —cuatro segundos y a otra cosa— así
/// que sin historial, un mensaje que salta mientras el operador mira otro
/// sitio se pierde para siempre. En un directo eso no vale.
///
/// Vive aquí porque las tres decisiones que lo forman son de producto y no de
/// framework: cuántos se guardan, cómo se sellan con la hora, y —la que más
/// importa— QUÉ CUENTA COMO AVISO, que es lo que enciende el botón. Si los dos
/// productos no coinciden en eso, el mismo mensaje enciende el aviso en uno y
/// no en el otro.</summary>
public sealed class StatusLog
{
    /// <summary>Tope del historial. Al pasarse se tira el más antiguo: es un
    /// registro de sesión para mirar hacia atrás un rato, no un log de
    /// auditoría.</summary>
    public const int MaxEntries = 300;

    private readonly List<string> _entries = new();

    /// <summary>Del más antiguo al más reciente.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Apunta un mensaje sellado con la hora. La hora se pasa desde
    /// fuera para que esto se pueda probar.</summary>
    public void Add(string message, DateTime now)
    {
        _entries.Add($"[{now:HH:mm:ss}] {message}");
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    /// <summary>El historial para enseñarlo: el más RECIENTE primero, que es
    /// el que se busca al abrirlo.</summary>
    public string Render(string whenEmpty) =>
        _entries.Count > 0
            ? string.Join(Environment.NewLine, Enumerable.Reverse(_entries))
            : whenEmpty;

    /// <summary>¿Este mensaje merece encender el botón del historial? Lo que
    /// pasa por la barra es casi todo rutina («Show cargado», «Cue añadido»);
    /// esto distingue lo que el operador querría haber visto aunque estuviera
    /// mirando a otro lado.
    ///
    /// Se comprueba el glifo de aviso además de las palabras porque hay
    /// mensajes que solo lo llevan a él.</summary>
    public static bool IsWarning(string message) =>
        message.Contains("[AVISO]")
        || message.Contains(Icons.Warning)
        || message.Contains("Error", StringComparison.OrdinalIgnoreCase)
        || message.Contains("falló", StringComparison.OrdinalIgnoreCase);
}
