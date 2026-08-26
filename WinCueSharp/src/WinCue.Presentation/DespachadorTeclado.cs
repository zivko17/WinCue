namespace WinCue.Presentation;

// ==============================================================================
// DespachadorTeclado — por que existe este fichero.
//
// En WinCue (WPF) la decision de "que tecla hace que" vivia mezclada con el
// codigo que la ejecuta, en MainWindow.xaml.cs. Eso se podia verificar
// pulsando teclas delante del programa. En esta maquina eso dejo de ser
// posible: keybd_event va a la ventana con foco, y esa maquina tiene un
// escritorio remoto conectado a equipo de audio de un local real; PostMessage
// al HWND de nivel superior no llega porque WinUI 3 mete el XAML en una isla
// (DesktopWindowXamlSource); y apuntar al HWND del bridge lo bloquea el
// clasificador de seguridad de la sesion, que no se puede rodear.
//
// La solucion no es simular mejor la pulsacion: es sacar la UNICA parte de
// todo esto que es una decision pura -- que codigo de tecla, con que
// modificadores y en que estado produce que accion -- a una funcion que no
// toca ventana, ni foco, ni GPU. Una funcion pura se prueba sin que nadie
// pulse nada, y esas pruebas si corren en CI. Todo lo que SI necesita una
// ventana (leer el foco real, decidir si el TextBox del PGM cuenta como campo
// de escritura, ejecutar la accion) se queda en cada app; esta funcion solo
// decide, con lo que la app ya le entrega resuelto.
//
// LAS TRES REGLAS QUE NO SON OBVIAS (leidas del codigo de WinCue,
// MainWindow.xaml.cs:2084-2180, no supuestas):
//
//   1. Blackout (B) se mira ANTES que los hotkeys por cue. Si no, un show con
//      un cue cuya hotkey sea B hace blackout en WinCue y saca ese cue A SALA
//      en Secuence -- mismo archivo de show, comportamiento distinto.
//   2. El buffer de "ir a cue" (digitos) se mira DESPUES de los hotkeys. Un
//      cue con hotkey en un digito dispara ese cue, y el digito nunca llega a
//      alimentar el buffer.
//   3. Los hotkeys por cue solo se miran cuando NO hay ningun modificador
//      (ctrl/shift/alt) pulsado. Por eso Ctrl+Z deshace en vez de disparar un
//      cue cuya hotkey sea Z, y por lo mismo Shift+Q o Alt+Q tampoco disparan
//      un cue de hotkey Q.
//
// Y una divergencia DELIBERADA con WinCue, tambien con su prueba: aqui todo
// el transporte (Go, StopAll, StopSeleccionado, PlayheadAnterior/Siguiente,
// PausaGlobal) se mira ANTES que los hotkeys por cue, flechas y P incluidas.
// En WinCue esas dos viven por debajo de los hotkeys, asi que alli un show
// puede asignarle a un cue la flecha abajo o la P y robarle al operador la
// navegacion del playhead -- que es precisamente el gesto que esta tanda
// viene a proteger. Aqui eso no puede pasar.
//
// NOTA (no es un hueco, es lo que pide el brief): con el foco en un campo de
// escritura, WinCue hace que Escape devuelva el foco a la tabla de cues
// (MainWindow.xaml.cs:2092-2093). Aqui Escape con FocoEnCampoDeEscritura es
// Nada sin mas: mover el foco es un efecto sobre la UI, y esta funcion no
// toca UI. Queda escrito para quien consuma esto.
//
// REVISION CON PRUEBAS DE MUTACION (2026-08-26): la primera version tenia 15
// pruebas en verde y 20 mutaciones aplicadas al fichero dejaban 11 vivas.
// Se corrigio una regresion real y se tomaron tres decisiones de producto:
//
//   - C1 (regresion real): el buffer de "ir a cue" solo aceptaba digitos de
//     la fila superior y el "." de esa fila. El teclado NUMERICO
//     (NumPad0..NumPad9 y su punto decimal, que es como se teclea casi
//     siempre un numero de cue en directo) no alimentaba el buffer. Ahora
//     alimenta igual que la fila superior.
//   - PausarCue se implementa en Ctrl+P (WinCue: OnPauseCueToggle). NO lleva
//     el candado de modo SHOW: pausar UN cue durante el show es operar el
//     show, no editar su estructura -- a diferencia de Copiar/Pegar/
//     Duplicar/Agrupar.
//   - Agrupar (Ctrl+G) SI lleva el candado de modo SHOW: modifica la
//     estructura del show igual que Copiar, Pegar y Duplicar (con las que
//     comparte el candado) y que Eliminar y Renombrar (que llevan uno
//     equivalente, aparte). Quedarse fuera de esa lista era un fallo del
//     brief original, no del codigo.
//   - Eliminar (Supr) y Renombrar (F2) se movieron por DEBAJO de los hotkeys
//     por cue, alineados con WinCue. A diferencia del transporte -que si
//     diverge deliberadamente y por encima de los hotkeys, para proteger la
//     navegacion del playhead- aqui no hay ninguna razon equivalente para
//     divergir: un cue con hotkey Supr debe dispararse igual en los dos
//     productos, y divergir sin motivo es como dos productos acaban
//     comportandose distinto sin que nadie sepa por que.
//
// SEGUNDO BARRIDO DE MUTACION (2026-08-26): quedaban tres agujeros de
// cobertura sobre codigo que ya era correcto (ninguno exigio cambiar la
// logica, solo afirmarla): Ctrl+B y Shift+B no tenian prueba de que NO
// hacen blackout (el candado de "sin modificadores" ya los cubria);
// BufferBorrar no se afirmaba nunca en positivo (solo se probaba con el
// buffer vacio, que un borrado sin implementar tambien cumple); y el punto
// de la fila superior (OEM_PERIOD) no tenia prueba propia, solo el del
// numerico -- la mitad de lo que usa un operador en un portatil.
// ==============================================================================

/// <summary>Que hay que hacer con una tecla. <c>Nada</c> significa
/// «no es mia»: el llamante NO debe marcarla como tratada.</summary>
public enum AccionTecla
{
    Nada,
    Go, StopAll, StopSeleccionado, PausaGlobal, Blackout,
    PlayheadAnterior, PlayheadSiguiente, PlayheadASeleccion,
    DispararCue,
    BufferDigito, BufferBorrar, BufferSaltar,
    Abrir, Guardar, GuardarComo, Agrupar,
    Deshacer, Rehacer, Copiar, Pegar, Duplicar, PausarCue, Eliminar, Renombrar,
}

/// <summary>La decision. <c>CueId</c> solo para DispararCue; <c>Caracter</c> solo
/// para BufferDigito.</summary>
public readonly record struct DecisionTecla(
    AccionTecla Accion, int CueId = 0, char Caracter = '\0');

/// <summary>Lo que el despachador necesita saber del mundo, y que no puede
/// averiguar por si mismo sin dejar de ser puro.</summary>
public readonly record struct EstadoTeclado(
    bool DialogoAbierto,
    bool FocoEnCampoDeEscritura,
    bool FocoEnTablaDeCues,
    bool HayDigitosEnBuffer,
    bool ModoShow);

public static class DespachadorTeclado
{
    // Codigos de tecla virtual de Windows (WM_KEYDOWN / VkKeyScan). Se
    // nombran aqui para no repetir literales sueltos por el cuerpo del metodo.
    private const int VkBack = 0x08;
    private const int VkEnter = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const int VkDelete = 0x2E;
    private const int Vk0 = 0x30;
    private const int Vk9 = 0x39;
    private const int VkB = 0x42;
    private const int VkC = 0x43;
    private const int VkD = 0x44;
    private const int VkG = 0x47;
    private const int VkO = 0x4F;
    private const int VkP = 0x50;
    private const int VkS = 0x53;
    private const int VkV = 0x56;
    private const int VkY = 0x59;
    private const int VkZ = 0x5A;
    private const int VkNumPad0 = 0x60;
    private const int VkNumPad9 = 0x69;
    private const int VkDecimal = 0x6E;   // "." del teclado numerico
    private const int VkF2 = 0x71;
    private const int VkPeriodOem = 0xBE; // OEM_PERIOD, el "." de la fila superior

    /// <param name="vk">Codigo de tecla virtual de Windows. En WinUI 3 es
    /// <c>(int)e.Key</c> directamente; en WPF, <c>KeyInterop.VirtualKeyFromKey</c>.
    /// Se usa el numero y no un enum propio para que los dos productos puedan
    /// llamar sin traducir.</param>
    /// <param name="hotkeysPorCue">vk → id de cue, del show cargado.</param>
    public static DecisionTecla Resolver(
        int vk, bool ctrl, bool shift, bool alt,
        in EstadoTeclado estado,
        IReadOnlyDictionary<int, int> hotkeysPorCue)
    {
        // 1. Ningun dialogo modal deja pasar teclado al show.
        if (estado.DialogoAbierto)
            return new DecisionTecla(AccionTecla.Nada);

        // 2. Un campo de escritura se queda con todo. El despachador no sabe
        // que control tiene el foco; el llamante ya decidio si ese control
        // (p.ej. el selector del PGM) cuenta como campo de escritura o no.
        // Esta guarda y la de arriba van ANTES del bloque de Ctrl a
        // proposito: sin ese orden, un dialogo modal abierto dejaria pasar
        // Ctrl+S (guardar) y escribir el nombre de un cue dejaria pasar
        // Ctrl+D (duplicar).
        if (estado.FocoEnCampoDeEscritura)
            return new DecisionTecla(AccionTecla.Nada);

        var sinModificadores = !ctrl && !shift && !alt;

        // 3. Con Ctrl: archivo y edicion. Se exige "ctrl && !alt", no solo
        // "ctrl": en teclado espanol (y otros) AltGr se reporta como
        // Ctrl+Alt (la tecla Alt derecha), y AltGr+C debe escribir un
        // caracter, no disparar Copiar.
        if (ctrl && !alt)
        {
            var accionCtrl = vk switch
            {
                VkO => AccionTecla.Abrir,
                VkS when shift => AccionTecla.GuardarComo,
                VkS => AccionTecla.Guardar,
                VkG => AccionTecla.Agrupar,
                VkZ when shift => AccionTecla.Rehacer,
                VkZ => AccionTecla.Deshacer,
                VkY => AccionTecla.Rehacer,
                VkC => AccionTecla.Copiar,
                VkV => AccionTecla.Pegar,
                VkD => AccionTecla.Duplicar,
                VkP => AccionTecla.PausarCue,
                _ => AccionTecla.Nada,
            };

            if (accionCtrl != AccionTecla.Nada)
            {
                // En modo SHOW la edicion de la ESTRUCTURA del show esta
                // bloqueada: Agrupar la modifica igual que Copiar, Pegar y
                // Duplicar, que comparten este candado (quedarse fuera de
                // esta lista era un fallo del brief original). Eliminar y
                // Renombrar llevan el candado equivalente mas abajo, aparte,
                // porque no pasan por este switch de Ctrl. PausarCue NO
                // entra en ninguno de los dos: pausar un cue concreto
                // durante el show es operarlo, no editar su estructura.
                var esDeEdicion = accionCtrl is AccionTecla.Copiar or AccionTecla.Pegar
                    or AccionTecla.Duplicar or AccionTecla.Agrupar;
                if (esDeEdicion && estado.ModoShow)
                    return new DecisionTecla(AccionTecla.Nada);

                return new DecisionTecla(accionCtrl);
            }
        }

        // 4. Transporte. Por encima de los hotkeys siempre -- divergencia
        // deliberada con WinCue: aqui un show nunca le puede robar al
        // operador la navegacion del playhead ni la pausa global.
        if (sinModificadores)
        {
            switch (vk)
            {
                case VkSpace: return new DecisionTecla(AccionTecla.Go);
                case VkEscape: return new DecisionTecla(AccionTecla.StopAll);
                case VkS: return new DecisionTecla(AccionTecla.StopSeleccionado);
                case VkUp:
                case VkLeft: return new DecisionTecla(AccionTecla.PlayheadAnterior);
                case VkDown:
                case VkRight: return new DecisionTecla(AccionTecla.PlayheadSiguiente);
                case VkP: return new DecisionTecla(AccionTecla.PausaGlobal);
            }

            // 5. Seguridad: Blackout, ANTES que los hotkeys por cue. Sin este
            // orden, un show con un cue de hotkey B hace blackout en WinCue y
            // saca ese cue a sala en Secuence, con el mismo archivo.
            if (vk == VkB)
                return new DecisionTecla(AccionTecla.Blackout);
        }

        // 6. Hotkeys por cue. Solo sin modificadores: Ctrl+Z deshace, no
        // dispara un cue cuya hotkey sea Z; Shift+Q o Alt+Q tampoco disparan
        // un cue de hotkey Q.
        if (sinModificadores && hotkeysPorCue.TryGetValue(vk, out var cueId))
            return new DecisionTecla(AccionTecla.DispararCue, CueId: cueId);

        // 7. Eliminar (Supr) y Renombrar (F2), DEBAJO de los hotkeys por
        // cue -- alineado con WinCue. A diferencia del transporte, que si
        // diverge deliberadamente y por encima (proteger el playhead es una
        // razon; no hay ninguna equivalente aqui), un cue con hotkey Supr
        // debe dispararse igual en los dos productos.
        if (sinModificadores && (vk == VkDelete || vk == VkF2))
        {
            if (estado.ModoShow)
                return new DecisionTecla(AccionTecla.Nada);
            return new DecisionTecla(vk == VkDelete ? AccionTecla.Eliminar : AccionTecla.Renombrar);
        }

        if (sinModificadores)
        {
            // 8. Buffer de "ir a cue". Va DESPUES de los hotkeys: si el
            // digito ya disparo un cue arriba, nunca llega aqui. Acepta
            // digitos de la fila superior, del teclado NUMERICO
            // (NumPad0..NumPad9) y el punto decimal en cualquiera de sus dos
            // teclas (OEM_PERIOD de la fila superior y Decimal del
            // numerico) -- en directo, un numero de cue se teclea casi
            // siempre en el numerico.
            if ((vk >= Vk0 && vk <= Vk9) || (vk >= VkNumPad0 && vk <= VkNumPad9)
                || vk == VkPeriodOem || vk == VkDecimal)
            {
                char caracter;
                if (vk >= VkNumPad0 && vk <= VkNumPad9)
                    caracter = (char)('0' + (vk - VkNumPad0));
                else if (vk == VkPeriodOem || vk == VkDecimal)
                    caracter = '.';
                else
                    caracter = (char)('0' + (vk - Vk0));

                return new DecisionTecla(AccionTecla.BufferDigito, Caracter: caracter);
            }

            if (estado.HayDigitosEnBuffer)
            {
                if (vk == VkBack) return new DecisionTecla(AccionTecla.BufferBorrar);
                if (vk == VkEnter) return new DecisionTecla(AccionTecla.BufferSaltar);
            }

            // 9. Enter con el buffer vacio: solo lleva el playhead a la
            // seleccion si el foco esta en la tabla de cues. Fuera de la
            // tabla, Nada -- de eso depende que Tab+Enter siga activando
            // botones, ya que Espacio dejo de hacerlo (linea 4, arriba).
            if (vk == VkEnter)
            {
                return estado.FocoEnTablaDeCues
                    ? new DecisionTecla(AccionTecla.PlayheadASeleccion)
                    : new DecisionTecla(AccionTecla.Nada);
            }
        }

        return new DecisionTecla(AccionTecla.Nada);
    }
}
