// =============================================================================
// OscWireTests.cs — Serialización y parseo del cable OSC (auditoría 03)
// =============================================================================
// Build estaba a 0% (el feedback hacia Companion nunca se había ejecutado en
// un test — y es el método del fix CA2014 de la fase 1) y las ramas de Parse
// para bundles, blobs y tipos extendidos (d/h/N) tampoco. Round-trips
// Build→Parse + datagramas construidos a mano byte a byte.
// =============================================================================

using System.Buffers.Binary;
using System.Text;

namespace WinCue.Controller.Tests;

public class OscWireTests
{
    // ── Helpers binarios (padding OSC: múltiplos de 4 con NUL) ─────────

    private static void PadString(List<byte> bytes, string s)
    {
        var raw = Encoding.UTF8.GetBytes(s);
        bytes.AddRange(raw);
        int pad = 4 - (raw.Length % 4);
        for (int i = 0; i < pad; i++) bytes.Add(0);
    }

    private static void AddBE32(List<byte> bytes, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        bytes.AddRange(b.ToArray());
    }

    private static byte[] Bundle(params byte[][] elements)
    {
        var bytes = new List<byte>();
        PadString(bytes, "#bundle");          // 8 bytes con el NUL de padding
        bytes.AddRange(new byte[8]);          // timetag (ignorado)
        foreach (var e in elements)
        {
            AddBE32(bytes, e.Length);
            bytes.AddRange(e);
        }
        return bytes.ToArray();
    }

    // ------------------------------------------------------------------
    // Build → Parse (round-trip del feedback a Companion)
    // ------------------------------------------------------------------

    [Fact]
    public void Build_RoundTrip_IntFloatStringBool()
    {
        byte[] wire = OscPacket.Build("/wincue/playhead", 7, 1.5f, "Intro", true, false);

        var (address, args) = Assert.Single(OscPacket.Parse(wire));
        Assert.Equal("/wincue/playhead", address);
        Assert.Equal(new object?[] { 7, 1.5f, "Intro", true, false }, args);
    }

    [Fact]
    public void Build_LongYDouble_DegradanAIntYFloat()
    {
        // Contrato documentado de Build: int→i, float/double→f (long→i)
        byte[] wire = OscPacket.Build("/x", 5_000_000_000L, 2.25);

        var (_, args) = Assert.Single(OscPacket.Parse(wire));
        Assert.Equal(unchecked((int)5_000_000_000L), args[0]);   // truncado como el cast
        Assert.Equal(2.25f, args[1]);
    }

    [Fact]
    public void Build_SinArgs_SoloDireccion()
    {
        var (address, args) = Assert.Single(OscPacket.Parse(OscPacket.Build("/go")));
        Assert.Equal("/go", address);
        Assert.Empty(args);
    }

    [Fact]
    public void Build_MuchosFloats_NoDesborda()
    {
        // El escenario del fix CA2014 (stackalloc que vivía dentro del bucle):
        // un mensaje con MUCHOS argumentos float debe construirse sin drama.
        object?[] args = Enumerable.Range(0, 500).Select(i => (object?)(float)i).ToArray();

        byte[] wire = OscPacket.Build("/stress", args);

        var (_, parsed) = Assert.Single(OscPacket.Parse(wire));
        Assert.Equal(500, parsed.Count);
        Assert.Equal(499f, parsed[^1]);
    }

    // ------------------------------------------------------------------
    // Parse: bundles (el plano ya está cubierto en RemoteControlTests)
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_BundleAnidado_TambienEntra()
    {
        byte[] wire = Bundle(Bundle(OscPacket.Build("/panic")));

        var (address, _) = Assert.Single(OscPacket.Parse(wire));
        Assert.Equal("/panic", address);
    }

    // ------------------------------------------------------------------
    // Parse: tipos extendidos y blobs
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_TiposExtendidos_DoubleInt64YNil()
    {
        var bytes = new List<byte>();
        PadString(bytes, "/ext");
        PadString(bytes, ",dhN");
        Span<byte> b8 = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b8, 3.5);
        bytes.AddRange(b8.ToArray());
        BinaryPrimitives.WriteInt64BigEndian(b8, 10_000_000_000L);
        bytes.AddRange(b8.ToArray());

        var (_, args) = Assert.Single(OscPacket.Parse(bytes.ToArray()));
        Assert.Equal(3.5, args[0]);
        Assert.Equal(10_000_000_000L, args[1]);
        Assert.Null(args[2]);
    }

    [Fact]
    public void Parse_Blob_ConPadding()
    {
        var bytes = new List<byte>();
        PadString(bytes, "/blob");
        PadString(bytes, ",bi");             // blob de 5 bytes (padding a 8) + int
        AddBE32(bytes, 5);
        bytes.AddRange(new byte[] { 1, 2, 3, 4, 5, 0, 0, 0 });
        AddBE32(bytes, 42);

        var (_, args) = Assert.Single(OscPacket.Parse(bytes.ToArray()));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, args[0]);
        Assert.Equal(42, args[1]);           // el padding del blob no descuadra
    }

    // ------------------------------------------------------------------
    // Parse: robustez ("el show nunca se cae por la red")
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_DatagramaCorrupto_DevuelveVacioSinLanzar()
    {
        Assert.Empty(OscPacket.Parse(Array.Empty<byte>()));
        Assert.Empty(OscPacket.Parse(new byte[] { 0xFF, 0x13, 0x00 }));
        Assert.Empty(OscPacket.Parse(Encoding.UTF8.GetBytes("sin barra inicial")));

        // Mensaje truncado a la mitad: no lanza (lista vacía o parcial)
        byte[] ok = OscPacket.Build("/cue/1/go", 1);
        _ = OscPacket.Parse(ok[..(ok.Length / 2)]);
    }

    [Fact]
    public void Parse_TagDesconocido_ConservaLosArgsAnteriores()
    {
        var bytes = new List<byte>();
        PadString(bytes, "/raro");
        PadString(bytes, ",iz");             // 'z' no existe → parar tras el int
        AddBE32(bytes, 9);

        var (_, args) = Assert.Single(OscPacket.Parse(bytes.ToArray()));
        Assert.Equal(9, Assert.Single(args));
    }
}
