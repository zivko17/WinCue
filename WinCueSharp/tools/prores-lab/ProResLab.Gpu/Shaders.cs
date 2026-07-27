using Vortice.D3DCompiler;

namespace ProResLab.Gpu;

/// <summary>
/// Shaders HLSL del camino de 10/12 bits, compilados en runtime con
/// D3DCompiler (misma decisión de lab que en hap-lab: sin paso de build).
/// </summary>
/// <remarks>
/// <para>
/// Los planos llegan como texturas <c>R16_UNORM</c>: la GPU devuelve
/// <c>código / 65535</c>, pero el código útil es de 10 o 12 bits alineado a la
/// derecha. Por eso la constante <c>yScale</c> lleva incorporado el factor
/// <c>65535 / (alto − bajo)</c> del rango, y <c>yOffset</c> el desplazamiento:
/// UNA multiplicación y una suma resuelven a la vez la profundidad de bits y
/// el rango limitado/completo.
/// </para>
/// <para>
/// El muestreo de CROMA es POINT por defecto (replicación horizontal), que es
/// exactamente lo que hace swscale de FFmpeg —y por tanto mpv— al convertir
/// 4:2:2 a RGB. Es la referencia contra la que se mide el PSNR. Con muestreo
/// lineal la imagen es (discutiblemente) mejor pero deja de ser bit-comparable
/// con ffmpeg: ver <c>--croma lineal</c> y el INFORME.
/// </para>
/// </remarks>
internal static class Shaders
{
    private const string Source = """
        cbuffer Params : register(b0)
        {
            float4 yScaleOffset;   // x = escala de luma,  y = offset de luma
            float4 cScaleOffset;   // x = escala de croma, y = offset de croma
            float4 coeff;          // Cr->R, Cb->G, Cr->G, Cb->B
            float4 flags;          // x = 1 si hay plano alfa
        };

        struct VsOut
        {
            float4 pos : SV_Position;
            float2 uv  : TEXCOORD0;
        };

        VsOut VsFullscreen(uint id : SV_VertexID)
        {
            VsOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            o.uv = uv;
            return o;
        }

        Texture2D<float> texY : register(t0);
        Texture2D<float> texU : register(t1);
        Texture2D<float> texV : register(t2);
        Texture2D<float> texA : register(t3);
        SamplerState smpPoint  : register(s0);   // luma y alfa (1:1)
        SamplerState smpChroma : register(s1);   // croma (point o lineal)

        float4 PsYuvToRgb(VsOut i) : SV_Target
        {
            float y = (texY.Sample(smpPoint,  i.uv) * yScaleOffset.x) + yScaleOffset.y;
            float u = (texU.Sample(smpChroma, i.uv) * cScaleOffset.x) + cScaleOffset.y;
            float v = (texV.Sample(smpChroma, i.uv) * cScaleOffset.x) + cScaleOffset.y;

            float3 rgb = float3(
                y + (coeff.x * v),
                y + (coeff.y * u) + (coeff.z * v),
                y + (coeff.w * u));

            float a = flags.x > 0.5
                ? (texA.Sample(smpPoint, i.uv) * yScaleOffset.x) + yScaleOffset.y
                : 1.0;

            return float4(saturate(rgb), saturate(a));
        }

        // Variante que IGNORA el alfa (opaco): el laboratorio compara contra
        // el decode de ffmpeg a BGRA, que tampoco premultiplica.
        float4 PsYuvToRgbOpaque(VsOut i) : SV_Target
        {
            float4 c = PsYuvToRgb(i);
            return float4(c.rgb, 1.0);
        }
        """;

    public static byte[] CompileVertexShader() => Compile("VsFullscreen", "vs_5_0");

    public static byte[] CompilePixelShader(bool keepAlpha)
        => Compile(keepAlpha ? "PsYuvToRgb" : "PsYuvToRgbOpaque", "ps_5_0");

    private static byte[] Compile(string entryPoint, string profile) =>
        Compiler.Compile(Source, entryPoint, "prores-lab-shaders", profile,
            ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
}
