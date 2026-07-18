// =============================================================================
// CueListEditor.cs — Edición de la lista de cues (extraído de MainController,
// paso 2 del plan de docs/auditoria/02-arquitectura.md)
// =============================================================================
// Portapapeles (copiar/pegar/duplicar con remapeo de ids y jerarquía), mover
// bloques (drag & drop), añadir/insertar/borrar/renumerar, agrupar/desagrupar/
// colapsar e importar archivos. Todo es manipulación del MODELO + undo; no
// toca el estado de reproducción: lo poco que necesita del núcleo (parar un
// cue sonando antes de borrarlo, olvidar el estado round-robin de un grupo
// eliminado) entra como callbacks.
//
// Hilos: solo el hilo de UI (como el controlador).
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller;

public sealed class CueListEditor
{
    private readonly CueModel _model;
    private readonly IShowView _view;
    private readonly Action _pushUndo;
    private readonly Action<int> _setPlayhead;
    private readonly Action<int> _stopIfRunning;      // parar el cue antes de borrarlo
    private readonly Action<int> _forgetGroupState;   // round-robin del grupo eliminado

    // Portapapeles interno de cues (JSON de una lista de cues)
    private string? _clipboard;

    private static readonly System.Text.Json.JsonSerializerOptions CueJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public CueListEditor(
        CueModel model,
        IShowView view,
        Action pushUndo,
        Action<int> setPlayhead,
        Action<int> stopIfRunning,
        Action<int> forgetGroupState)
    {
        _model = model;
        _view = view;
        _pushUndo = pushUndo;
        _setPlayhead = setPlayhead;
        _stopIfRunning = stopIfRunning;
        _forgetGroupState = forgetGroupState;
    }

    // ==================================================================
    // COPIAR / PEGAR / DUPLICAR
    // ==================================================================

    /// <summary>Copia los cues de las filas dadas (con sus descendientes).</summary>
    public void CopyCues(IReadOnlyList<int> rows)
    {
        string? json = SerializeRows(rows);
        if (json is null)
            return;
        _clipboard = json;
        _view.FlashStatus("Cues copiados");
    }

    /// <summary>Pega el portapapeles debajo de la fila dada, con ids nuevos.</summary>
    public void PasteCues(int row)
    {
        if (_clipboard is null)
            return;
        InsertSerialized(_clipboard, row);
    }

    /// <summary>Duplica las filas dadas justo debajo (sin tocar el portapapeles).</summary>
    public void DuplicateCues(IReadOnlyList<int> rows)
    {
        string? json = SerializeRows(rows);
        if (json is null)
            return;
        InsertSerialized(json, rows.Max());
    }

    private string? SerializeRows(IReadOnlyList<int> rows)
    {
        var valid = rows.Where(r => r >= 0 && r < _model.Count)
                        .Distinct().OrderBy(r => r).ToList();
        if (valid.Count == 0)
            return null;
        // Incluir descendientes de los grupos seleccionados (en orden de lista)
        var ids = new HashSet<int>();
        foreach (int r in valid)
        {
            var c = _model.Cues[r];
            ids.Add(c.Id);
            foreach (var d in _model.Descendants(c.Id))
                ids.Add(d.Id);
        }
        var block = _model.Cues.Where(c => ids.Contains(c.Id)).ToList();
        return System.Text.Json.JsonSerializer.Serialize(block, CueJson);
    }

    private void InsertSerialized(string json, int afterRow)
    {
        List<Cue> cues;
        try
        {
            cues = System.Text.Json.JsonSerializer.Deserialize<List<Cue>>(json, CueJson)
                ?? new List<Cue>();
        }
        catch (System.Text.Json.JsonException) { return; }
        if (cues.Count == 0)
            return;

        _pushUndo();

        // Ids nuevos preservando la jerarquía interna del bloque
        var idMap = new Dictionary<int, int>();
        int nextId = _model.NextId();
        foreach (var c in cues)
            idMap[c.Id] = nextId++;
        var refCue = afterRow >= 0 ? _model.GetCue(afterRow) : null;
        foreach (var c in cues)
        {
            int oldId = c.Id;
            c.Id = idMap[oldId];
            c.ParentId = c.ParentId is int pid && idMap.TryGetValue(pid, out int npid)
                ? npid                     // padre dentro del bloque pegado
                : refCue?.ParentId;        // nivel superior: hereda contexto
            c.State = CueState.Idle;
            c.LatencyMs = null;
        }

        int insertAt = afterRow >= 0 ? afterRow + 1 : _model.Count;
        for (int i = 0; i < cues.Count; i++)
            _model.InsertCue(insertAt + i, cues[i]);
        _view.ReloadTable();
        _setPlayhead(insertAt);
        _view.FlashStatus($"{cues.Count} cue(s) pegados");
    }

    /// <summary>Mueve un cue de fila (drag &amp; drop de la tabla o del timeline).
    /// Un GROUP se mueve como BLOQUE con todos sus descendientes, y el cue
    /// movido adopta el grupo del punto de destino (soltar entre los hijos de
    /// un grupo lo mete dentro; soltar fuera lo saca).</summary>
    public void OnMoveCue(int fromRow, int toRow)
    {
        if (fromRow == toRow ||
            fromRow < 0 || fromRow >= _model.Count ||
            toRow < 0 || toRow >= _model.Count)
            return;
        int end = _model.GroupExtent(fromRow);   // bloque [fromRow, end)
        if (toRow >= fromRow && toRow < end)
            return;   // destino dentro del propio bloque

        var targetCue = _model.GetCue(toRow);
        var all = _model.Cues.ToList();
        int blockLen = end - fromRow;
        var block = all.GetRange(fromRow, blockLen);
        all.RemoveRange(fromRow, blockLen);
        var moveIds = block.Select(c => c.Id).ToHashSet();

        int insertAt;
        int? newParent;
        if (targetCue is { Type: CueType.Group } targetGroup
            && !moveIds.Contains(targetGroup.Id))
        {
            // Soltar SOBRE la fila de un grupo = meterlo DENTRO, como 1er hijo
            insertAt = all.FindIndex(c => c.Id == targetGroup.Id) + 1;
            newParent = targetGroup.Id;
        }
        else
        {
            insertAt = Math.Clamp(
                toRow < fromRow ? toRow : toRow - blockLen + 1, 0, all.Count);
            // El cue movido adopta el grupo del cue que desplaza en el destino
            newParent = insertAt < all.Count ? all[insertAt].ParentId : null;
        }
        if (newParent is int np && moveIds.Contains(np))
            return;   // no puede quedar dentro de sí mismo

        _pushUndo();
        block[0].ParentId = newParent;

        all.InsertRange(insertAt, block);
        _model.ReplaceAll(all);
        _view.ReloadTable();
        _setPlayhead(_model.IndexOfId(block[0].Id));
    }

    // ==================================================================
    // GESTIÓN DE LA LISTA (añadir / importar / editar)
    // ==================================================================

    public void AddNewCue(CueType cueType = CueType.Audio)
    {
        _pushUndo();
        var cue = new Cue
        {
            Id = _model.NextId(),
            Type = cueType,
            Name = $"{Capitalize(cueType)} cue",
            Duration = cueType is CueType.Wait or CueType.Fade ? 3.0 : 0.0,
            Number = _model.NextNumber(),
        };
        _model.AddCue(cue);
        _view.AppendCueRow(cue);
        _setPlayhead(_model.Count - 1);
    }

    /// <summary>Inserta un cue nuevo del tipo dado justo debajo de la fila row.</summary>
    public void OnInsertCue(CueType cueType, int row)
    {
        _pushUndo();
        int insertAt = row >= 0 ? row + 1 : _model.Count;
        // Heredar el grupo del cue de referencia (mantiene contigüidad)
        var refCue = row >= 0 ? _model.GetCue(row) : null;
        var cue = new Cue
        {
            Id = _model.NextId(),
            Type = cueType,
            Name = $"{Capitalize(cueType)} cue",
            Duration = cueType is CueType.Wait or CueType.Fade ? 3.0 : 0.0,
            Number = _model.NextNumber(),
            ParentId = refCue?.ParentId,
        };
        _model.InsertCue(insertAt, cue);
        _view.ReloadTable();
        _setPlayhead(Math.Min(insertAt, _model.Count - 1));
    }

    /// <summary>Elimina el cue de la fila dada (deteniéndolo antes si sonaba).</summary>
    public void OnDeleteCue(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is null)
            return;
        _pushUndo();
        _stopIfRunning(cue.Id);
        _model.RemoveCue(row);
        _view.ReloadTable();
        int count = _model.Count;
        _setPlayhead(count > 0 ? Math.Min(row, count - 1) : 0);
    }

    /// <summary>Renumera todos los cues 1..n en orden de lista (con undo).</summary>
    public void RenumberCues()
    {
        if (_model.Count == 0)
            return;
        _pushUndo();
        for (int i = 0; i < _model.Count; i++)
            _model.Cues[i].Number = (i + 1).ToString();
        _view.ReloadTable();
        _view.FlashStatus($"Renumerados {_model.Count} cues (1..{_model.Count})");
    }

    private static string Capitalize(CueType t)
    {
        string s = t.ToString().ToUpperInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // Grupos: agrupar / desagrupar / colapsar
    // ------------------------------------------------------------------

    /// <summary>Crea un GROUP que contiene los cues seleccionados (y descendientes).</summary>
    public void OnGroupSelected(IReadOnlyList<int> rows)
    {
        var valid = rows.Where(r => r >= 0 && r < _model.Count).Distinct().OrderBy(r => r).ToList();
        if (valid.Count == 0)
            return;
        _pushUndo();
        var cues = _model.Cues;
        var selCues = valid.Select(r => cues[r]).ToList();

        // Conjunto a mover = seleccionados + todos sus descendientes
        var moveIds = new HashSet<int>();
        foreach (var c in selCues)
        {
            moveIds.Add(c.Id);
            foreach (var d in _model.Descendants(c.Id))
                moveIds.Add(d.Id);
        }
        var block = cues.Where(c => moveIds.Contains(c.Id)).ToList();   // conserva orden

        int gid = _model.NextId();
        var group = new Cue
        {
            Id = gid, Type = CueType.Group, Name = "Grupo", Duration = 0.0,
            Number = _model.NextNumber(),
            ParentId = selCues[0].ParentId, GroupMode = GroupMode.Enter,
        };
        // Solo los seleccionados de nivel superior cuelgan del nuevo grupo
        foreach (var c in selCues)
            c.ParentId = gid;

        int first = valid[0];
        var result = new List<Cue>();
        for (int i = 0; i < cues.Count; i++)
        {
            if (i == first)
            {
                result.Add(group);
                result.AddRange(block);
            }
            if (moveIds.Contains(cues[i].Id))
                continue;
            result.Add(cues[i]);
        }
        _model.ReplaceAll(result);
        _view.ReloadTable();
        int gi = _model.IndexOfId(gid);
        _setPlayhead(gi >= 0 ? gi : 0);
        _view.FlashStatus($"{selCues.Count} cue(s) agrupados");
    }

    /// <summary>Deshace un grupo: promueve sus hijos directos y elimina el GROUP.</summary>
    public void OnUngroup(int row)
    {
        var group = _model.GetCue(row);
        if (group is null || group.Type != CueType.Group)
            return;
        _pushUndo();
        foreach (var c in _model.DirectChildren(group.Id))
            c.ParentId = group.ParentId;
        _forgetGroupState(group.Id);
        _model.RemoveCue(row);
        _view.ReloadTable();
        int count = _model.Count;
        _setPlayhead(count > 0 ? Math.Min(row, count - 1) : 0);
        _view.FlashStatus("Grupo deshecho");
    }

    /// <summary>Contrae/expande un grupo (oculta/muestra sus descendientes).</summary>
    public void OnToggleCollapse(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is null || cue.Type != CueType.Group)
            return;
        cue.Collapsed = !cue.Collapsed;
        _view.RefreshRow(row);
        _view.ApplyGroupVisibility();
    }

    /// <summary>Crea cues a partir de archivos (drag-drop / importación).
    /// La duración real del archivo se lee con ffprobe al importar.</summary>
    public void AddCuesFromFiles(int targetRow, IReadOnlyList<string> paths)
    {
        _pushUndo();
        int insertAt = targetRow >= 0 ? targetRow : _model.Count;
        int created = 0;
        foreach (string path in paths)
        {
            var result = MediaImporter.ValidateFile(path);
            if (!result.Valid)
                continue;
            var ctype = result.MediaType == "VIDEO" ? CueType.Video : CueType.Audio;
            var cue = new Cue
            {
                Id = _model.NextId(),
                Type = ctype,
                Name = Path.GetFileNameWithoutExtension(path),
                Duration = MediaImporter.ProbeDurationSeconds(result.FilePath),
                Number = _model.NextNumber(),
                FilePath = result.FilePath,
            };
            _model.InsertCue(insertAt + created, cue);
            created++;
        }
        if (created > 0)
        {
            _view.ReloadTable();
            _setPlayhead(Math.Min(insertAt, _model.Count - 1));
            _view.FlashStatus($"{created} cue(s) creado(s) desde archivo.");
        }
    }
}
