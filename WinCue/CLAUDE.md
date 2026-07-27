# WinCue — instrucciones de proyecto

## Dónde está el código

El desarrollo activo es **WinCueSharp/** (C#/.NET 8), en el directorio hermano
`../WinCueSharp/`, NO en esta carpeta. Esta carpeta es la que se abre al iniciar
sesión, pero el código, el grafo y el `CLAUDE.md` específico del port viven allí.

Antes de tocar nada, lee también `../WinCueSharp/CLAUDE.md`.

## graphify

Hay un grafo de conocimiento del proyecto en `../WinCueSharp/graphify-out/`, con
god nodes, comunidades y relaciones entre ficheros.

Reglas:

- **Para cualquier pregunta sobre el código, consulta el grafo ANTES de leer
  ficheros sueltos o hacer grep.** Desde `../WinCueSharp/`:
  - `graphify query "<pregunta>"` — devuelve un subgrafo acotado, casi siempre
    mucho más pequeño que el informe entero o que la salida de un grep.
  - `graphify path "<A>" "<B>"` — relación entre dos conceptos.
  - `graphify explain "<concepto>"` — explicación de un nodo concreto.
- Lee `graphify-out/GRAPH_REPORT.md` solo para una revisión amplia de
  arquitectura, o cuando query/path/explain no den contexto suficiente.
- **Después de modificar código, ejecuta `graphify update .` desde
  `../WinCueSharp/`** para mantener el grafo al día. Es extracción AST: no usa
  LLM y no cuesta tokens.

### Ojo con el directorio

El grafo está en `../WinCueSharp/graphify-out/`, no aquí. Los comandos de
graphify hay que lanzarlos **desde `../WinCueSharp/`**; ejecutados desde esta
carpeta no encuentran `graph.json` y se pondrían a construir un grafo nuevo y
vacío de un directorio que casi no tiene código.

### Qué NO está en el grafo

La extracción es AST: recoge la ESTRUCTURA (tipos, miembros, llamadas), no la
intención. El porqué de las decisiones —y en este proyecto hay varias que no se
deducen del código— está en los mensajes de commit y en los comentarios. Si algo
parece arbitrario, mira `git log` antes de "arreglarlo".
