using System.Diagnostics;

namespace WinCue.Presentation;

// ==============================================================================
// BufferIrACue — la parte de «ir a cue» que SÍ tiene estado.
//
// DespachadorTeclado.Resolver es deliberadamente puro (ver su cabecera): no
// puede recordar qué dígitos lleva tecleados el operador ni cuándo tecleó el
// último, porque eso es memoria entre teclas, y una función pura no tiene
// memoria. Ese recuerdo es EXACTAMENTE lo único que este fichero añade.
//
// Resolver sigue siendo quien decide QUÉ hacer con cada tecla — incluida toda
// la precedencia (el buffer va después de los hotkeys, Escape no es suyo,
// etc., ver DespachadorTeclado.cs). Este fichero solo aplica esa decisión
// sobre el texto acumulado (añadir, borrar el último, vaciar al saltar) y
// decide cuándo ese texto ha caducado. No conoce cues, no conoce DisplayNumber,
// no conoce la ventana: eso es de quien lo llame (MainWindow.TratarBufferIrACue).
// ==============================================================================

/// <summary>El buffer de «ir a cue»: el texto tecleado hasta ahora y CUÁNDO se
/// tecleó el último carácter — un timestamp de <see cref="Stopwatch.GetTimestamp"/>,
/// no un reloj de pared, para que la caducidad se pueda medir con
/// <see cref="Stopwatch.GetElapsedTime(long, long)"/> sin abrir una dependencia
/// con <c>DateTime</c>.</summary>
public readonly record struct EstadoBufferIrACue(string Texto, long UltimoTimestamp)
{
    public static readonly EstadoBufferIrACue Vacio = new("", 0);

    /// <summary>true si hay texto Y han pasado más de 4 s desde el último
    /// carácter. Recibe «ahora» como parámetro — en vez de leer
    /// <see cref="Stopwatch.GetTimestamp"/> él mismo — para poder probarse sin
    /// esperar 4 s de verdad: la prueba construye dos timestamps separados por
    /// la diferencia que quiera y se los pasa a esta función.</summary>
    public bool Caducado(long ahora) =>
        Texto.Length > 0 && Stopwatch.GetElapsedTime(UltimoTimestamp, ahora).TotalSeconds > 4;
}

/// <summary>Aplica una tecla al buffer de «ir a cue», delegando en
/// <see cref="DespachadorTeclado.Resolver"/> la decisión de qué hacer y
/// quedándose solo con la parte de estado que esa función, siendo pura, no
/// puede recordar entre teclas.</summary>
public static class ProcesadorBufferIrACue
{
    /// <param name="Estado">El buffer TRAS procesar esta tecla.</param>
    /// <param name="Accion">La decisión de <see cref="DespachadorTeclado.Resolver"/>
    /// para esta tecla — puede ser cualquier <see cref="AccionTecla"/>, no solo
    /// las del buffer (p. ej. <c>StopAll</c> si la tecla era Escape): el
    /// llamante decide qué hacer con el resto.</param>
    /// <param name="CueObjetivo">Solo con <c>Accion == BufferSaltar</c>: el
    /// texto que hay que casar contra <c>DisplayNumber</c>. <c>null</c> en
    /// cualquier otro caso.</param>
    public readonly record struct Resultado(
        EstadoBufferIrACue Estado, AccionTecla Accion, string? CueObjetivo = null);

    /// <param name="ahora">El mismo <see cref="Stopwatch.GetTimestamp"/> que el
    /// llamante usará para guardar <see cref="EstadoBufferIrACue.UltimoTimestamp"/>
    /// si esta tecla acumula un dígito — un solo «ahora» para medir la
    /// caducidad Y para fechar el carácter nuevo, no dos lecturas del reloj
    /// que podrían no coincidir.</param>
    /// <param name="hotkeysPorCue">Se reenvía tal cual a Resolver. Cuando quien
    /// llama ya ha comprobado los hotkeys por cue antes de llegar aquí (como
    /// hace <c>MainWindow.OnKeyDownHotkeys</c>), lo correcto es pasar un
    /// diccionario vacío: los hotkeys ya se resolvieron y no deben volver a
    /// mirarse una segunda vez.</param>
    public static Resultado Procesar(
        EstadoBufferIrACue estadoPrevio, long ahora,
        int vk, bool ctrl, bool shift, bool alt,
        in EstadoTeclado estadoTeclado,
        IReadOnlyDictionary<int, int> hotkeysPorCue)
    {
        // La caducidad se mira ANTES de resolver la tecla: si no, teclear «2»,
        // esperar 5 s y teclear «1» acumularía «21» en vez de arrancar un
        // buffer nuevo con «1» — el caso de prueba explícito del brief.
        var estado = estadoPrevio.Caducado(ahora) ? EstadoBufferIrACue.Vacio : estadoPrevio;

        var decision = DespachadorTeclado.Resolver(
            vk, ctrl, shift, alt,
            estadoTeclado with { HayDigitosEnBuffer = estado.Texto.Length > 0 },
            hotkeysPorCue);

        return decision.Accion switch
        {
            AccionTecla.BufferDigito =>
                new Resultado(new EstadoBufferIrACue(estado.Texto + decision.Caracter, ahora), decision.Accion),

            // Retroceso NO reinicia el reloj de caducidad: borrar un carácter
            // no es «seguir tecleando un número» a efectos de los 4 s — si lo
            // fuera, un operador que se equivoca podría mantener el buffer
            // vivo indefinidamente a base de borrar y volver a escribir.
            AccionTecla.BufferBorrar =>
                new Resultado(estado with { Texto = estado.Texto[..^1] }, decision.Accion),

            AccionTecla.BufferSaltar =>
                new Resultado(EstadoBufferIrACue.Vacio, decision.Accion, estado.Texto),

            // Cualquier otra decisión (incluida Nada, incluida StopAll cuando
            // la tecla es Escape) no toca el buffer — Escape no es suyo, y NO
            // lo vacía: para eso está la caducidad, no la tecla de pánico. Pero
            // SÍ puede haber caducado arriba, y por eso se devuelve «estado» y
            // no «estadoPrevio».
            _ => new Resultado(estado, decision.Accion),
        };
    }
}
