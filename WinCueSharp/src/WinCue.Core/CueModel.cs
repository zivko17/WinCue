// =============================================================================
// CueModel.cs — Capa de Datos Pura (port de cue_model.py::CueModel)
// =============================================================================
// Colección ordenada de Cue. Sin UI, sin dependencias de framework gráfico.
// El controlador es el único que debe llamar a los métodos de escritura;
// la vista NUNCA toca este modelo directamente.
//
// Hilos permitidos: SOLO el hilo principal (UI thread).
// =============================================================================

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinCue.Core;

public class CueModel
{
    private readonly List<Cue> _cues = new();

    /// <summary>Opciones JSON compartidas: indentado 2 espacios, UTF-8 sin escapar
    /// (equivale a json.dumps(indent=2, ensure_ascii=False) de Python).</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------
    // Acceso de lectura
    // ------------------------------------------------------------------

    /// <summary>Lista de cues en orden de ejecución. Solo lectura desde la vista.</summary>
    public IReadOnlyList<Cue> Cues => _cues;

    public int Count => _cues.Count;

    /// <summary>Devuelve el Cue en la posición dada o null si está fuera de rango.</summary>
    public Cue? GetCue(int index) =>
        index >= 0 && index < _cues.Count ? _cues[index] : null;

    /// <summary>Devuelve el Cue con el id dado, o null si no existe.</summary>
    public Cue? GetById(int cueId) => _cues.Find(c => c.Id == cueId);

    /// <summary>Devuelve el índice de fila del cue con el id dado, o -1.</summary>
    public int IndexOfId(int cueId) => _cues.FindIndex(c => c.Id == cueId);

    /// <summary>Devuelve un id nuevo, único, mayor que cualquiera existente.</summary>
    public int NextId() => _cues.Count == 0 ? 1 : _cues.Max(c => c.Id) + 1;

    /// <summary>Sugiere el siguiente número de cue como string incremental simple.</summary>
    public string NextNumber()
    {
        int maxNum = 0;
        foreach (var c in _cues)
        {
            string token = string.IsNullOrEmpty(c.Number) ? c.Id.ToString() : c.Number;
            if (double.TryParse(token, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double val))
                maxNum = Math.Max(maxNum, (int)val);
        }
        return (maxNum + 1).ToString();
    }

    // ------------------------------------------------------------------
    // Jerarquía de grupos
    // ------------------------------------------------------------------

    /// <summary>Profundidad de anidamiento del cue (0 = raíz).</summary>
    public int Depth(Cue cue)
    {
        int d = 0;
        int? pid = cue.ParentId;
        int guard = 0;
        while (pid is not null && guard < 100)
        {
            var parent = GetById(pid.Value);
            if (parent is null) break;
            d++;
            pid = parent.ParentId;
            guard++;
        }
        return d;
    }

    /// <summary>True si <paramref name="cue"/> está dentro del grupo (a cualquier nivel).</summary>
    public bool IsDescendant(Cue cue, int groupId)
    {
        int? pid = cue.ParentId;
        int guard = 0;
        while (pid is not null && guard < 100)
        {
            if (pid == groupId) return true;
            var parent = GetById(pid.Value);
            if (parent is null) break;
            pid = parent.ParentId;
            guard++;
        }
        return false;
    }

    /// <summary>Hijos directos del grupo, en orden de lista.</summary>
    public List<Cue> DirectChildren(int groupId) =>
        _cues.Where(c => c.ParentId == groupId).ToList();

    /// <summary>Índices de fila de los hijos directos del grupo, en orden.</summary>
    public List<int> DirectChildIndices(int groupId)
    {
        var result = new List<int>();
        for (int i = 0; i < _cues.Count; i++)
            if (_cues[i].ParentId == groupId)
                result.Add(i);
        return result;
    }

    /// <summary>Todos los descendientes (cualquier nivel), en orden de lista.</summary>
    public List<Cue> Descendants(int groupId) =>
        _cues.Where(c => IsDescendant(c, groupId)).ToList();

    /// <summary>
    /// Índice de la primera fila DESPUÉS del grupo (tras todos sus descendientes).
    /// Asume contigüidad: los descendientes van inmediatamente tras el grupo.
    /// </summary>
    public int GroupExtent(int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= _cues.Count)
            return _cues.Count;
        var group = _cues[groupIndex];
        int end = groupIndex + 1;
        for (int i = groupIndex + 1; i < _cues.Count; i++)
        {
            if (IsDescendant(_cues[i], group.Id))
                end = i + 1;
            else
                break;
        }
        return end;
    }

    /// <summary>True si el cue tiene ALGÚN grupo por encima en la cadena de
    /// padres. Con la regla de "grupos sin anidar" esto solo puede ser cierto
    /// para cues normales: ningún GROUP debe cumplirlo.</summary>
    public static bool HasGroupAncestor(Cue cue, Func<int, Cue?> byId)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(byId);
        int? pid = cue.ParentId;
        int guard = 0;
        while (pid is not null && guard++ < 100)
        {
            var parent = byId(pid.Value);
            if (parent is null) return false;
            if (parent.Type == CueType.Group) return true;
            pid = parent.ParentId;
        }
        return false;
    }

    /// <summary>True si el cue está dentro de algún grupo (atajo sobre el modelo).</summary>
    public bool IsInsideGroup(Cue cue) => HasGroupAncestor(cue, GetById);

    /// <summary>True si algún grupo ancestro del cue está colapsado (debe ocultarse).</summary>
    public bool IsAncestorCollapsed(Cue cue)
    {
        int? pid = cue.ParentId;
        int guard = 0;
        while (pid is not null && guard < 100)
        {
            var parent = GetById(pid.Value);
            if (parent is null) break;
            if (parent.Type == CueType.Group && parent.Collapsed)
                return true;
            pid = parent.ParentId;
            guard++;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Mutación (solo el controlador llama a estos métodos)
    // ------------------------------------------------------------------

    public void AddCue(Cue cue) => _cues.Add(cue);

    /// <summary>Inserta un cue en la posición dada (clamp a los límites).</summary>
    public void InsertCue(int row, Cue cue)
    {
        row = Math.Clamp(row, 0, _cues.Count);
        _cues.Insert(row, cue);
    }

    /// <summary>Actualiza el estado de un cue.</summary>
    public void SetState(int index, CueState state)
    {
        if (index >= 0 && index < _cues.Count)
            _cues[index].State = state;
    }

    /// <summary>Registra la latencia medida de un cue.</summary>
    public void SetLatency(int index, double latencyMs)
    {
        if (index >= 0 && index < _cues.Count)
            _cues[index].LatencyMs = latencyMs;
    }

    /// <summary>Elimina el cue en la posición dada. No-op si el índice está fuera de rango.</summary>
    public void RemoveCue(int row)
    {
        if (row >= 0 && row < _cues.Count)
            _cues.RemoveAt(row);
    }

    public void UpdateCue(int row, Cue cue)
    {
        if (row >= 0 && row < _cues.Count)
            _cues[row] = cue;
    }

    /// <summary>Mueve el cue de fromRow a toRow reordenando la lista.</summary>
    public void MoveCue(int fromRow, int toRow)
    {
        int count = _cues.Count;
        if (fromRow == toRow ||
            fromRow < 0 || fromRow >= count ||
            toRow < 0 || toRow >= count)
            return;
        var cue = _cues[fromRow];
        _cues.RemoveAt(fromRow);
        _cues.Insert(toRow, cue);
    }

    /// <summary>
    /// Reemplaza toda la lista en la instancia existente.
    /// Las referencias externas al objeto CueModel siguen siendo válidas.
    /// Llamar SOLO desde el controlador, nunca desde la vista.
    /// </summary>
    public void ReplaceAll(IEnumerable<Cue> cues)
    {
        _cues.Clear();
        _cues.AddRange(cues);
    }

    // ------------------------------------------------------------------
    // Serialización JSON (compatible byte-a-byte en claves con Python)
    // ------------------------------------------------------------------

    /// <summary>Serializa la lista de cues a JSON con sangría legible.</summary>
    public string ToJson()
    {
        var payload = new ShowFilePayload { Version = 1, Cues = _cues };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// Deserializa JSON y devuelve la lista de cues validados.
    /// Lanza FormatException con mensaje descriptivo si el formato es inválido,
    /// hay IDs duplicados o algún campo tiene un valor fuera de rango.
    /// Soporta tanto lista directa como wrapper {"version":1, "cues":[...]}.
    /// Retrocompatible: los shows antiguos solo tienen id/type/name/duration/
    /// file_path; el resto de campos toman sus valores por defecto.
    /// </summary>
    public static List<Cue> FromJson(string text) => FromJson(text, out _);

    /// <inheritdoc cref="FromJson(string)"/>
    /// <param name="text">Contenido del fichero de show.</param>
    /// <param name="flattenedGroups">Cuántos grupos ANIDADOS se degradaron al
    /// cargar (ver <see cref="FlattenNestedGroups"/>). 0 en un show normal.</param>
    public static List<Cue> FromJson(string text, out int flattenedGroups)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException exc)
        {
            throw new FormatException($"JSON inválido: {exc.Message}", exc);
        }

        JsonArray rawList = root switch
        {
            JsonArray arr => arr,
            JsonObject obj when obj["cues"] is JsonArray arr => arr,
            _ => throw new FormatException("El archivo JSON debe contener una lista de cues."),
        };

        var seenIds = new HashSet<int>();
        var cues = new List<Cue>();
        for (int i = 0; i < rawList.Count; i++)
        {
            Cue c;
            try
            {
                c = rawList[i].Deserialize<Cue>(JsonOptions)
                    ?? throw new FormatException("cue nulo");
                c.State = CueState.Idle;   // siempre IDLE al cargar
                c.LatencyMs = null;        // se medirá en la primera reproducción
                c.Normalize();
            }
            catch (Exception exc) when (exc is JsonException or ArgumentException or FormatException)
            {
                throw new FormatException($"Cue #{i + 1}: {exc.Message}", exc);
            }
            if (!seenIds.Add(c.Id))
                throw new FormatException($"ID duplicado en el archivo: {c.Id}");
            cues.Add(c);
        }
        flattenedGroups = FlattenNestedGroups(cues);
        return cues;
    }

    /// <summary>
    /// Aplana los GRUPOS ANIDADOS de una lista recién cargada: un grupo dentro
    /// de otro no tiene sentido en una lista de cues y ya no se puede crear,
    /// pero los shows guardados con versiones anteriores (o por la versión
    /// Python) pueden traerlos. Cargar NUNCA debe fallar por eso.
    ///
    /// QUÉ SE HACE Y POR QUÉ: el grupo interior se DEGRADA A MEMO y sus hijos
    /// pasan a colgar del grupo exterior. Se eligió así frente a las
    /// alternativas porque es lo único que no pierde nada ni mueve nada:
    ///   · borrar el grupo interior perdería su nombre, número, color y notas;
    ///   · sacarlo al nivel raíz obligaría a reordenar la lista (su bloque
    ///     tendría que saltar por encima de los hermanos que quedan detrás),
    ///     y en una lista de cues el ORDEN es el show;
    ///   · tolerarlos dejaría el "Grupo &gt; Grupo" en pantalla para siempre.
    /// Un MEMO es exactamente lo que queda de una cabecera de grupo sin acción:
    /// una línea de comentario, en su sitio, con su texto y su color.
    ///
    /// Compatible con Python: no se inventa ningún campo (solo cambian "type"
    /// y "parent_id", que ya existían).
    /// </summary>
    /// <returns>Cuántos grupos se degradaron (0 = el show ya estaba plano).</returns>
    public static int FlattenNestedGroups(IList<Cue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var byId = new Dictionary<int, Cue>();
        foreach (var c in cues)
            byId[c.Id] = c;
        Cue? Lookup(int id) => byId.TryGetValue(id, out var c) ? c : null;

        int flattened = 0;
        // Cada vuelta degrada UN grupo anidado; con 3 niveles hacen falta 2
        // vueltas. El techo (una por cue) corta cualquier ciclo del fichero.
        for (int guard = 0; guard <= cues.Count; guard++)
        {
            var nested = cues.FirstOrDefault(
                c => c.Type == CueType.Group && HasGroupAncestor(c, Lookup));
            if (nested is null)
                break;
            foreach (var child in cues.Where(c => c.ParentId == nested.Id).ToList())
                child.ParentId = nested.ParentId;
            nested.Type = CueType.Memo;
            flattened++;
        }
        return flattened;
    }

    /// <summary>Wrapper de fichero de show: {"version": 1, "cues": [...]}.</summary>
    private sealed class ShowFilePayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [System.Text.Json.Serialization.JsonPropertyName("cues")]
        public IReadOnlyList<Cue> Cues { get; set; } = Array.Empty<Cue>();
    }
}
