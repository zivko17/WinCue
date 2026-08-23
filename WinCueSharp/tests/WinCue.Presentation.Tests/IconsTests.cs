// =============================================================================
// IconsTests.cs — red del traslado del catálogo de iconos.
// =============================================================================
// No se comparan glifos literales: son caracteres del PUA y teclearlos en la
// prueba reproduciría el error que se quiere cazar. Se comprueban PROPIEDADES
// que un traslado chapucero rompe — que estén los 59, que ninguno se haya
// quedado vacío, que no haya dos con el mismo glifo (copiar-pegar) y que Get()
// alcance a todos.
// =============================================================================

using System.Reflection;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class IconsTests
{
    private static Dictionary<string, string> Todos() =>
        typeof(Icons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!,
                          StringComparer.Ordinal);

    [Fact]
    public void EstanLos59IconosQueTeniaLaInterfazDeWpf()
    {
        Assert.Equal(59, Todos().Count);
    }

    [Fact]
    public void NingunIconoSeQuedoVacioEnLaMudanza()
    {
        foreach (var (nombre, glifo) in Todos())
            Assert.False(string.IsNullOrEmpty(glifo), $"El icono «{nombre}» se quedó vacío.");
    }

    [Fact]
    public void NoHayDosIconosConElMismoGlifo()
    {
        // Un copiar-pegar mal rematado deja dos nombres apuntando al mismo
        // dibujo, y eso no lo ve nadie hasta que un botón sale con el icono
        // de otro.
        var repetidos = Todos()
            .GroupBy(p => p.Value)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" = ", g.Select(p => p.Key)))
            .ToList();

        Assert.True(repetidos.Count == 0,
            "Iconos que comparten glifo: " + string.Join("; ", repetidos));
    }

    [Fact]
    public void GetAlcanzaATodosLosIconosPorNombre()
    {
        foreach (var (nombre, glifo) in Todos())
            Assert.Equal(glifo, Icons.Get(nombre));
    }

    [Fact]
    public void GetCantaSiElNombreNoExiste()
    {
        // Contrato heredado: un icono mal escrito en XAML debe fallar al abrir
        // la ventana, no salir en blanco.
        var ex = Assert.Throws<ArgumentException>(() => Icons.Get("NoExisteEsteIcono"));
        Assert.Contains("NoExisteEsteIcono", ex.Message, StringComparison.Ordinal);
    }
}
