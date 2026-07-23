// =============================================================================
// HapShaders.cs — Shaders HLSL del pipeline HAP, compilados en runtime con
// D3DCompiler (promocionado de tools/hap-lab/HapLab.View/Shaders.cs)
// =============================================================================

using Vortice.D3DCompiler;

namespace WinCue.Video.Hap;

/// <summary>
/// Fuente única con el VS y los dos pixel shaders del pipeline HAP. El
/// triángulo fullscreen sale de SV_VertexID (sin vertex buffer ni input
/// layout). La conversión de HapY es la Scaled YCoCg→RGB del shader de
/// referencia de Vidvox (hap-in-avfoundation, ScaledCoCgYToRGBA.frag),
/// validada en el lab contra el decode de ffmpeg (PSNR 51,6 dB).
/// </summary>
internal static class HapShaders
{
    private const string Source = """
        struct VsOut
        {
            float4 pos : SV_Position;
            float2 uv  : TEXCOORD0;
        };

        VsOut VsFullscreen(uint id : SV_VertexID)
        {
            // Triángulo que cubre la pantalla: (0,0) (2,0) (0,2) en uv
            VsOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            o.uv = uv;
            return o;
        }

        Texture2D tex0 : register(t0);
        SamplerState smp0 : register(s0);

        // Hap1 (BC1) y Hap5 (BC3): la GPU descomprime el DXT al muestrear.
        // Alfa forzado a 1: la mezcla por capas la hace el compositor (DWM).
        float4 PsPassthrough(VsOut i) : SV_Target
        {
            return float4(tex0.Sample(smp0, i.uv).rgb, 1.0);
        }

        // HapY (BC3 con YCoCg escalado): Co=R, Cg=G, escala=B, Y=A
        float4 PsScaledYCoCg(VsOut i) : SV_Target
        {
            float4 c = tex0.Sample(smp0, i.uv);
            float s = (c.b * (255.0 / 8.0)) + 1.0;
            float co = (c.r - 0.50196078431373) / s;
            float cg = (c.g - 0.50196078431373) / s;
            float y = c.a;
            return float4(y + co - cg, y + cg, y - co - cg, 1.0);
        }
        """;

    public static byte[] CompileVertexShader() => Compile("VsFullscreen", "vs_5_0");

    public static byte[] CompilePassthroughPixelShader() => Compile("PsPassthrough", "ps_5_0");

    public static byte[] CompileScaledYCoCgPixelShader() => Compile("PsScaledYCoCg", "ps_5_0");

    private static byte[] Compile(string entryPoint, string profile) =>
        Compiler.Compile(Source, entryPoint, "wincue-hap-shaders", profile,
            ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
}
