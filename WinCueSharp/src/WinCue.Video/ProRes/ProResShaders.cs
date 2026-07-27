// =============================================================================
// ProResShaders.cs — Shaders HLSL del camino de 10/12 bits (YUV planar → RGB)
// =============================================================================
// Promocionado de tools/prores-lab/ProResLab.Gpu/Shaders.cs. Compilados en
// runtime con D3DCompiler, misma decisión que HapShaders: sin paso de build.
//
// Los planos llegan como texturas R16_UNORM: la GPU devuelve código/65535, pero
// el código útil es de 10 o 12 bits alineado a la DERECHA. Por eso yScale lleva
// incorporado el factor 65535/(alto − bajo) del rango y yOffset el
// desplazamiento — UNA multiplicación y una suma resuelven a la vez la
// profundidad de bits y el rango limitado/completo (ver ColorParams).
//
// El muestreo de CROMA es POINT (replicación horizontal), que es exactamente lo
// que hace swscale de FFmpeg —y por tanto mpv— al convertir 4:2:2 a RGB. El
// laboratorio midió la alternativa: con muestreo LINEAL el PSNR contra ffmpeg
// cae de 48,0 dB a 33,9 dB con máx |Δ| = 118. No es peor imagen, es una
// decisión de upsampling distinta; se elige POINT para que el operador vea
// EXACTAMENTE lo mismo que veía con mpv.
//
// Coste medido del shader: 0,02-0,08 ms por frame (presupuesto a 60 fps:
// 16,7 ms). El shader es gratis; lo que cuesta es la SUBIDA (§ 5 del INFORME).
// =============================================================================

using Vortice.D3DCompiler;

namespace WinCue.Video.ProRes;

internal static class ProResShaders
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
        SamplerState smpChroma : register(s1);   // croma (POINT: igual que swscale)

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

        // Variante OPACA: la mezcla entre capas la hace el compositor por
        // opacidad de superficie (igual que en HAP), no el alfa del medio. Es
        // la que usa el pipeline salvo que el cue pida conservar el alfa.
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
        Compiler.Compile(Source, entryPoint, "wincue-prores-shaders", profile,
            ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
}
