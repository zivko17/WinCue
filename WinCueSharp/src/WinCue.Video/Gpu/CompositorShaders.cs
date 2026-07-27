// =============================================================================
// CompositorShaders.cs — HLSL del compositor (capa L3)
// =============================================================================
// Un solo par VS/PS para TODAS las capas. La geometría del destino va por
// viewport (como en el pipeline HAP), el recorte del origen por constant
// buffer y la opacidad por blend factor — así el shader es un passthrough y
// no hay una variante por combinación.
//
// El alfa se fuerza a 1 a propósito: la mezcla la decide el blend factor de la
// capa, no el canal alfa del contenido. Un vídeo con alfa basura (frecuente en
// material de cliente) no puede agujerear la mezcla.
// =============================================================================

using Vortice.D3DCompiler;

namespace WinCue.Video.Gpu;

internal static class CompositorShaders
{
    private const string Source = """
        cbuffer Layer : register(b0)
        {
            float4 uvXform;   // xy = escala del recorte, zw = desplazamiento
        };

        struct VsOut
        {
            float4 pos : SV_Position;
            float2 uv  : TEXCOORD0;
        };

        VsOut VsFullscreen(uint id : SV_VertexID)
        {
            // Triángulo que cubre el viewport: (0,0) (2,0) (0,2) en uv
            VsOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            o.uv = uv;
            return o;
        }

        Texture2D tex0 : register(t0);
        SamplerState smp0 : register(s0);

        float4 PsLayer(VsOut i) : SV_Target
        {
            float2 uv = i.uv * uvXform.xy + uvXform.zw;
            return float4(tex0.Sample(smp0, uv).rgb, 1.0);
        }
        """;

    public static byte[] CompileVertexShader() => Compile("VsFullscreen", "vs_5_0");

    public static byte[] CompilePixelShader() => Compile("PsLayer", "ps_5_0");

    private static byte[] Compile(string entryPoint, string profile) =>
        Compiler.Compile(Source, entryPoint, "wincue-compositor", profile,
            ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
}
