namespace HapLab.Tests;

/// <summary>
/// Tests de la referencia CPU de la conversión Scaled YCoCg→RGB de HapY (la
/// misma matemática que el pixel shader de HapLab.View). Valida el shader
/// conceptualmente con texels de valor conocido.
/// </summary>
public sealed class YCoCgReferenceTests
{
    private const double Tolerancia = 1e-9;

    [Theory]
    [InlineData(0.0, 0.0)]   // negro
    [InlineData(0.5, 0.5)]   // gris medio
    [InlineData(1.0, 1.0)]   // blanco
    public void CromaNeutra_DevuelveGrisIgualALaY(double y, double esperado)
    {
        // Con Co = Cg = offset (croma neutra), RGB = (Y, Y, Y) para cualquier escala
        (double r, double g, double b) = YCoCgReference.ToRgb(
            YCoCgReference.ChromaOffset, YCoCgReference.ChromaOffset, 0.25, y);

        Assert.Equal(esperado, r, Tolerancia);
        Assert.Equal(esperado, g, Tolerancia);
        Assert.Equal(esperado, b, Tolerancia);
    }

    [Fact]
    public void EscalaCero_EsFactorUno()
    {
        // B = 0 → s = 1: la croma se aplica sin atenuar.
        // Co = offset + 0,25, Cg = offset → R = Y + 0,25; G = Y; B = Y - 0,25
        (double r, double g, double b) = YCoCgReference.ToRgb(
            YCoCgReference.ChromaOffset + 0.25, YCoCgReference.ChromaOffset, 0.0, 0.5);

        Assert.Equal(0.75, r, Tolerancia);
        Assert.Equal(0.5, g, Tolerancia);
        Assert.Equal(0.25, b, Tolerancia);
    }

    [Fact]
    public void EscalaMaxima_AtenuaLaCroma()
    {
        // B = 1 → s = 255/8 + 1 = 32,875: misma croma que arriba pero atenuada
        double s = (255.0 / 8.0) + 1.0;
        (double r, double g, double b) = YCoCgReference.ToRgb(
            YCoCgReference.ChromaOffset + 0.25, YCoCgReference.ChromaOffset, 1.0, 0.5);

        Assert.Equal(0.5 + (0.25 / s), r, Tolerancia);
        Assert.Equal(0.5, g, Tolerancia);
        Assert.Equal(0.5 - (0.25 / s), b, Tolerancia);
    }

    [Fact]
    public void CgPuro_SumaEnVerdeYRestaEnRojoYAzul()
    {
        // Co = offset, Cg = offset + 0,2, s = 1:
        // R = Y - Cg; G = Y + Cg; B = Y - Cg
        (double r, double g, double b) = YCoCgReference.ToRgb(
            YCoCgReference.ChromaOffset, YCoCgReference.ChromaOffset + 0.2, 0.0, 0.5);

        Assert.Equal(0.3, r, Tolerancia);
        Assert.Equal(0.7, g, Tolerancia);
        Assert.Equal(0.3, b, Tolerancia);
    }

    [Fact]
    public void InversaDeLaCodificacion_RecuperaElRgbOriginal()
    {
        // Codificación YCoCg (sin escala): Y = R/4 + G/2 + B/4;
        // Co = R/2 - B/2; Cg = -R/4 + G/2 - B/4. El decodificador debe invertirla.
        (double rIn, double gIn, double bIn) = (0.8, 0.4, 0.2);
        double y = (rIn / 4) + (gIn / 2) + (bIn / 4);
        double co = (rIn / 2) - (bIn / 2);
        double cg = (-rIn / 4) + (gIn / 2) - (bIn / 4);

        (double r, double g, double b) = YCoCgReference.ToRgb(
            YCoCgReference.ChromaOffset + co, YCoCgReference.ChromaOffset + cg, 0.0, y);

        Assert.Equal(rIn, r, Tolerancia);
        Assert.Equal(gIn, g, Tolerancia);
        Assert.Equal(bIn, b, Tolerancia);
    }
}
