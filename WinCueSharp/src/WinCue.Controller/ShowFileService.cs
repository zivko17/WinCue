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

        // Media faltante → estado ERROR visible en la tabla
        int missing = 0;
        for (int i = 0; i < _model.Count; i++)
        {
            var c = _model.Cues[i];
            if ((c.IsMedia() || c.Type == CueType.Script) &&
                c.FilePath.Length > 0 && !File.Exists(c.FilePath))
            {
                _model.SetState(i, CueState.Error);
                missing++;
            }
        }

        _view.ReloadTable();
        _setPlayhead(0);
        _log($"Show cargado: {path}");
        _view.FlashStatus(missing > 0
            ? Loc.T($"Show cargado — ⚠ {missing} archivo(s) de media NO encontrados",
                    $"Show loaded — ⚠ {missing} media file(s) NOT found")
            : Loc.T($"Show cargado: {Path.GetFileName(path)}",
                    $"Show loaded: {Path.GetFileName(path)}"));
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
