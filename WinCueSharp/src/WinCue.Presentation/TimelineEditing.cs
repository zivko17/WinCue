// =============================================================================
// TimelineEditing.cs — qué escribe en el modelo un arrastre al soltarse.
// =============================================================================
// Segunda pasada del traslado del timeline: la POLÍTICA del soltar deja de
// ser exclusiva de WinCue.App/TimelinePanel.cs. Es la parte con más criterio
// del timeline — y por eso la más peligrosa de tener por duplicado:
//   · mover más allá del vecino = reordenar la lista (TryReorder)
//   · soltar montado sobre el anterior = encadenar en PARALELO
//     (LinkOverlapWithPrev: AUTO_CONTINUE + pre_wait compensado)
//   · los bloques NO arrastrados se quedan donde estaban (AnchorOthers:
//     el modelo es relativo, el timeline se edita en absoluto)
//   · solape en la misma capa = crossfade; separar = quitarlo
//     (ReconcileSameLayerFades, solo con el interruptor 🔀 puesto)
// Todo traducido LITERALMENTE de OnMouseUp y sus ayudantes; los números
// (umbral 0,05 s, ancla 0,02 s, redondeo a 3 decimales…) son los de hoy.
//
// No toca el modelo directamente: escribe a través de los MISMOS delegados
// que ya usaba el panel — commit(cueId, campo, valor), que cada producto
// cablea a MainController.OnCueEditCommitted (edición en vivo + undo +
// asterisco de modificado), y moveCue(fila, fila) → OnMoveCue. Los commits
// son SÍNCRONOS: el flujo reproyecta el modelo entre pasos y cuenta con ver
// cada cambio ya aplicado (igual que hacía RebuildLayout en el panel).
// =============================================================================

using WinCue.Core;

namespace WinCue.Presentation;

/// <summary>Convierte un TimelineDragGesture soltado en commits sobre el
/// modelo. Compartido por los dos productos: mismo gesto, mismo resultado.</summary>
public static class TimelineEditing
{
    /// <summary>El soltar completo, en el orden de siempre: reordenar si se
    /// cruzó al vecino; si no, encadenar/comprometer pre_wait y pista; luego
    /// anclar a los demás; y por último reconciliar los crossfades de la
    /// misma capa (si <paramref name="autoCrossfade"/>). Sin gesto real
    /// (Moved false) no escribe nada: eso fue un clic.</summary>
    public static void CommitDrag(CueModel model, TimelineDragGesture g,
        Action<int, string, object?> commit, Action<int, int> moveCue, bool autoCrossfade)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(g);
        if (!g.Moved)
            return;

        // Proyección LIMPIA (sin el arrastre): base para detectar cruces
        var proj = TimelineLayout.Project(model);
        var cue = g.Cue;

        bool reordered = false;
        switch (g.Mode)
        {
            case TimelineDragMode.Move:
                reordered = TryReorder(model, proj, cue, g.PreWait - g.OrigPreWait, moveCue);
                if (!reordered)
                {
                    bool linked = false;
                    double committed = g.PreWait < -1e-9
                        ? LinkOverlapWithPrev(model, proj, g, commit, out linked)
                        : g.PreWait;
                    if (Math.Abs(committed - g.OrigPreWait) > 1e-9)
                        commit(cue.Id, "pre_wait", Math.Max(0, committed));
                    // Pista: SIEMPRE manda el gesto vertical del usuario.
                    // Sin gesto, un solape recién creado congela el bloque
                    // en la fila donde se veía (la fila "auto" bailaría).
                    if (!g.IsGroup)
                    {
                        if (g.LayerTouched && g.Layer != cue.TimelineLayer)
                            commit(cue.Id, "timeline_layer", g.Layer);
                        else if (linked && cue.TimelineLayer < 0 && g.Layer >= 0)
                            commit(cue.Id, "timeline_layer", g.Layer);
                    }
                }
                break;
            case TimelineDragMode.ResizeL:
                if (Math.Abs(g.PreWait - g.OrigPreWait) > 1e-9)
                    commit(cue.Id, "pre_wait", g.PreWait);
                if (Math.Abs(g.TrimStart - cue.TrimStart) > 1e-9)
                    commit(cue.Id, "trim_start", g.TrimStart);
                break;
            case TimelineDragMode.ResizeR:
                if (cue.IsMedia())
                    commit(cue.Id, "trim_end", g.TrimEnd);
                else
                    commit(cue.Id, "duration", g.Duration);
                break;
        }

        // Los DEMÁS bloques se quedan donde estaban: mover/redimensionar
        // uno no arrastra al resto del show (sus pre_wait se compensan)
        if (!reordered)
            AnchorOthers(model, cue, g.Anchors, commit);

        // Fades gestionados POR CAPA: solape en la misma capa = crossfade
        // (audio); separar = quitarlo. Entre capas, nada automático.
        if (!g.IsGroup)
            ReconcileSameLayerFades(model, autoCrossfade, commit);
    }

    /// <summary>Si el bloque (o grupo) se soltó más allá de su hermano
    /// anterior/siguiente, reordena la lista y devuelve true. Es el "viceversa"
    /// de la correlación lista↔timeline. Compara contra la proyección SIN el
    /// arrastre: en una cadena secuencial los vecinos posteriores se desplazan
    /// junto al bloque y nunca se cruzarían.</summary>
    private static bool TryReorder(CueModel model, TimelineProjection proj,
        Cue cue, double delta, Action<int, int> moveCue)
    {
        if (Math.Abs(delta) < 0.05)
            return false;

        var siblings = model.Cues.Where(c => c.ParentId == cue.ParentId).ToList();
        int pos = siblings.FindIndex(c => c.Id == cue.Id);
        if (pos < 0 || !proj.StartById.TryGetValue(cue.Id, out double myStart))
            return false;
        double droppedStart = Math.Max(0, myStart + delta);

        if (delta < 0 && pos > 0)
        {
            var prev = siblings[pos - 1];
            if (proj.StartById.TryGetValue(prev.Id, out double prevStart)
                && droppedStart < prevStart)
            {
                moveCue(model.IndexOfId(cue.Id), model.IndexOfId(prev.Id));
                return true;
            }
        }
        else if (delta > 0 && pos < siblings.Count - 1)
        {
            var next = siblings[pos + 1];
            if (proj.StartById.TryGetValue(next.Id, out double nextStart)
                && droppedStart > nextStart)
            {
                // Aterrizar DESPUÉS del bloque completo del vecino (grupos incl.)
                int nextRow = model.IndexOfId(next.Id);
                int toRow = Math.Min(model.Count - 1,
                    model.GroupExtent(nextRow) - 1);
                moveCue(model.IndexOfId(cue.Id), toRow);
                return true;
            }
        }
        return false;
    }

    /// <summary>El bloque se soltó ANTES de su punto de disparo (montado sobre
    /// el cue anterior): encadenarlos en PARALELO — el anterior pasa a
    /// AUTO_CONTINUE y este cue recibe el pre_wait que lo deja exactamente
    /// donde se soltó. Es lo que hace que "las capas se junten" en el timeline.
    /// Dentro de un grupo TIMELINE los hijos ya son paralelos: ahí solo se
    /// recorta el offset a 0 (encadenar duplicaría disparos).</summary>
    private static double LinkOverlapWithPrev(CueModel model, TimelineProjection proj,
        TimelineDragGesture g, Action<int, string, object?> commit, out bool linked)
    {
        linked = false;
        var cue = g.Cue;

        // Hijo de grupo TIMELINE: el paralelismo ya existe vía pre_wait
        if (cue.ParentId is int pid &&
            model.GetById(pid) is { Type: CueType.Group, GroupMode: GroupMode.Timeline })
            return Math.Max(0, g.PreWait);

        var siblings = model.Cues.Where(c => c.ParentId == cue.ParentId).ToList();
        int pos = siblings.FindIndex(c => c.Id == cue.Id);
        if (pos <= 0
            || !proj.StartById.TryGetValue(cue.Id, out double cleanStart)
            || !proj.StartById.TryGetValue(siblings[pos - 1].Id, out double prevStart))
            return 0;

        var prev = siblings[pos - 1];
        double desired = cleanStart + (g.PreWait - g.OrigPreWait);
        double newTrigger = prevStart + prev.PostWait;   // disparo con AUTO_CONTINUE

        if (prev.ContinueMode != ContinueMode.AutoContinue)
            commit(prev.Id, "continue_mode", "AUTO_CONTINUE");
        linked = true;

        // La fila del bloque NUNCA se recoloca sola: la del arrastrado la
        // decide el gesto del usuario (se fija fuera) y la del anterior solo
        // se CONGELA donde se ve si estaba en "auto" (para que el apilado no
        // la baile después). Misma pista = sustitución a corte, por diseño.
        if (cue.IsMedia() && prev.IsMedia() && prev.TimelineLayer < 0)
        {
            int prevShown = proj.Blocks.FirstOrDefault(
                b => !b.Ghost && b.Cue.Id == prev.Id)?.Layer ?? 2;
            commit(prev.Id, "timeline_layer", prevShown);
        }

        return Math.Max(0, desired - newTrigger);
    }

    /// <summary>Tras un arrastre, recoloca los bloques NO arrastrados en el
    /// tiempo donde estaban antes (ancla): el modelo de datos es relativo
    /// (cadenas de pre_wait), pero el timeline se edita en absoluto — mover
    /// un bloque no debe desplazar a los demás. Si un pre_wait tendría que
    /// ser negativo (el bloque anterior creció por encima), se queda en 0:
    /// ese desplazamiento es físicamente inevitable.</summary>
    private static void AnchorOthers(CueModel model, Cue dragged,
        IReadOnlyDictionary<int, double> anchors, Action<int, string, object?> commit)
    {
        var starts = TimelineLayout.Project(model).StartById;
        foreach (var c in model.Cues)   // orden de lista: aguas arriba primero
        {
            if (c.Id == dragged.Id
                || !anchors.TryGetValue(c.Id, out double want)
                || model.IsDescendant(c, dragged.Id)
                || !starts.TryGetValue(c.Id, out double now))
                continue;
            double delta = want - now;
            if (Math.Abs(delta) < 0.02)
                continue;
            double npw = Math.Max(0.0, c.PreWait + delta);
            if (Math.Abs(npw - c.PreWait) < 1e-9)
                continue;
            commit(c.Id, "pre_wait", Math.Round(npw, 3));
            starts = TimelineLayout.Project(model).StartById;   // el ajuste restaura también a sus dependientes
        }
    }

    /// <summary>Reconciliación de fades POR CAPA tras un arrastre (🔀 ON):
    /// entre cada par de medias CONSECUTIVAS DE LA MISMA CAPA, el solape se
    /// convierte en crossfade (audio mezcla; vídeo funde por opacidad) y al
    /// separarse el crossfade se retira. Las CAPAS DISTINTAS son
    /// independientes: nunca se escriben fades entre ellas — para fundir
    /// entre capas está el menú contextual "Crossfade con…". Los fades de
    /// bloques sin vecino en su capa (primero/último/aislados) NO se tocan
    /// (manuales del usuario).</summary>
    private static void ReconcileSameLayerFades(CueModel model, bool autoCrossfade,
        Action<int, string, object?> commit)
    {
        if (!autoCrossfade)
            return;
        // Proyección con los commits del arrastre aplicados
        var blocks = TimelineLayout.Project(model).Blocks;

        foreach (var layerGroup in blocks
                     .Where(b => b.Cue.IsMedia() && b.Dur > 0 && !b.Ghost)
                     .GroupBy(b => b.Layer))
        {
            var ordered = layerGroup.OrderBy(b => b.Start).ToList();
            for (int i = 0; i + 1 < ordered.Count; i++)
            {
                var a = ordered[i];
                var b = ordered[i + 1];
                double overlap = a.Start + a.Dur - b.Start;
                double xf = overlap > 0.05
                    ? Math.Round(Math.Min(overlap,
                        Math.Min(a.Dur, Math.Max(0.1, b.Dur))), 1)
                    : 0.0;
                if (Math.Abs(a.Cue.FadeOut - xf) > 0.01)
                    commit(a.Cue.Id, "fade_out", xf);
                if (Math.Abs(b.Cue.FadeIn - xf) > 0.01)
                    commit(b.Cue.Id, "fade_in", xf);
            }
        }
    }
}
