# =============================================================================
# main.py — Punto de Entrada de la Aplicación
# =============================================================================
# Responsabilidad: Instanciar QApplication, construir el grafo de objetos
#   (modelo → vista → controlador) en el orden correcto, conectar señales
#   y lanzar el event loop.
#
# Hilos permitidos: SOLO el hilo principal (main thread).
#   Todo lo que ocurre en este archivo es inicialización síncrona antes de
#   que el event loop de Qt arranque. Una vez que app.exec() se llama, el
#   control pasa íntegramente al event loop de Qt.
# =============================================================================

# ---------------------------------------------------------------------------
# from __future__ imports — exigido por el lenguaje, antes que nada más
# ---------------------------------------------------------------------------
from __future__ import annotations

# ---------------------------------------------------------------------------
# Bloque de PATH para libmpv-2.dll — debe ir ANTES de cualquier import que
# pueda arrastrar python-mpv de forma transitiva.
#
# · PATH: python-mpv usa ctypes.util.find_library() que todavía honra PATH
#   en Windows incluso en Python ≥ 3.8.
# · add_dll_directory: la API moderna de Windows (desde Python 3.8) para
#   directorios de búsqueda de DLL. Cubre dependencias transitivas de
#   libmpv-2.dll (decoders, etc.) cargadas via LoadLibraryEx con flags.
# ---------------------------------------------------------------------------
import os as _os

_HERE = _os.path.dirname(_os.path.abspath(__file__))
_os.environ["PATH"] = _HERE + _os.pathsep + _os.environ.get("PATH", "")

if hasattr(_os, "add_dll_directory") and _os.path.isdir(_HERE):
    try:
        _os.add_dll_directory(_HERE)
    except (OSError, FileNotFoundError):
        pass

# ---------------------------------------------------------------------------
# A partir de aquí los imports normales
# ---------------------------------------------------------------------------

import sys

from PySide6.QtWidgets import QApplication

from cue_controller import MainController
from cue_model import CueModel
from main_window import MainWindow
from video_engine import MPV_AVAILABLE as _MPV_OK

if not _MPV_OK:
    print(
        "[FATAL] MPV no disponible — la reproducción de vídeo NO funcionará.\n"
        "        Verifica que libmpv-2.dll está en la raíz del proyecto "
        f"({_HERE}).",
        flush=True,
    )


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName("CUE CONTROLLER")
    app.setApplicationVersion("0.1")

    # ----------------------------------------------------------------
    # 2. Construir el Modelo con datos de ejemplo
    # ----------------------------------------------------------------
    model = CueModel()
    for cue in CueModel.create_sample_data():
        model.add_cue(cue)

    # ----------------------------------------------------------------
    # 3. Construir la Vista (requiere el modelo para la tabla inicial)
    # ----------------------------------------------------------------
    window = MainWindow(model)

    # ----------------------------------------------------------------
    # 4. Construir el Controlador y conectar señales → slots
    # ----------------------------------------------------------------
    controller = MainController(model, window)

    # Señales de la vista conectadas a métodos del controlador
    window.go_pressed.connect(controller.on_go_pressed)
    window.stop_pressed.connect(controller.on_stop_pressed)
    window.stress_test_pressed.connect(controller.on_stress_test_pressed)

    # Paso 4 — guardar/cargar show y edición de cues
    window.save_requested.connect(controller.on_save_requested)
    window.load_requested.connect(controller.on_load_requested)
    window.cue_edit_committed.connect(controller.on_cue_edit_committed)

    # Paso 5 — navegación por teclado
    window.prev_pressed.connect(controller.on_prev_pressed)
    window.jump_to_cue.connect(controller.on_jump_to_cue)

    # ----------------------------------------------------------------
    # 5. Mostrar y lanzar el event loop
    # ----------------------------------------------------------------
    window.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
