// =============================================================================
// Program.cs — Prueba de concepto: mpv (OpenGL) → textura D3D11 → compositor
// =============================================================================
// Cadena de veredictos, de menos a más comprometido. Cada uno depende del
// anterior; el primero que falla corta ESE adaptador.
//
//   T1  Device D3D11 en el adaptador pedido
//   T2  Contexto OpenGL + identificación del driver
//   T3  ¿GL y D3D11 en el MISMO adaptador? (en máquinas híbridas, no por defecto)
//   T4  ¿Existe WGL_NV_DX_interop2? (interop v1 solo garantiza D3D9)
//   T5  wglDXOpenDeviceNV contra un device D3D11  <- GO/NO-GO principal
//   T6  Registrar un ID3D11Texture2D como GL_TEXTURE_2D + FBO completo
//   T7  GL escribe un color conocido → D3D11 lo lee  ← prueba que CRUZAN bytes
//   T8  mpv renderiza un vídeo real dentro de la textura D3D11
//   T9  Compositor propio: dos capas mezcladas en UNA render target
//
// POR DEFECTO SE PRUEBAN TODOS LOS ADAPTADORES, uno tras otro. En un equipo
// con gráficos integrados + dedicada, el integrado es el caso que decide la
// arquitectura: la dedicada casi siempre sale bien.
//
// Uso:
//   mpv-d3d11-lab [--adapter=all|high|low|N] [ruta-al-video]
// =============================================================================

using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MpvD3D11Lab;

internal static class Program
{
    private const int W = 1920, H = 1080;

    private static int _failures;
    private static string _outDir = ".";

    private sealed class AdapterResult
    {
        public required string Name;
        public required string GlRenderer;
        public int Failures;
        public string Verdict = "";
    }

    private static int Main(string[] args)
    {
        // La consola de Windows no viene en UTF-8: sin esto, los acentos y los
        // caracteres de línea salen como basura en el equipo de destino.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (System.IO.IOException) { }

        Console.WriteLine("=== LAB mpv -> D3D11 (prueba de concepto del compositor) ===");
        Console.WriteLine($"    {Environment.OSVersion.VersionString} - {RuntimeInformation.OSArchitecture}");
        Console.WriteLine();

        string baseDir = FindRepoRoot() ?? AppContext.BaseDirectory;
        _outDir = Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(_outDir);

        string selector = "all";
        string? mediaArg = null;
        foreach (string a in args)
        {
            if (a.StartsWith("--adapter=", StringComparison.OrdinalIgnoreCase))
                selector = a["--adapter=".Length..];
            else
                mediaArg = a;
        }

        string? media = mediaArg ?? FindMedia(baseDir);
        if (media is null || !File.Exists(media))
        {
            Console.WriteLine("[FALLA] No encuentro el vídeo de prueba.");
            Console.WriteLine("        Pásale la ruta:  mpv-d3d11-lab ruta\\al\\video.mp4");
            return 2;
        }
        Console.WriteLine($"    vídeo: {media}");

        ResolveLibmpv(baseDir);

        var results = new List<AdapterResult>();
        try
        {
            using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            List<(int Index, string Name)> adapters = Enumerate(factory);

            Console.WriteLine();
            Console.WriteLine("Adaptadores encontrados:");
            foreach ((int i, string name) in adapters)
                Console.WriteLine($"   [{i}] {name}");

            foreach (int index in Select(factory, adapters, selector))
            {
                factory.EnumAdapters1((uint)index, out IDXGIAdapter1? adapter).CheckError();
                using IDXGIAdapter1 a = adapter!;
                results.Add(RunOnAdapter(a, index, media));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[EXCEPCIÓN] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 2;
        }

        // ── Resumen ─────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("=== RESUMEN ===");
        foreach (AdapterResult r in results)
        {
            Console.WriteLine($"  {(r.Failures == 0 ? "VERDE" : "ROJO ")}  {r.Name}");
            Console.WriteLine($"         GL: {r.GlRenderer}");
            if (r.Verdict.Length > 0)
                Console.WriteLine($"         {r.Verdict}");
        }
        Console.WriteLine();
        Console.WriteLine("Manda esta salida entera (y la carpeta 'out') de vuelta.");

        return results.Count > 0 && results.All(r => r.Failures == 0) ? 0 : 1;
    }

    private static List<(int, string)> Enumerate(IDXGIFactory6 factory)
    {
        var list = new List<(int, string)>();
        for (uint i = 0; ; i++)
        {
            SharpGen.Runtime.Result r = factory.EnumAdapters1(i, out IDXGIAdapter1? a);
            if (r.Failure || a is null)
                break;
            using (a)
            {
                AdapterDescription1 d = a.Description1;
                // Fuera el "Microsoft Basic Render Driver" (WARP): no dice nada
                if ((d.Flags & AdapterFlags.Software) == 0)
                    list.Add(((int)i, d.Description.Trim()));
            }
        }
        return list;
    }

    private static IEnumerable<int> Select(IDXGIFactory6 factory,
        List<(int Index, string Name)> adapters, string selector)
    {
        if (int.TryParse(selector, out int explicitIndex))
            return new[] { explicitIndex };

        if (selector.Equals("high", StringComparison.OrdinalIgnoreCase) ||
            selector.Equals("low", StringComparison.OrdinalIgnoreCase))
        {
            GpuPreference pref = selector.Equals("high", StringComparison.OrdinalIgnoreCase)
                ? GpuPreference.HighPerformance
                : GpuPreference.MinimumPower;
            using IDXGIAdapter1 pick = factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, pref);
            string name = pick.Description1.Description.Trim();
            foreach ((int i, string n) in adapters)
            {
                if (string.Equals(n, name, StringComparison.Ordinal))
                    return new[] { i };
            }
            return Array.Empty<int>();
        }

        return adapters.Select(a => a.Index);   // "all"
    }

    // -----------------------------------------------------------------------
    // La cadena de pruebas, sobre UN adaptador
    // -----------------------------------------------------------------------

    private static AdapterResult RunOnAdapter(IDXGIAdapter1 adapter, int index, string media)
    {
        _failures = 0;
        AdapterDescription1 ad = adapter.Description1;
        var result = new AdapterResult { Name = $"[{index}] {ad.Description.Trim()}", GlRenderer = "?" };

        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine($"ADAPTADOR {result.Name}");
        Console.WriteLine(new string('=', 70));

        // ── T1 ─ Device D3D11 ───────────────────────────────────────────────
        Section("T1 - Device D3D11");
        Console.WriteLine($"     VRAM dedicada : {ad.DedicatedVideoMemory / (1024 * 1024)} MB");
        ID3D11Device? device = null;
        ID3D11DeviceContext? ctx = null;
        try
        {
            FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                levels, out device, out ctx).CheckError();
            Check("device D3D11 creado", true, $"feature level {device!.FeatureLevel}");
        }
        catch (Exception ex)
        {
            Check("device D3D11 creado", false, ex.Message);
            result.Failures = _failures;
            result.Verdict = "no se pudo crear device D3D11";
            return result;
        }

        using ID3D11Device d3d = device!;
        using ID3D11DeviceContext dc = ctx!;

        // ── T2 ─ Contexto OpenGL ────────────────────────────────────────────
        Section("T2 - Contexto OpenGL");
        GlD3D11Interop? gl = null;
        try
        {
            gl = new GlD3D11Interop();
        }
        catch (Exception ex)
        {
            Check("contexto GL", false, ex.Message);
            result.Failures = _failures;
            result.Verdict = "sin OpenGL utilizable";
            return result;
        }

        using (gl)
        {
            result.GlRenderer = gl.GlRenderer;
            Console.WriteLine($"     GL_VENDOR   : {gl.GlVendor}");
            Console.WriteLine($"     GL_RENDERER : {gl.GlRenderer}");
            Console.WriteLine($"     GL_VERSION  : {gl.GlVersion}");
            Check("contexto GL current", true);
            Check("get_proc_address resuelve el core de GL 1.1 (glGetString)",
                gl.GetGlProc("glGetString") != 0);

            // ── T3 ─ ¿MISMO adaptador? ──────────────────────────────────────
            Section("T3 - ¿OpenGL y D3D11 en el mismo adaptador?");
            string? d3dVendor = VendorOf(ad.Description);
            string? glVendor = VendorOf(gl.GlRenderer);
            Console.WriteLine($"     D3D11 -> {d3dVendor ?? "?"}   |   OpenGL -> {glVendor ?? "?"}");
            bool sameVendor = d3dVendor is not null && d3dVendor == glVendor;
            Check("coinciden", sameVendor, sameVendor ? "" : "el interop EXIGE que sean el mismo");
            if (!sameVendor)
            {
                Console.WriteLine();
                Console.WriteLine("     Esto NO es un fallo del driver: es que Windows le da a este");
                Console.WriteLine("     proceso el OpenGL de otra GPU. Para probar ESTA de verdad:");
                Console.WriteLine();
                Console.WriteLine("       1. Configuración → Sistema → Pantalla → Gráficos");
                Console.WriteLine("       2. Agregar escritorio → elegir mpv-d3d11-lab.exe");
                Console.WriteLine("       3. Opciones → 'Ahorro de energía'  (= gráficos integrados)");
                Console.WriteLine("          o 'Alto rendimiento' (= la dedicada), según cuál toque");
                Console.WriteLine("       4. Volver a lanzar:  mpv-d3d11-lab.exe --adapter=low");
                Console.WriteLine();
            }
            if (Bail())
            {
                result.Failures = _failures;
                result.Verdict = "OpenGL y D3D11 en GPUs distintas — forzar la GPU del proceso y repetir";
                return result;
            }

            // ── T4 ─ Extensiones ────────────────────────────────────────────
            Section("T4 - Extensiones WGL_NV_DX_interop");
            bool v1 = gl.HasExtension("WGL_NV_DX_interop");
            bool v2 = gl.HasExtension("WGL_NV_DX_interop2");
            Console.WriteLine($"     WGL_NV_DX_interop  : {(v1 ? "sí" : "NO")}");
            Console.WriteLine($"     WGL_NV_DX_interop2 : {(v2 ? "sí" : "NO")}");
            Check("interop v1 (la de la ruta D3D9 actual)", v1);
            Check("interop v2 (la que habilita objetos D3D11)", v2);
            if (Bail())
            {
                result.Failures = _failures;
                result.Verdict = v1
                    ? "sin interop2: este driver NO puede con el motor nuevo"
                    : "sin interop: este driver no puede ni con la ruta actual";
                return result;
            }

            // ── T5 ─ GO/NO-GO ───────────────────────────────────────────────
            Section("T5 - wglDXOpenDeviceNV(ID3D11Device)   <- GO/NO-GO");
            bool opened = gl.OpenDevice(d3d.NativePointer);
            Check("interop abierto contra un device D3D11", opened,
                opened ? "" : $"GetLastError=0x{Marshal.GetLastWin32Error():X}");
            if (Bail())
            {
                result.Failures = _failures;
                result.Verdict = "el driver rechaza el interop con D3D11";
                return result;
            }

            // ── T6 ─ Registro ───────────────────────────────────────────────
            Section("T6 - Registro de ID3D11Texture2D como GL_TEXTURE_2D");
            GlD3D11Interop.Registration? reg = null;
            ID3D11Texture2D? interopTex = null;
            string winner = "";

            foreach ((string name, bool shared) in new[]
                     {
                         ("Shared + wglDXSetResourceShareHandleNV", true),
                         ("sin Shared, registro directo", false),
                     })
            {
                ID3D11Texture2D tex = d3d.CreateTexture2D(new Texture2DDescription
                {
                    Width = W,
                    Height = H,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    MiscFlags = shared ? ResourceOptionFlags.Shared : ResourceOptionFlags.None,
                });

                nint handle = 0;
                if (shared)
                {
                    using IDXGIResource res = tex.QueryInterface<IDXGIResource>();
                    handle = res.SharedHandle;
                }

                reg = gl.Register(tex.NativePointer, handle, W, H, out string detail);
                if (reg is not null)
                {
                    interopTex = tex;
                    winner = name;
                    Console.WriteLine($"     variante OK : {name}");
                    break;
                }
                Console.WriteLine($"     variante KO : {name} — {detail}");
                tex.Dispose();
            }

            Check("textura D3D11 registrada en el interop GL", reg is not null, winner);
            if (Bail())
            {
                result.Failures = _failures;
                result.Verdict = "el driver no registra texturas D3D11 en GL";
                return result;
            }

            using ID3D11Texture2D tex2d = interopTex!;
            using ID3D11ShaderResourceView interopSrv = d3d.CreateShaderResourceView(tex2d, null);

            // ── T7 ─ ¿Cruzan los bytes? ─────────────────────────────────────
            Section("T7 - GL escribe → D3D11 lee (prueba de píxel)");
            const byte SrcR = 0x20, SrcG = 0x40, SrcB = 0x80;
            gl.ClearFromGl(reg!, SrcR / 255f, SrcG / 255f, SrcB / 255f, 1f);

            byte[] back = Gpu.ReadBack(d3d, dc, tex2d, W, H);
            var px = Gpu.Pixel(back, W, W / 2, H / 2);
            Console.WriteLine($"     GL escribió  R={SrcR:X2} G={SrcG:X2} B={SrcB:X2}");
            Console.WriteLine($"     D3D11 leyó   R={px.R:X2} G={px.G:X2} B={px.B:X2} A={px.A:X2}");

            bool exact = px.R == SrcR && px.G == SrcG && px.B == SrcB;
            bool swapped = px.R == SrcB && px.B == SrcR;
            Check("los píxeles cruzan GL → D3D11", exact || swapped,
                exact ? "orden de canales correcto"
                      : swapped ? "OJO: canales R/B permutados" : "");
            if (Bail())
            {
                result.Failures = _failures;
                result.Verdict = "el interop no transfiere píxeles";
                return result;
            }

            // ── T8 ─ mpv ────────────────────────────────────────────────────
            Section("T8 - mpv renderiza un vídeo real en la textura D3D11");
            byte[]? mpvFrame = RenderWithMpv(gl, reg!, d3d, dc, tex2d, media);
            if (mpvFrame is null)
            {
                result.Failures = _failures;
                result.Verdict = "mpv no pudo renderizar por el interop";
                return result;
            }

            double luma = Gpu.MeanLuma(mpvFrame);
            string bmp7 = Path.Combine(_outDir, $"adaptador{index}-t8-mpv.bmp");
            Bmp.Save(bmp7, mpvFrame, W, H);
            Console.WriteLine($"     luma media : {luma:F1}");
            Console.WriteLine($"     volcado    : {Path.GetFileName(bmp7)}");
            Check("el frame de mpv está en la textura D3D11 (no es negro)", luma > 2.0);
            if (Bail())
            {
                result.Failures = _failures;
                result.Verdict = "mpv rinde negro";
                return result;
            }

            // ── T9 ─ Compositor ─────────────────────────────────────────────
            Section("T9 - Compositor D3D11: dos capas en UNA render target");
            using var comp = new Compositor(d3d, dc, W, H);
            const byte MagR = 0xFF, MagG = 0x00, MagB = 0xFF;
            using ID3D11Texture2D solid = d3d.CreateTexture2D(new Texture2DDescription
            {
                Width = W,
                Height = H,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            using ID3D11RenderTargetView solidRtv = d3d.CreateRenderTargetView(solid, null);
            using ID3D11ShaderResourceView solidSrv = d3d.CreateShaderResourceView(solid, null);
            dc.ClearRenderTargetView(solidRtv, new Color4(MagR / 255f, MagG / 255f, MagB / 255f, 1f));

            const float Opacity = 0.5f;
            comp.Begin(0, 0, 0);
            comp.DrawLayer(interopSrv, 1.0f);
            comp.DrawLayer(solidSrv, Opacity);
            dc.Flush();

            byte[] mixed = Gpu.ReadBack(d3d, dc, comp.Output, W, H);
            string bmp8 = Path.Combine(_outDir, $"adaptador{index}-t9-compositor.bmp");
            Bmp.Save(bmp8, mixed, W, H);

            int sx = W / 2, sy = H / 2;
            var vid = Gpu.Pixel(mpvFrame, W, sx, sy);
            var got = Gpu.Pixel(mixed, W, sx, sy);
            int expR = (int)Math.Round((MagR * Opacity) + (vid.R * (1 - Opacity)));
            int expG = (int)Math.Round((MagG * Opacity) + (vid.G * (1 - Opacity)));
            int expB = (int)Math.Round((MagB * Opacity) + (vid.B * (1 - Opacity)));
            Console.WriteLine($"     esperado   R={expR,3} G={expG,3} B={expB,3}");
            Console.WriteLine($"     compuesto  R={got.R,3} G={got.G,3} B={got.B,3}");
            Console.WriteLine($"     volcado    : {Path.GetFileName(bmp8)}");

            bool blendOk = Math.Abs(got.R - expR) <= 2
                        && Math.Abs(got.G - expG) <= 2
                        && Math.Abs(got.B - expB) <= 2;
            Check("la mezcla de dos capas da la aritmética esperada", blendOk);
        }

        result.Failures = _failures;
        result.Verdict = _failures == 0
            ? "el motor nuevo puede correr en este adaptador"
            : "revisar las líneas [FALLA] de arriba";
        return result;
    }

    /// <summary>Palabra de fabricante, para detectar el caso híbrido en el que
    /// OpenGL y D3D11 caen en GPUs distintas (y el interop no puede).</summary>
    private static string? VendorOf(string s)
    {
        if (s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (s.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (s.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD";
        return null;
    }

    // -----------------------------------------------------------------------
    // mpv: render API sobre el FBO de la textura registrada
    // -----------------------------------------------------------------------

    private static byte[]? RenderWithMpv(GlD3D11Interop gl,
        GlD3D11Interop.Registration reg, ID3D11Device d3d, ID3D11DeviceContext dc,
        ID3D11Texture2D tex, string media)
    {
        nint mpv = MpvMin.mpv_create();
        if (mpv == 0)
        {
            Check("mpv_create", false, "¿libmpv-2.dll no encontrado?");
            return null;
        }

        nint renderCtx = 0;
        nint apiType = MpvMin.Utf8("opengl");
        nint initParams = 0, createParams = 0, fboParam = 0, renderParams = 0, flipY = 0;
        var getProc = new MpvMin.GetProcAddressFn((_, name) =>
            gl.GetGlProc(Marshal.PtrToStringAnsi(name) ?? ""));

        try
        {
            MpvMin.mpv_set_option_string(mpv, "terminal", "no");
            MpvMin.mpv_set_option_string(mpv, "msg-level", "all=no");
            MpvMin.mpv_set_option_string(mpv, "vo", "libmpv");
            MpvMin.mpv_set_option_string(mpv, "ao", "null");
            MpvMin.mpv_set_option_string(mpv, "hwdec", "no");
            MpvMin.mpv_set_option_string(mpv, "keep-open", "yes");

            int rc = MpvMin.mpv_initialize(mpv);
            if (rc < 0)
            {
                Check("mpv_initialize", false, MpvMin.Err(rc));
                return null;
            }

            var init = new MpvMin.OpenGlInitParams
            {
                GetProcAddress = Marshal.GetFunctionPointerForDelegate(getProc),
                Ctx = 0,
            };
            initParams = Marshal.AllocHGlobal(Marshal.SizeOf<MpvMin.OpenGlInitParams>());
            Marshal.StructureToPtr(init, initParams, false);

            createParams = MpvMin.BuildParams(
                (MpvMin.ParamApiType, apiType),
                (MpvMin.ParamOpenGlInitParams, initParams));

            rc = MpvMin.mpv_render_context_create(out renderCtx, mpv, createParams);
            if (rc < 0)
            {
                Check("mpv_render_context_create (opengl)", false, MpvMin.Err(rc));
                return null;
            }
            Check("mpv_render_context_create (opengl)", true);

            rc = MpvMin.Command(mpv, "loadfile", media);
            if (rc < 0)
            {
                Check("loadfile", false, MpvMin.Err(rc));
                return null;
            }

            fboParam = Marshal.AllocHGlobal(Marshal.SizeOf<MpvMin.OpenGlFbo>());
            flipY = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(flipY, 1);
            renderParams = MpvMin.BuildParams(
                (MpvMin.ParamOpenGlFbo, fboParam),
                (MpvMin.ParamFlipY, flipY));

            var deadline = DateTime.UtcNow.AddSeconds(20);
            byte[]? frame = null;
            int renders = 0;
            while (DateTime.UtcNow < deadline)
            {
                _ = MpvMin.mpv_render_context_update(renderCtx);

                gl.RenderInto(reg, fbo =>
                {
                    Marshal.StructureToPtr(
                        new MpvMin.OpenGlFbo { Fbo = (int)fbo, W = W, H = H, InternalFormat = 0 },
                        fboParam, false);
                    int r = MpvMin.mpv_render_context_render(renderCtx, renderParams);
                    if (r < 0)
                        throw new InvalidOperationException($"mpv_render_context_render: {MpvMin.Err(r)}");
                });
                renders++;

                byte[] candidate = Gpu.ReadBack(d3d, dc, tex, W, H);
                if (Gpu.MeanLuma(candidate) > 2.0)
                {
                    frame = candidate;
                    break;
                }
                Thread.Sleep(30);
            }

            Console.WriteLine($"     renders hasta la primera imagen : {renders}");
            if (frame is null)
            {
                MpvMin.mpv_get_property(mpv, "time-pos", MpvMin.FormatDouble, out double t);
                Check("mpv entregó imagen antes del plazo", false, $"time-pos={t:F2}");
                return null;
            }
            return frame;
        }
        finally
        {
            if (renderCtx != 0) MpvMin.mpv_render_context_free(renderCtx);
            MpvMin.mpv_terminate_destroy(mpv);
            if (renderParams != 0) Marshal.FreeHGlobal(renderParams);
            if (flipY != 0) Marshal.FreeHGlobal(flipY);
            if (fboParam != 0) Marshal.FreeHGlobal(fboParam);
            if (createParams != 0) Marshal.FreeHGlobal(createParams);
            if (initParams != 0) Marshal.FreeHGlobal(initParams);
            Marshal.FreeHGlobal(apiType);
            GC.KeepAlive(getProc);
        }
    }

    // -----------------------------------------------------------------------
    // Infraestructura del lab
    // -----------------------------------------------------------------------

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        string mark = ok ? "[OK]   " : "[FALLA]";
        Console.WriteLine($"  {mark} {what}{(detail.Length > 0 ? "  — " + detail : "")}");
    }

    private static bool Bail()
    {
        if (_failures == 0) return false;
        Console.WriteLine("\n  ··· se corta este adaptador: lo siguiente depende de lo que ha fallado.");
        return true;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WinCueSharp.sln")))
                return dir.FullName;
        }
        return null;
    }

    /// <summary>Busca el clip: primero junto al ejecutable (paquete portable),
    /// luego en el repo.</summary>
    private static string? FindMedia(string baseDir)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "media", "prueba.mp4");
        if (File.Exists(bundled))
            return bundled;
        string inRepo = Path.Combine(baseDir, "tests", "hap-test", "demo-h264-1080p30-noaudio.mp4");
        return File.Exists(inRepo) ? inRepo : null;
    }

    private static void ResolveLibmpv(string baseDir)
    {
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, asm, path) =>
        {
            if (!name.Equals("libmpv-2.dll", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (NativeLibrary.TryLoad(name, asm, path, out nint h))
                return h;
            string? env = Environment.GetEnvironmentVariable("WINCUE_LIBMPV");
            if (!string.IsNullOrEmpty(env) && File.Exists(env) && NativeLibrary.TryLoad(env, out h))
                return h;
            foreach (string candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"),
                         Path.Combine(baseDir, "publish", "libmpv-2.dll"),
                     })
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out h))
                    return h;
            }
            return 0;
        });
    }
}
