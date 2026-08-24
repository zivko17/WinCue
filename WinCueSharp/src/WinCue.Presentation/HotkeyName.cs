namespace WinCue.Presentation;

/// <summary>El nombre de tecla que el operador escribe en «Disparadores →
/// Hotkey», puesto en su forma canónica.
///
/// Existe porque hay shows guardados por la versión de Python que traen
/// nombres de Qt («Esc», «Return») y siguen teniendo que disparar. Ese
/// diccionario de alias es una DECISIÓN de producto, no de framework: es la
/// misma en WinCue y en Secuence.
///
/// Lo que NO se comparte es convertir el nombre en una tecla: WPF usa
/// System.Windows.Input.Key y WinUI 3 usa Windows.System.VirtualKey, dos
/// enumeraciones distintas con nombres que no siempre coinciden. Cada
/// producto hace su propio Enum.TryParse sobre el nombre que salga de aquí.
/// </summary>
public static class HotkeyName
{
    /// <summary>Nombre canónico del alias, o el mismo nombre si no lo es.
    /// No valida: un nombre que no sea una tecla sale tal cual y el
    /// Enum.TryParse de cada producto lo rechazará.</summary>
    public static string Normalize(string name) => name switch
    {
        // Nombres de Qt heredados del settings.json de la versión Python.
        "Esc" => "Escape",
        "Return" => "Enter",
        _ => name,
    };
}
