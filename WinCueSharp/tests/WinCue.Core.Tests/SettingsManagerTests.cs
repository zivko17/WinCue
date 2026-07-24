// =============================================================================
// SettingsManagerTests.cs — Persistencia de configuración
// =============================================================================

using WinCue.Core;

namespace WinCue.Core.Tests;

public class SettingsManagerTests : IDisposable
{
    private readonly string _dir;
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public SettingsManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wincue_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FirstRun_CreatesFileWithDefaults()
    {
        var sm = new SettingsManager(SettingsPath);
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal("wasapi", sm.Get<string>("audio_driver"));
        Assert.Equal(48000, sm.Get<int>("audio_sample_rate"));
        Assert.Equal(100, sm.Get<int>("master_volume"));
        Assert.Equal("concurrent", sm.Get<string>("go_mode"));
        Assert.False(sm.Get<bool>("osc_enabled"));
    }

    [Fact]
    public void GetAll_NumerosConvertibles_ConCualquierRespaldo()
    {
        // Regresión: con un settings.json ANTIGUO (sin las claves nuevas),
        // los defaults ausentes del archivo quedan respaldados por int de C#
        // y GetValue<double>() LANZA — el diálogo de configuración crasheaba
        // al abrir. La conversión del diálogo usa Deserialize<>, que tolera
        // ambos respaldos: esto lo pina para TODOS los defaults numéricos.
        File.WriteAllText(SettingsPath, "{\"master_volume\": 80}");
        var sm = new SettingsManager(SettingsPath);
        foreach (var (key, node) in sm.GetAll())
            if (node is not null &&
                node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                double d = System.Text.Json.JsonSerializer.Deserialize<double>(node);
                Assert.False(double.IsNaN(d), $"clave {key}");
            }
    }

    [Fact]
    public void SetAndReload_PersistsValues()
    {
        var sm = new SettingsManager(SettingsPath);
        sm.Set("master_volume", 55);
        sm.Set("audio_driver", "asio");

        var sm2 = new SettingsManager(SettingsPath);
        Assert.Equal(55, sm2.Get<int>("master_volume"));
        Assert.Equal("asio", sm2.Get<string>("audio_driver"));
        // El resto conserva defaults
        Assert.Equal(48000, sm2.Get<int>("audio_sample_rate"));
    }

    [Fact]
    public void SetMany_SavesOnce()
    {
        var sm = new SettingsManager(SettingsPath);
        sm.SetMany(new Dictionary<string, object?>
        {
            ["osc_enabled"] = true,
            ["osc_listen_port"] = 9999,
        });
        var sm2 = new SettingsManager(SettingsPath);
        Assert.True(sm2.Get<bool>("osc_enabled"));
        Assert.Equal(9999, sm2.Get<int>("osc_listen_port"));
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(SettingsPath, "{esto no es json válido");
        var sm = new SettingsManager(SettingsPath);
        Assert.Equal("wasapi", sm.Get<string>("audio_driver"));
        // Y reescribe el archivo con contenido válido
        var sm2 = new SettingsManager(SettingsPath);
        Assert.Equal(48000, sm2.Get<int>("audio_sample_rate"));
    }

    [Fact]
    public void PythonSettingsFile_LoadsAndMergesWithDefaults()
    {
        // Un settings.json parcial escrito por la versión Python
        File.WriteAllText(SettingsPath, """
            {
              "audio_driver": "directsound",
              "master_volume": 80,
              "go_mode": "per_cue",
              "clave_vieja_desconocida": 1
            }
            """);
        var sm = new SettingsManager(SettingsPath);
        // El archivo no tiene settings_schema → v1 → migra hasta v3, y ahí
        // "directsound" (opción retirada, nunca implementada) pasa a "wasapi",
        // que es lo que el motor abría de facto.
        Assert.Equal("wasapi", sm.Get<string>("audio_driver"));
        Assert.Equal(80, sm.Get<int>("master_volume"));
        Assert.Equal("per_cue", sm.Get<string>("go_mode"));
        // Clave no presente en el archivo → default
        Assert.Equal(512, sm.Get<int>("audio_buffer_size"));
        // Clave desconocida se conserva (no rompe)
        Assert.Equal(1, sm.Get<int>("clave_vieja_desconocida"));
    }

    [Fact]
    public void MissingKey_ReturnsProvidedDefault()
    {
        var sm = new SettingsManager(SettingsPath);
        Assert.Equal("fallback", sm.Get("no_existe", "fallback"));
        Assert.Equal(-7, sm.Get("tampoco", -7));
    }

    [Fact]
    public void ResetDefaults_RestoresFactoryValues()
    {
        var sm = new SettingsManager(SettingsPath);
        sm.Set("master_volume", 10);
        sm.ResetDefaults();
        Assert.Equal(100, sm.Get<int>("master_volume"));
    }

    // ── v2→v3: retirada del driver "directsound" (nunca implementado) ──

    [Fact]
    public void MigrationV3_DirectSoundDriver_BecomesWasapi()
    {
        File.WriteAllText(SettingsPath, """
            {
              "settings_schema": 2,
              "audio_driver": "directsound",
              "audio_device_name": "Altavoces"
            }
            """);
        var sm = new SettingsManager(SettingsPath);
        Assert.Equal("wasapi", sm.Get<string>("audio_driver"));
        // El dispositivo elegido NO se toca: seguía saliendo por WASAPI
        Assert.Equal("Altavoces", sm.Get<string>("audio_device_name"));
        Assert.Equal(3, sm.Get<int>("settings_schema"));
    }

    [Fact]
    public void MigrationV3_AsioDriver_IsPreserved()
    {
        File.WriteAllText(SettingsPath, """
            {
              "settings_schema": 2,
              "audio_driver": "asio",
              "audio_device_name": "ASIO4ALL v2"
            }
            """);
        var sm = new SettingsManager(SettingsPath);
        Assert.Equal("asio", sm.Get<string>("audio_driver"));
        Assert.Equal("ASIO4ALL v2", sm.Get<string>("audio_device_name"));
    }

    [Fact]
    public void MigrationV3_IsIdempotent_AndPersisted()
    {
        File.WriteAllText(SettingsPath, """
            { "settings_schema": 2, "audio_driver": "directsound" }
            """);
        _ = new SettingsManager(SettingsPath);
        // Reabrir: ya está en v3, la migración no vuelve a correr y el valor
        // migrado quedó ESCRITO en disco
        var sm2 = new SettingsManager(SettingsPath);
        Assert.Equal("wasapi", sm2.Get<string>("audio_driver"));
        Assert.Equal(3, sm2.Get<int>("settings_schema"));
    }
}
