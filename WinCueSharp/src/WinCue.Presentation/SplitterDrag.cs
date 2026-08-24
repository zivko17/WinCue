namespace WinCue.Presentation;

/// <summary>Hasta dónde puede crecer o encoger un panel cuando el operador
/// arrastra su separador.
///
/// WPF trae <c>GridSplitter</c> y WinCue lo usa tal cual; WinUI 3 no lo trae,
/// así que Secuence tiene que arrastrar a mano. Lo que NO puede ser a mano es
/// decidir dónde para: un separador que deja la lista de cues sin sitio, o el
/// panel de pestañas por debajo de lo que hace falta para leerlo, es un
/// separador roto. Esa cuenta vive aquí, con pruebas.
///
/// El orden de los topes importa y es el de <c>GridSplitter</c>: el mínimo
/// PROPIO manda sobre el hueco que quede. En una ventana tan baja que no
/// caben los dos mínimos, el panel se queda en el suyo y el vecino se lleva
/// el recorte; lo otro sería devolver un tamaño que ni siquiera es legible.
/// </summary>
public static class SplitterDrag
{
    /// <summary>Tamaño nuevo del panel que se está arrastrando.</summary>
    /// <param name="current">Su tamaño cuando empezó el arrastre.</param>
    /// <param name="delta">Cuánto ha corrido el puntero, YA orientado: en
    /// positivo el panel crece. Orientarlo es del control, que es quien sabe
    /// hacia qué lado crece el suyo.</param>
    /// <param name="min">Mínimo del panel.</param>
    /// <param name="max">Máximo del panel (<c>PositiveInfinity</c> si no
    /// tiene).</param>
    /// <param name="available">Hueco que se reparten los DOS paneles y el
    /// separador, todo junto.</param>
    /// <param name="neighbourMin">Mínimo del panel de enfrente.</param>
    /// <param name="splitter">Grueso del separador.</param>
    public static double Resize(double current, double delta,
                                double min, double max,
                                double available, double neighbourMin, double splitter)
    {
        double cap = Math.Min(max, available - neighbourMin - splitter);
        // Con la ventana muy pequeña el tope de arriba puede caer por debajo
        // del mínimo propio. Entonces gana el mínimo: el vecino se apaña.
        if (cap < min)
            cap = min;
        return Math.Clamp(current + delta, min, cap);
    }
}
