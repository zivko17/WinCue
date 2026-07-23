// =============================================================================
// ShowFileService.cs — Persistencia del show en disco (extraído de
// MainController, paso 1 del plan de docs/auditoria/02-arquitectura.md)
// =============================================================================
// Guardar (con backups rotados y rutas relativas), autosave silencioso,
// cargar (resolviendo rutas y marcando media faltante) y relocalización de
// medios. NO toca el estado de reproducción: lo que necesita del controlador
// (undo, parar todo antes de cargar, mover el playhead, log, invalidar caché
// de audio) entra como callbacks — el núcleo sigue siendo el dueño único de
// _running y compañía.
//
// Hilos: solo el hilo de UI (como el controlador).
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller;

public sealed class ShowFileService
{
    private readonly CueModel _model;
    private readonly IShowView _view;
    private readonly Action<string> _log;
    private readonly Action _pushUndo;
    private readonly Action _beforeLoad;             // parar todo + descartar standby
    private readonly Action<int> _setPlayhead;
    private readonly Action<int> _invalidateAudioCache;

    public ShowFileService(
        CueModel model,
        IShowView view,
        Action<string> log,
        Action pushUndo,
        Action beforeLoad,
        Action<int> setPlayhead,
        Action<int> invalidateAudioCache)
    {
        _model = model;
        _view = view;
        _log = log;
        _pushUndo = pushUndo;
        _beforeLoad = beforeLoad;
        _setPlayhead = setPlayhead;
        _invalidateAudioCache = invalidateAudioCache;
    }

    /// <summary>Guarda el show. El media que vive junto al .json (o debajo) se
    /// guarda con RUTA RELATIVA → el show es portátil entre máquinas.
    /// Devuelve true si el archivo se escribió (el llamante limpia su flag
    /// de cambios sin guardar).</summary>
    public bool SaveShow(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            path += ".json";
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";

        // Relativizar temporalmente y restaurar tras serializar
        var originals = new Dictionary<int, string>();
        foreach (var c in _model.Cues)
        {
            if (c.FilePath.Length == 0 || !Path.IsPathRooted(c.FilePath) || dir.Length == 0)
                continue;
            string rel = Path.GetRelativePath(dir, c.FilePath);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                originals[c.Id] = c.FilePath;
                c.FilePath = rel;
            }
        }
        try
        {
            RotateBackups(path);
            File.WriteAllText(path, _model.ToJson());
            _log($"Show guardado: {path}");
            _view.FlashStatus(Loc.T($"Show guardado: {Path.GetFileName(path)}", $"Show saved: {Path.GetFileName(path)}"));
            return true;
        }
        catch (IOException exc)
        {
            _view.ShowError(Loc.T("Error al guardar", "Save error"), exc.Message);
            return false;
        }
        finally
        {
            foreach (var c in _model.Cues)
                if (originals.TryGetValue(c.Id, out string? orig))
                    c.FilePath = orig;
        }
    }

    /// <summary>Exporta un show PORTÁTIL: copia todos los medios referenciados a
    /// una carpeta «media» junto al .json de destino y guarda el show con rutas
    /// relativas (SaveShow ya relativiza lo que cae bajo su carpeta). El show de
    /// TRABAJO no se altera: sus cues siguen apuntando a los originales.
    /// convertLossyAudioToWav = true → el audio comprimido (mp3, aac, ogg…) se
    /// transcodifica a WAV PCM en la copia. convertVideoToHap = true → los
    /// vídeos (no imágenes, no ya-HAP) se transcodifican a HAP .mov en la
    /// copia (arranque instantáneo y mínima CPU en directo; más disco y sus
    /// fundidos de opacidad van a corte por la ruta clásica). En ambos casos,
    /// si ffmpeg falla se COPIA el original tal cual. Devuelve (copiados,
    /// faltantes).</summary>
    public (int Copied, int Missing) ExportPortable(
        string targetShowPath, bool convertLossyAudioToWav = false,
        bool convertVideoToHap = false)
    {
        string ffmpegExe = SettingsManager.Instance.Get("ffmpeg_path", "");
        if (!targetShowPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            targetShowPath += ".json";
        string dir = Path.GetDirectoryName(Path.GetFullPath(targetShowPath)) ?? "";
        string mediaDir = Path.Combine(dir, "media");
        Directory.CreateDirectory(mediaDir);

        // Cada origen se copia UNA vez; nombres únicos ante colisiones dentro de
        // esta exportación (dos archivos distintos con el mismo nombre).
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // origen → destino abs
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, missing = 0;
        foreach (var c in _model.Cues)
        {
            if (c.FilePath.Length == 0 || map.ContainsKey(c.FilePath))
                continue;
            if (!File.Exists(c.FilePath))
            {
                missing++;
                continue;
            }
            // Nombre único dentro de esta exportación (dos orígenes distintos
            // con el mismo nombre no deben pisarse)
            string UniqueDest(string fileName)
            {
                string candidate = fileName;
                for (int n = 1; usedNames.Contains(candidate); n++)
                    candidate = $"{Path.GetFileNameWithoutExtension(fileName)}_{n}{Path.GetExtension(fileName)}";
                usedNames.Add(candidate);
                return Path.Combine(mediaDir, candidate);
            }

            // Audio lossy con conversión activada → el destino pasa a .wav
            bool convert = convertLossyAudioToWav && c.Type == CueType.Audio &&
                MediaImporter.LossyAudioFormats.Contains(Path.GetExtension(c.FilePath));
            // Vídeo (no imagen, no ya-HAP) con conversión activada → HAP .mov
            bool toHap = convertVideoToHap && c.Type == CueType.Video &&
                !MediaImporter.ImageFormats.Contains(Path.GetExtension(c.FilePath)) &&
                !MediaImporter.IsUnsupportedVideoCodec(c.FilePath);
            try
            {
                if (convert)
                {
                    string wavDest = UniqueDest(
                        Path.GetFileNameWithoutExtension(c.FilePath) + ".wav");
                    if (MediaImporter.ConvertToWav(c.FilePath, wavDest, ffmpegExe))
                    {
                        map[c.FilePath] = wavDest;
                        copied++;
                        continue;
                    }
                    // ffmpeg no disponible o falló: copiar el original tal cual
                    _log($"Export portable: conversión a WAV falló, se copia el original: {c.FilePath}");
                }
                else if (toHap)
                {
                    string hapDest = UniqueDest(
                        Path.GetFileNameWithoutExtension(c.FilePath) + ".mov");
                    if (MediaImporter.ConvertToHap(c.FilePath, hapDest, ffmpegExe))
                    {
                        map[c.FilePath] = hapDest;
                        copied++;
                        continue;
                    }
                    _log($"Export portable: conversión a HAP falló, se copia el original: {c.FilePath}");
                }
                string dest = UniqueDest(Path.GetFileName(c.FilePath));
                if (!string.Equals(Path.GetFullPath(c.FilePath), Path.GetFullPath(dest),
                        StringComparison.OrdinalIgnoreCase))
                    File.Copy(c.FilePath, dest, overwrite: true);
                map[c.FilePath] = dest;
                copied++;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                _log($"Export portable: no se pudo copiar {c.FilePath}: {exc.Message}");
                missing++;
            }
        }

        // Repuntar temporalmente al destino, guardar (SaveShow relativiza) y
        // restaurar → el modelo de trabajo queda intacto.
        var originals = new Dictionary<int, string>();
        foreach (var c in _model.Cues)
            if (map.TryGetValue(c.FilePath, out string? dest))
            {
                originals[c.Id] = c.FilePath;
                c.FilePath = dest;
            }
        bool ok;
        try
        {
            ok = SaveShow(targetShowPath);
        }
        finally
        {
            foreach (var c in _model.Cues)
                if (originals.TryGetValue(c.Id, out string? orig))
                    c.FilePath = orig;
        }
        if (!ok)
            return (copied, missing);   // SaveShow ya mostró el error

        _log($"Show portable exportado a {targetShowPath}: {copied} copiados, {missing} faltantes");
        _view.FlashStatus(missing > 0
            ? Loc.T($"Show portable exportado: {copied} medio(s) en «media» — ⚠ {missing} no encontrados",
                    $"Portable show exported: {copied} media file(s) in «media» — ⚠ {missing} not found")
            : Loc.T($"Show portable exportado: {copied} medio(s) copiados a «media».",
                    $"Portable show exported: {copied} media file(s) copied to «media»."));
        return (copied, missing);
    }

    /// <summary>Copias de seguridad rotadas al SOBRESCRIBIR un show:
    /// show.json → show.json.bak1 → .bak2 → .bak3. Un guardado malo en
    /// directo nunca es irreversible.</summary>
    private static void RotateBackups(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            for (int i = 2; i >= 1; i--)
                if (File.Exists($"{path}.bak{i}"))
                    File.Copy($"{path}.bak{i}", $"{path}.bak{i + 1}", overwrite: true);
            File.Copy(path, $"{path}.bak1", overwrite: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Sin backup no se bloquea el guardado principal
        }
    }

    /// <summary>Guardado silencioso para el autosave (sin diálogos ni status).</summary>
    public bool Autosave(string path)
    {
        if (_model.Count == 0)
            return false;
        try
        {
            File.WriteAllText(path, _model.ToJson());
            return true;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Carga un show: resuelve rutas relativas contra la carpeta del
    /// .json y marca en ERROR los cues cuyo media no existe. Devuelve true si
    /// el show quedó cargado (el llamante limpia su flag de cambios).</summary>
    public bool LoadShow(string path)
    {
        List<Cue> newCues;
        try
        {
            newCues = CueModel.FromJson(File.ReadAllText(path));
        }
        catch (Exception exc) when (exc is IOException or FormatException)
        {
            _view.ShowError(Loc.T("Error al cargar", "Load error"), exc.Message);
            return false;
        }
        if (newCues.Count == 0)
        {
            _view.ShowError(Loc.T("Show vacío", "Empty show"), Loc.T("El archivo no contiene ningún cue.", "The file contains no cues."));
            return false;
        }

        _pushUndo();     // deshacer la carga restaura el show anterior
        _beforeLoad();   // parar todo + descartar standby (ids antiguos ya no valen)

        // Rutas relativas → absolutas respecto a la carpeta del show
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        foreach (var c in newCues)
            if (c.FilePath.Length > 0 && !Path.IsPathRooted(c.FilePath) && dir.Length > 0)
                c.FilePath = Path.GetFullPath(Path.Combine(dir, c.FilePath));

        _model.ReplaceAll(newCues);

        // Media faltante → ERROR. Los vídeos HAP se SONDEAN AQUÍ (reproducción
        // parada = seguro) para calentar la caché del enrutado: en caliente el
        // controlador solo consulta la caché, nunca lanza ffprobe en el hilo de
        // UI. HAP no es error: se reproduce por la ruta clásica.
        int missing = 0, classicPath = 0;
        for (int i = 0; i < _model.Count; i++)
        {
            var c = _model.Cues[i];
            if ((c.IsMedia() || c.Type == CueType.Script) &&
                c.FilePath.Length > 0 && !File.Exists(c.FilePath))
            {
                _model.SetState(i, CueState.Error);
                missing++;
            }
            else if (c.Type == CueType.Video && c.FilePath.Length > 0 &&
                     MediaImporter.IsUnsupportedVideoCodec(c.FilePath))
            {
                classicPath++;
            }
        }

        _view.ReloadTable();
        _setPlayhead(0);
        _log($"Show cargado: {path} ({missing} faltantes, {classicPath} HAP por ruta clásica)");
        string warn = missing > 0
            ? Loc.T($" — ⚠ {missing} archivo(s) NO encontrados", $" — ⚠ {missing} file(s) NOT found")
            : "";
        warn += classicPath > 0
            ? Loc.T($" — {classicPath} vídeo(s) HAP → ruta clásica (sin fundidos de opacidad)",
                    $" — {classicPath} HAP video(s) → classic path (no opacity fades)")
            : "";
        _view.FlashStatus(warn.Length > 0
            ? Loc.T($"Show cargado{warn}", $"Show loaded{warn}")
            : Loc.T($"Show cargado: {Path.GetFileName(path)}", $"Show loaded: {Path.GetFileName(path)}"));
        return true;
    }

    /// <summary>Cues de media/script cuyo archivo NO existe en disco.</summary>
    public int MissingMediaCount()
    {
        int missing = 0;
        foreach (var c in _model.Cues)
            if ((c.IsMedia() || c.Type == CueType.Script)
                && c.FilePath.Length > 0 && !File.Exists(c.FilePath))
                missing++;
        return missing;
    }

    /// <summary>Relocaliza los medios faltantes buscando POR NOMBRE DE
    /// ARCHIVO (recursivo) en la carpeta dada — el caso "show movido de
    /// máquina". Devuelve cuántos se recuperaron.</summary>
    public int RelocateMedia(string folder)
    {
        if (!Directory.Exists(folder))
            return 0;

        // Índice nombre → primera ruta encontrada
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string f in Directory.EnumerateFiles(
                folder, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (!index.ContainsKey(name))
                    index[name] = f;
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Carpeta parcialmente ilegible: se relocaliza lo que se pudo indexar
        }

        _pushUndo();
        int recovered = 0;
        for (int i = 0; i < _model.Count; i++)
        {
            var c = _model.Cues[i];
            if (!(c.IsMedia() || c.Type == CueType.Script)
                || c.FilePath.Length == 0 || File.Exists(c.FilePath))
                continue;
            if (index.TryGetValue(Path.GetFileName(c.FilePath), out string? found))
            {
                c.FilePath = found;
                c.LatencyMs = null;
                _model.SetState(i, CueState.Idle);
                _invalidateAudioCache(c.Id);
                recovered++;
            }
        }

        if (recovered > 0)
            _view.ReloadTable();
        int stillMissing = MissingMediaCount();
        _log($"Relocalización de medios: {recovered} recuperados, {stillMissing} pendientes ({folder})");
        _view.FlashStatus(stillMissing > 0
            ? Loc.T($"Relocalizados {recovered} archivo(s) — ⚠ quedan {stillMissing} sin encontrar",
                    $"Relocated {recovered} file(s) — ⚠ {stillMissing} still missing")
            : Loc.T($"Relocalizados {recovered} archivo(s). Todos los medios OK.",
                    $"Relocated {recovered} file(s). All media OK."));
        return recovered;
    }
}
