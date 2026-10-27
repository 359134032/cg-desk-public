using System.IO;
using System.Reflection;
using System.Text;

namespace CgDesk.Services;

internal static class FsrShaders
{
    const string Vertex = """
        struct VsOut { float4 pos : SV_Position; };
        VsOut FullVS(uint id : SV_VertexID)
        {
            VsOut output;
            float2 corner = float2((id << 1) & 2, id & 2);
            output.pos = float4(corner * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            return output;
        }
        """;

    public static byte[] VertexBytecode { get; private set; } = [];
    public static byte[] EasuBytecode { get; private set; } = [];
    public static byte[] RcasBytecode { get; private set; } = [];

    public static void Compile()
    {
        if (VertexBytecode.Length > 0)
        {
            return;
        }

        var common = Read("CgDesk.Shaders.ffx_a.h");
        var fsr = Read("CgDesk.Shaders.ffx_fsr1.h");
        VertexBytecode = CompileSource(Vertex, "FullVS", "vs_5_0");
        EasuBytecode = CompileSource(EasuSource(common, fsr), "EasuPS", "ps_5_0");
        RcasBytecode = CompileSource(RcasSource(common, fsr), "RcasPS", "ps_5_0");
    }

    static string EasuSource(string common, string fsr)
    {
        const string head = """
            #define A_GPU 1
            #define A_HLSL 1

            """;
        const string tail = """

            Texture2D InputTexture : register(t0);
            SamplerState LinearSampler : register(s0);
            #define FSR_EASU_F 1
            AF4 FsrEasuRF(AF2 p) { return InputTexture.GatherRed(LinearSampler, p); }
            AF4 FsrEasuGF(AF2 p) { return InputTexture.GatherGreen(LinearSampler, p); }
            AF4 FsrEasuBF(AF2 p) { return InputTexture.GatherBlue(LinearSampler, p); }

            """;
        const string end = """

            cbuffer EasuCB : register(b0)
            {
                uint4 con0;
                uint4 con1;
                uint4 con2;
                uint4 con3;
            };
            float4 EasuPS(float4 pos : SV_Position) : SV_Target
            {
                AF3 color;
                FsrEasuF(color, uint2(pos.xy), con0, con1, con2, con3);
                return float4(color, 1);
            }
            """;
        return head + common + tail + fsr + end;
    }

    static string RcasSource(string common, string fsr)
    {
        const string head = """
            #define A_GPU 1
            #define A_HLSL 1

            """;
        const string tail = """

            Texture2D InputTexture : register(t0);
            cbuffer RcasCB : register(b0)
            {
                uint4 con;
                uint4 extent;
            };
            #define FSR_RCAS_F 1
            AF4 FsrRcasLoadF(ASU2 p)
            {
                int2 q = clamp(int2(p), int2(0, 0), int2((int)extent.x - 1, (int)extent.y - 1));
                return InputTexture.Load(int3(q, 0));
            }
            void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) { }

            """;
        const string end = """

            float4 RcasPS(float4 pos : SV_Position) : SV_Target
            {
                AF1 r, g, b;
                FsrRcasF(r, g, b, uint2(pos.xy), con);
                return float4(r, g, b, 1);
            }
            """;
        return head + common + tail + fsr + end;
    }

    static byte[] CompileSource(string source, string entry, string profile)
    {
        return Vortice.D3DCompiler.Compiler.Compile(
            source,
            entry,
            entry + ".hlsl",
            profile,
            Vortice.D3DCompiler.ShaderFlags.OptimizationLevel3).ToArray();
    }

    static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("缺少着色器 " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
