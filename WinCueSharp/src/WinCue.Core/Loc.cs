// =============================================================================
// Loc.cs — Idioma de la interfaz (ES / EN), compartido por toda la solución
// =============================================================================
// Vive en Core para que TODAS las capas (App, Controller, …) puedan traducir
// sus cadenas visibles. Traducción inline: cada sitio da las dos versiones —
// Loc.T(es, en) — así no hay catálogo aparte que se desincronice. El idioma
// se lee de settings (ui_language) al arrancar y se aplica al reiniciar.
// =============================================================================

namespace WinCue.Core;

public static class Loc
{
    public enum Language { Es, En }

    /// <summary>Idioma activo de la sesión (fijado al arrancar desde settings).</summary>
    public static Language Current { get; private set; } = Language.Es;

    /// <summary>Fija el idioma desde el código de settings ("es"/"en").</summary>
    public static void SetFromCode(string? code) =>
        Current = string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)
            ? Language.En : Language.Es;

    /// <summary>Devuelve la cadena en el idioma activo.</summary>
    public static string T(string es, string en) => Current == Language.En ? en : es;

    /// <summary>true si la UI está en inglés (para lógica puntual).</summary>
    public static bool IsEnglish => Current == Language.En;
}
