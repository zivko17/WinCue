// =============================================================================
// Pantallas.cs — Enumerar los monitores sin que WinRT te muerda
// =============================================================================
// LA TRAMPA, y costó encontrarla porque estaba tapada por un catch mudo:
// la lista que devuelve DisplayArea.FindAll() es una proyección WinRT que
// expone IVectorView<DisplayArea> pero NO IIterable<DisplayArea>. Se puede
// preguntar .Count e indexar con [i], pero en cuanto alguien la ENUMERA —un
// foreach, o cualquier LINQ, que pide IEnumerable<T> por debajo— la
// proyección intenta el cast y lanza:
//
//     System.InvalidCastException: Specified cast is not valid.
//        at WinRT.IObjectReference.As[T](Guid iid)
//        at IReadOnlyListImpl`1.Make_IEnumerableObjRef()
//        at IReadOnlyListImpl`1.GetEnumerator()
//
// O sea que `DisplayArea.FindAll().Where(...)` NO devuelve una lista filtrada:
// lanza SIEMPRE, con cualquier número de monitores. Los dos sitios que lo
// hacían tenían su try/catch, así que el fallo salía como "aquí solo hay una
// pantalla": en Ajustes el desplegable ofrecía únicamente la principal, y la
// ventana de salida caía a la principal hiciera lo que hiciera el operador.
// Verificado en la máquina de Andrés (dos monitores, 1920x1080 + 1024x600):
// FindAll().Count == 2 e indexar da los dos, y .Where() sobre esa misma lista
// lanza en la línea siguiente.
//
// Por eso esto vive en UN solo sitio: las dos llamadas tienen que estar de
// acuerdo en el orden de los índices (es el contrato de video_output_screen*
// en settings.json), y dos copias de la misma cuenta es como divergen.
//
// Y por eso el catch de aquí AVISA en vez de callarse. Un catch mudo es lo que
// dejó esto vivo: el síntoma era indistinguible de "este equipo tiene un solo
// monitor", que es un caso legítimo y frecuente.
// =============================================================================

using Microsoft.UI.Windowing;

namespace Secuence.App;

internal static class Pantallas
{
    /// <summary>Los monitores que NO son el principal, en el orden que da
    /// <c>DisplayArea.FindAll()</c>. Es el contrato de índices de
    /// <c>video_output_screen*</c>: 0 = SIEMPRE la principal (explícito, no por
    /// orden); i&gt;0 = el (i−1)-ésimo de esta lista.
    /// <para>Se INDEXA, no se enumera — ver la cabecera del fichero.</para></summary>
    /// <param name="principal">La principal, para descartarla. Se pasa en vez de
    /// leerla aquí para que quien llame use la MISMA en la comparación y en la
    /// caída a la principal.</param>
    /// <param name="aviso">Dónde contar un fallo de enumeración. Nunca se calla:
    /// sin aviso, "no hay más monitores" y "no pude preguntarlo" se ven igual.</param>
    internal static List<DisplayArea> NoPrincipales(DisplayArea principal, Action<string>? aviso = null)
    {
        var otras = new List<DisplayArea>();
        try
        {
            var todas = DisplayArea.FindAll();
            for (int i = 0; i < todas.Count; i++)
            {
                var area = todas[i];
                if (area.DisplayId.Value != principal.DisplayId.Value)
                    otras.Add(area);
            }
        }
        catch (Exception ex)
        {
            // SIEMPRE al log, además del aviso opcional en pantalla: que este
            // fallo pudiera pasar inadvertido es precisamente lo que lo mantuvo
            // vivo. Va al mismo archivo que los fallos de colocación de salida
            // porque suelen ser el mismo incidente visto desde dos sitios.
            VideoOutputWindow.LogAviso(
                $"No se pudieron enumerar los monitores: {ex.GetType().Name}: {ex.Message}");
            aviso?.Invoke("No se pudieron enumerar los monitores. " +
                          "Solo se ofrece la pantalla principal.");
        }
        return otras;
    }
}
