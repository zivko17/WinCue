// =============================================================================
// PflBusTests.cs — El bus de preescucha entrega LO QUE SUENA, y nada más
// =============================================================================
// Lo que se comprueba aquí es lo que no se puede comprobar de oído sin dudar:
// que la derivación no altera lo que va a la sala, que lo que llega a los cascos
// son esas mismas muestras, y que cuando los dos relojes se desfasan el bus
// hace lo correcto — tirar lo viejo o callar, nunca repetir audio.
// =============================================================================

using NAudio.Wave;
using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class PflBusTests
{
    private const int SR = 48_000;

    /// <summary>Fuente que entrega una rampa 1,2,3,… para poder seguir la pista
    /// a cada muestra por su valor.</summary>
    private sealed class Ramp : ISampleProvider
    {
        private float _next = 1f;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SR, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[offset + i] = _next++;
            return count;
        }
    }

    private static float[] Read(ISampleProvider p, int n)
    {
        var b = new float[n];
        p.Read(b, 0, n);
        return b;
    }

    [Fact]
    public void LaDerivacion_NoAlteraLoQueVaALaSala()
    {
        // Lo primero que no puede pasar: que pinchar los cascos toque la sala.
        var bus = new PflBus(SR, 2);
        var tap = new PflTap(new Ramp(), bus);
        tap.Armed = true;

        float[] salida = Read(tap, 8);

        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }, salida);
    }

    [Fact]
    public void ArmadoElCue_LosCascosOyenLasMISMASMuestras()
    {
        var bus = new PflBus(SR, 2);
        var tap = new PflTap(new Ramp(), bus) { Armed = true };

        Read(tap, 8);                       // el cue suena en la sala
        float[] cascos = Read(bus.Reader, 8);

        // No «algo parecido»: las mismas, en el mismo orden.
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }, cascos);
    }

    [Fact]
    public void SinArmar_LosCascosNoOyenNada()
    {
        var bus = new PflBus(SR, 2);
        var tap = new PflTap(new Ramp(), bus);   // sin armar

        Read(tap, 8);

        Assert.Equal(new float[8], Read(bus.Reader, 8));
    }

    [Fact]
    public void SinDatos_ElLectorEmiteSILENCIO_NoRepiteAudio()
    {
        // Si el lector va más rápido que el escritor, repetir lo último sonaría
        // a archivo roto. Callar se entiende; un eco corto, no.
        var bus = new PflBus(SR, 2);
        var tap = new PflTap(new Ramp(), bus) { Armed = true };
        Read(tap, 4);

        float[] cascos = Read(bus.Reader, 8);

        Assert.Equal(new float[] { 1, 2, 3, 4 }, cascos[..4]);
        Assert.Equal(new float[4], cascos[4..]);   // el resto, silencio
    }

    [Fact]
    public void ElLectorSiempreEntregaElBloqueEntero()
    {
        // El mixer del bus vive con ReadFully: si el lector devolviera menos de
        // lo pedido, NAudio lo daría por terminado y lo sacaría de la mezcla —
        // la preescucha se apagaría sola en el primer hueco.
        var bus = new PflBus(SR, 2);
        var b = new float[64];
        Assert.Equal(64, bus.Reader.Read(b, 0, 64));
    }

    [Fact]
    public void ConElAnilloLleno_SeTiraLoVIEJO_YSeOyeLoDeAHORA()
    {
        // Dos relojes independientes: a la larga el escritor le gana al lector.
        // En un monitor lo que importa es el ahora, así que lo viejo se cae.
        var bus = new PflBus(SR, 2, seconds: 0.0001);   // anillo diminuto a propósito
        var tap = new PflTap(new Ramp(), bus) { Armed = true };

        Read(tap, 4096);   // mucho más de lo que cabe

        float[] cascos = Read(bus.Reader, 8);
        // Lo último escrito fue 4096: lo que se oye tiene que venir del final,
        // no del principio.
        Assert.True(cascos[0] > 2000f,
            $"los cascos están sirviendo audio viejo (muestra {cascos[0]})");
    }

    [Fact]
    public void SinDispositivoDeCascos_NoSePinchaNADA()
    {
        // El bus "" es la SALIDA PRINCIPAL. Si la preescucha se enganchara ahí
        // por no haber cascos configurados, el cue sonaría DOBLE por los
        // altavoces en mitad de la función — y con el botón encendido, como si
        // todo fuera bien. Tiene que decir que no.
        using var engine = new AudioEngine();
        Assert.False(engine.SetPfl(1, ""));
        Assert.Equal(-1, engine.PflCueId);
    }

    [Fact]
    public void AlSoltarYVolverAPinchar_NoSeArrastraLaColaDelAnterior()
    {
        var bus = new PflBus(SR, 2);
        var tap = new PflTap(new Ramp(), bus) { Armed = true };
        Read(tap, 16);          // queda audio del cue anterior en el anillo

        bus.Clear();            // se suelta el CUE

        Assert.Equal(new float[8], Read(bus.Reader, 8));
    }
}
