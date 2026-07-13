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
        Assert.Equal("directsound", sm.Get<string>("audio_driver"));
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
}
