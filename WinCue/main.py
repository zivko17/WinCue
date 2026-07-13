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
    app.setApplicationName("WinCue")
    app.setApplicationVersion("0.2")

    # ----------------------------------------------------------------
    # 2. Construir el Modelo con datos de ejemplo
    # ----------------------------------------------------------------
    model = CueModel()
    # Arranca en blanco (estilo QLab): la lista se construye arrastrando
    # archivos o con el botón ＋ CUE. (Antes cargaba create_sample_data().)

    # ----------------------------------------------------------------
    # 3. Construir la Vista (requiere el modelo para la tabla inicial)
    # ----------------------------------------------------------------
    window = MainWindow(model)

    # ----------------------------------------------------------------
    # 4. Construir el Controlador y conectar señales → slots
    # ----------------------------------------------------------------
    controller = MainController(model, window)

    # Transporte (modelo QLab: GO dispara + avanza sin cortar; Stop/Panic/Pause)
    window.go_pressed.connect(controller.on_go_pressed)
    window.stop_pressed.connect(controller.on_stop_pressed)
    window.panic_pressed.connect(controller.on_panic)
    window.pause_pressed.connect(controller.on_pause_toggle)

    # Guardar/cargar show y edición de cues
    window.save_requested.connect(controller.on_save_requested)
    window.load_requested.connect(controller.on_load_requested)
    window.cue_edit_committed.connect(controller.on_cue_edit_committed)

    # Navegación del playhead (standby)
    window.prev_pressed.connect(controller.on_prev_pressed)
    window.next_pressed.connect(controller.on_next_pressed)
    window.jump_to_cue.connect(controller.on_jump_to_cue)
    window.playhead_set.connect(controller.on_set_playhead)
    window.stop_cue.connect(controller.on_stop_selected)
    window.cue_insert_requested.connect(controller.on_insert_cue)

    # Grupos (estilo QLab)
    window.group_requested.connect(controller.on_group_selected)
    window.ungroup_requested.connect(controller.on_ungroup)
    window.collapse_toggled.connect(controller.on_toggle_collapse)

    # Hotkeys por cue (Triggers): disparan el cue esté o no en standby
    window.hotkey_triggered.connect(controller.trigger_cue_id)

    # Volumen master (top bar): aplica a audio y vídeo
    window.master_volume_changed.connect(controller.on_master_volume)
    from settings_manager import SettingsManager as _SM
    window._master_slider.setValue(int(_SM.instance().get("master_volume", 100)))

    # Cierre limpio: detener motores y timers al salir
    app.aboutToQuit.connect(controller.shutdown)

    # ----------------------------------------------------------------
    # 5. Mostrar (maximizada) y lanzar el event loop
    # ----------------------------------------------------------------
    # Arrancar maximizada: da sitio a la lista + inspector + preview y evita
    # que los paneles queden apretados en una ventana pequeña.
    window.showMaximized()
    # Activar la salida de vídeo (blackout en el monitor elegido) si procede
    window.refresh_video_output()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
