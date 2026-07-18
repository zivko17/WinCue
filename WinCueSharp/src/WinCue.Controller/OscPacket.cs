// =============================================================================
// OscPacket.cs — Parser mínimo de OSC 1.0 (sin dependencias)
// =============================================================================
// Suficiente para el control remoto (Companion, TouchOSC, consolas): mensajes
// con dirección + type tags (i, f, s, b, T, F, N, d, h) y #bundle anidados
// (el timetag se ignora: todo se ejecuta al llegar). Big-endian según el
// estándar. Un datagrama corrupto devuelve lista vacía, nunca lanza.
// =============================================================================

using System.Buffers.Binary;
using System.Text;

namespace WinCue.Controller;

public static class OscPacket
{
    /// <summary>Extrae todos los mensajes de un datagrama (mensaje o bundle).</summary>
    public static List<(string Address, List<object?> Args)> Parse(byte[] data)
    {
        var result = new List<(string, List<object?>)>();
        try
        {
            ParseElement(data, 0, data.Length, result, depth: 0);
        }
        catch (Exception exc) when (exc is ArgumentException or IndexOutOfRangeException
            or ArgumentOutOfRangeException or DecoderFallbackException)
        {
            // Datagrama malformado: ignorar (el show nunca se cae por la red)
        }
        return result;
    }

    private static void ParseElement(byte[] d, int start, int length,
        List<(string, List<object?>)> sink, int depth)
    {
        if (length <= 0 || depth > 8)
            return;
        int end = start + length;

        // Bundle: "#bundle\0" + timetag(8) + [int32 tamaño + elemento]...
        if (length >= 16 && d[start] == (byte)'#')
        {
            int pos = start + 16;   // "#bundle\0" (8) + timetag (8)
            while (pos + 4 <= end)
            {
                int size = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos, 4));
                pos += 4;
                if (size <= 0 || pos + size > end)
                    break;
                ParseElement(d, pos, size, sink, depth + 1);
                pos += size;
            }
            return;
        }

        // Mensaje: dirección + ",typetags" + argumentos
        int p = start;
        string address = ReadPaddedString(d, ref p, end);
        if (address.Length == 0 || address[0] != '/')
            return;

        var args = new List<object?>();
        if (p < end && d[p] == (byte)',')
        {
            string tags = ReadPaddedString(d, ref p, end);
            foreach (char tag in tags.AsSpan(1))
            {
                switch (tag)
                {
                    case 'i':
                        args.Add(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p, 4)));
                        p += 4;
                        break;
                    case 'f':
                        args.Add(BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(p, 4)));
                        p += 4;
                        break;
                    case 'd':
                        args.Add(BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(p, 8)));
                        p += 8;
                        break;
                    case 'h':
                        args.Add(BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(p, 8)));
                        p += 8;
                        break;
                    case 's' or 'S':
                        args.Add(ReadPaddedString(d, ref p, end));
                        break;
                    case 'b':
                        int blobLen = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p, 4));
                        p += 4;
                        if (blobLen < 0 || p + blobLen > end)
                            return;
                        args.Add(d.AsSpan(p, blobLen).ToArray());
                        p += Align4(blobLen);
                        break;
                    case 'T': args.Add(true); break;
                    case 'F': args.Add(false); break;
                    case 'N': args.Add(null); break;
                    default:
                        // Tag desconocido: no sabemos su tamaño → parar aquí
                        sink.Add((address, args));
                        return;
                }
            }
        }
        sink.Add((address, args));
    }

    private static string ReadPaddedString(byte[] d, ref int pos, int end)
    {
        int nul = Array.IndexOf(d, (byte)0, pos, end - pos);
        if (nul < 0)
        {
            pos = end;
            return "";
        }
        string s = Encoding.UTF8.GetString(d, pos, nul - pos);
        pos += Align4(nul - pos + 1);
        return s;
    }

    /// <summary>Redondea hacia arriba al múltiplo de 4 (padding OSC).</summary>
    private static int Align4(int n) => (n + 3) & ~3;

    // ------------------------------------------------------------------
    // Construcción (feedback de salida hacia Companion/TouchOSC)
    // ------------------------------------------------------------------

    /// <summary>Serializa un mensaje OSC (int→i, float/double→f, string→s,
    /// bool→T/F). Inverso de Parse.</summary>
    public static byte[] Build(string address, params object?[] args)
    {
        var bytes = new List<byte>(64);
        void PadString(string s)
        {
            var raw = Encoding.UTF8.GetBytes(s);
            bytes.AddRange(raw);
            int pad = 4 - (raw.Length % 4);
            for (int i = 0; i < pad; i++)
                bytes.Add(0);
        }
        void AddBE(int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            bytes.AddRange(b.ToArray());
        }

        PadString(address);
        var tags = new StringBuilder(",");
        foreach (object? a in args)
            tags.Append(a switch
            {
                int or long => 'i',
                float or double => 'f',
                bool b2 => b2 ? 'T' : 'F',
                _ => 's',
            });
        PadString(tags.ToString());
        // CA2014: el stackalloc va FUERA del bucle (dentro se acumulaba una
        // asignación de pila por argumento → desbordamiento con args de sobra)
        Span<byte> scratch = stackalloc byte[4];
        foreach (object? a in args)
        {
            switch (a)
            {
                case int i: AddBE(i); break;
                case long l: AddBE((int)l); break;
                case float f:
                    BinaryPrimitives.WriteSingleBigEndian(scratch, f);
                    bytes.AddRange(scratch.ToArray());
                    break;
                case double d:
                    BinaryPrimitives.WriteSingleBigEndian(scratch, (float)d);
                    bytes.AddRange(scratch.ToArray());
                    break;
                case bool: break;   // T/F van solo en los tags
                default: PadString(a?.ToString() ?? ""); break;
            }
        }
        return bytes.ToArray();
    }
}
