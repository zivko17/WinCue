using System.Globalization;
using System.Text;
using ProResLab;

// El informe se lee en consola: acentos y "—" tienen que salir bien.
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Consola redirigida sin soporte: no es motivo para abortar.
}

if (args.Length == 0)
{
    Uso();
    return 0;
}

string first = args[0];
if (first is "--licencia" or "--license")
{
    return Commands.Licencia();
}

if (first is "-h" or "--help" or "/?")
{
    Uso();
    return 0;
}

string path = first;
if (!File.Exists(path))
{
    Console.Error.WriteLine($"No existe el fichero: {path}");
    return 2;
}

int threads = Opt("--threads", Environment.ProcessorCount);
int repeat = Opt("--repeat", 1);
int maxFrames = Opt("--max-frames", int.MaxValue);
int streams = Opt("--concurrentes", 0);
int fuzz = Opt("--fuzz", 0);

if (fuzz > 0)
{
    return Commands.Fuzz(path, fuzz, Opt("--semilla", 1234));
}

if (streams > 0)
{
    return Commands.BenchConcurrent(path, streams, Opt("--threads-por-video", 1), repeat, maxFrames);
}

if (Has("--bench"))
{
    return Commands.BenchOne(path, threads, repeat, maxFrames);
}

return Commands.Info(path);

bool Has(string name) => Array.IndexOf(args, name) >= 0;

int Opt(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    if (i < 0 || i + 1 >= args.Length)
    {
        return fallback;
    }

    return int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
}

static void Uso()
{
    Console.WriteLine("""
        prores-lab — laboratorio del motor de vídeo propio con ProRes (paso 1)

          prores-lab --licencia
              Verifica que las DLL de FFmpeg cargadas son LGPL (y no GPL/nonfree)
              y que traen el decodificador 'prores'. Sale 0 solo si todo está bien.

          prores-lab <fichero.mov>
              Demux + decode del primer frame: perfil, colr, formato de píxel,
              planos y bytes por frame.

          prores-lab <fichero.mov> --bench [--threads N] [--repeat N] [--max-frames N]
              Decode sostenido: 1 hilo, slice, frame y slice+frame.

          prores-lab <fichero.mov> --concurrentes N [--threads-por-video N] [--repeat N]
              N vídeos SIMULTÁNEOS (la pregunta del directo).

          prores-lab <fichero.mov> --fuzz N [--semilla S]
              Robustez con bitstream corrompido: el proceso debe sobrevivir.

        La media se genera con generar-media.ps1 (ffmpeg del PATH).
        """);
}
