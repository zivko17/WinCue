# =============================================================================
# main_window.py — Vista Principal (V en MVC)
# =============================================================================
# Responsabilidad: Construir y gestionar todos los widgets de la interfaz.
#   La vista NO contiene lógica de negocio. No modifica CueModel. Solo:
#     · Expone señales Qt que el controlador conecta a sus slots.
#     · Ofrece métodos públicos para que el controlador actualice el display.
#
# LAYOUT:
#   ┌─ top_bar (36 px) ─ [CUE LIST] ──────── [SAVE] [LOAD] [STRESS TEST] ──┐
#   ├─ QSplitter (handleWidth=0) ────────────────────────────────────────────┤
#   │ ┌─ izq (≥340 px, 35 %) ───────┐  ┌─ der (65 %) ──────────────────────┐│
#   │ │  VideoContainer  320×180     │  │  QTableView (todo el alto)        ││
#   │ │  VU meter        320×14      │  │  · doble clic → editar cue        ││
#   │ │  Countdown       320×36 bold │  │  · drag & drop archivos           ││
#   │ │  [▶  GO  ]       320×52      │  │                                   ││
#   │ │  [■  STOP]       320×34      │  │                                   ││
#   │ │  Cue activo: …               │  │                                   ││
#   │ │  NEXT → …                    │  │                                   ││
#   │ │  ░░░ fondo #141414 ░░░░░░░   │  │                                   ││
#   │ └─────────────────────────────┘  └───────────────────────────────────┘│
#   └────────────────────────────────────────────────────────────────────────┘
#
# ATAJOS DE TECLADO (configurables en keybindings.json):
#   Space → GO   |  S → STOP   |  ← → cue anterior
#   Ctrl+S → Guardar   |  Ctrl+O → Abrir
#   0–9  → acumular número + 800 ms timeout → saltar a cue #N (1-based index)
#
# Hilos permitidos: SOLO el hilo principal (main thread).
# =============================================================================

from __future__ import annotations

import json
from pathlib import Path
from typing import Optional

from PySide6.QtCore import QModelIndex, Qt, QTimer, Signal
from PySide6.QtGui import (
    QColor,
    QDragEnterEvent,
    QDragLeaveEvent,
    QDragMoveEvent,
    QDropEvent,
    QFont,
    QKeyEvent,
    QKeySequence,
    QShortcut,
    QStandardItem,
    QStandardItemModel,
)
from PySide6.QtWidgets import (
    QAbstractItemView,
    QFileDialog,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QMainWindow,
    QMenu,
    QMessageBox,
    QPushButton,
    QSizePolicy,
    QSplitter,
    QTableView,
    QVBoxLayout,
    QWidget,
)

from cue_edit_dialog import CueEditDialog
from cue_model import STATE_COLORS, Cue, CueModel
from media_import import ALL_FORMATS, MediaImporter
from settings_dialog import SettingsDialog
from video_engine import VideoContainer  # Paso 3
from vu_meter import VuMeter             # Paso 5


# ---------------------------------------------------------------------------
# Constantes de estilo
# ---------------------------------------------------------------------------

COLOR_BG           = "#1a1a1a"
COLOR_BG_ALT       = "#222222"
COLOR_ACCENT       = "#e8a200"
COLOR_ACCENT_TEXT  = "#000000"
COLOR_GO           = "#2ecc71"
COLOR_STOP         = "#e74c3c"
COLOR_STRESS       = "#444444"
COLOR_BORDER       = "#333333"
COLOR_TEXT         = "#d4d4d4"
COLOR_HEADER_BG    = "#111111"
COLOR_LEFT_BG      = "#141414"

FONT_MONO     = "Consolas"
FONT_MONO_ALT = "Courier New"

# Formatos reconocidos en drag & drop — fuente de verdad en media_import.py
_MEDIA_EXTS: frozenset[str] = ALL_FORMATS

# Estilos del countdown según zona (cambia solo al cruzar umbral, no cada tick)
_CD_STYLE = (
    "QLabel {{ color: {color}; font-size: 20pt; font-weight: bold; "
    "background-color: #0d0d0d; border-radius: 3px; "
    f'font-family: "{FONT_MONO}", monospace; }}}}'
)
_CD_IDLE_STYLE  = _CD_STYLE.format(color="#3a3a3a")
_CD_GREEN_STYLE = _CD_STYLE.format(color="#2ecc71")
_CD_AMBER_STYLE = _CD_STYLE.format(color="#e8a200")
_CD_RED_STYLE   = _CD_STYLE.format(color="#e74c3c")

# Teclas por defecto (sobrescritas por keybindings.json si existe)
_DEFAULT_KB: dict[str, str] = {
    "go":   "Space",
    "stop": "S",
    "prev": "Left",
    "save": "Ctrl+S",
    "load": "Ctrl+O",
}

# Anchos de columna (px)
COL_WIDTHS: dict[int, int] = {
    0: 50, 1: 80, 2: 260, 3: 100, 4: 110, 5: 120,
}
COL_HEADERS = ["#", "TIPO", "NOMBRE", "DURACIÓN", "ESTADO", "LATENCIA ms"]
COL_ID, COL_TYPE, COL_NAME, COL_DUR, COL_STATE, COL_LAT = range(6)


# ---------------------------------------------------------------------------
# CueTableView — subclase con drag & drop nativo de archivos de media
# ---------------------------------------------------------------------------

class CueTableView(QTableView):
    """QTableView con soporte nativo de drag & drop para archivos de media.

    Emite file_assigned(row, filepath) cuando un archivo se suelta sobre una
    fila válida. Toda la lógica de UI (feedback visual, confirmación) vive aquí;
    la validación de contenido se delega a MediaImporter.
    """

    file_assigned:  Signal = Signal(int, str)   # (row, absolute_filepath)
    status_message: Signal = Signal(str, int)   # (message, timeout_ms)
    cue_removed:    Signal = Signal(list)        # list[int] filas a borrar
    cue_moved:      Signal = Signal(int, int)    # (from_row, to_row)

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setAcceptDrops(True)
        self.setDragEnabled(True)
        # DragDrop permite tanto drag interno (reordenar) como drop externo
        self.setDragDropMode(QAbstractItemView.DragDropMode.DragDrop)
        self.setDefaultDropAction(Qt.DropAction.MoveAction)
        self.setDropIndicatorShown(True)

    # ── Teclado — Delete para borrar cues ────────────────────────────────

    def keyPressEvent(self, event) -> None:
        if event.key() == Qt.Key.Key_Delete:
            indexes = self.selectedIndexes()
            if not indexes:
                super().keyPressEvent(event)
                return
            rows = sorted(set(i.row() for i in indexes), reverse=True)
            reply = QMessageBox.question(
                self,
                "Borrar cues",
                f"¿Borrar {len(rows)} cue(s)? Esta acción no se puede deshacer.",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            )
            if reply == QMessageBox.StandardButton.Yes:
                self.cue_removed.emit(rows)
        else:
            super().keyPressEvent(event)

    # ── Eventos de arrastre ───────────────────────────────────────────────

    def dragEnterEvent(self, event: QDragEnterEvent) -> None:
        if event.source() is self:
            # Drag interno: reordenar filas
            event.acceptProposedAction()
        elif self._has_valid_urls(event.mimeData()):
            self._set_drop_bg("#1a2a1a")
            event.acceptProposedAction()
        else:
            event.ignore()

    def dragMoveEvent(self, event: QDragMoveEvent) -> None:
        if event.source() is self:
            event.acceptProposedAction()
        elif self._has_valid_urls(event.mimeData()):
            event.acceptProposedAction()
        else:
            event.ignore()

    def dragLeaveEvent(self, event: QDragLeaveEvent) -> None:
        self._set_drop_bg(COLOR_BG)
        event.accept()

    def dropEvent(self, event: QDropEvent) -> None:
        if event.source() is self:
            self._handle_internal_drop(event)
        else:
            self._handle_external_drop(event)

    def _handle_internal_drop(self, event: QDropEvent) -> None:
        """Reordenado de filas: drag desde la misma tabla."""
        source_row = self.currentIndex().row()
        target_idx = self.indexAt(event.position().toPoint())
        target_row = (
            target_idx.row() if target_idx.isValid()
            else (self.model().rowCount() - 1 if self.model() else 0)
        )
        if source_row < 0 or source_row == target_row:
            event.ignore()
            return
        self.cue_moved.emit(source_row, target_row)
        event.acceptProposedAction()

    def _handle_external_drop(self, event: QDropEvent) -> None:
        """Asignación de archivos desde el explorador."""
        self._set_drop_bg(COLOR_BG)
        mime = event.mimeData()
        if not mime.hasUrls():
            event.ignore()
            return

        # Validar todos los archivos arrastrados
        valid_paths: list[str] = []
        for url in mime.urls():
            if url.isLocalFile():
                result = MediaImporter.validate_file(url.toLocalFile())
                if result["valid"]:
                    valid_paths.append(result["filepath"])

        if not valid_paths:
            event.ignore()
            return

        # Determinar fila objetivo
        idx = self.indexAt(event.position().toPoint())
        target_row = idx.row() if idx.isValid() else -1

        if target_row == -1:
            target_row = self._first_free_row()
            if target_row == -1:
                QMessageBox.warning(
                    self,
                    "Sin cues disponibles",
                    "No hay cues sin archivo asignado.\n"
                    "Arrastre el archivo directamente sobre una fila para reemplazarlo.",
                )
                event.ignore()
                return

        total_rows = self.model().rowCount() if self.model() else 0
        rows_affected: list[int] = []

        for i, path in enumerate(valid_paths):
            row = target_row + i
            if row >= total_rows:
                break

            if len(valid_paths) == 1:
                existing = self.model().item(row, COL_NAME)
                has_existing = bool(
                    existing and existing.data(Qt.ItemDataRole.UserRole)
                )
                if has_existing:
                    cue_id_item = self.model().item(row, COL_ID)
                    cue_id = cue_id_item.text() if cue_id_item else str(row + 1)
                    reply = QMessageBox.question(
                        self,
                        "Reemplazar archivo",
                        f"El cue #{cue_id} ya tiene un archivo asignado.\n¿Desea reemplazarlo?",
                        QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                    )
                    if reply != QMessageBox.StandardButton.Yes:
                        event.ignore()
                        return

            self.file_assigned.emit(row, path)
            rows_affected.append(row + 1)

        if rows_affected:
            if len(rows_affected) == 1:
                msg = f"Archivo asignado al cue {rows_affected[0]}."
            else:
                nums = ", ".join(str(r) for r in rows_affected)
                msg = f"{len(rows_affected)} archivos asignados a cues {nums}."
            self.status_message.emit(msg, 4000)

        event.acceptProposedAction()

    # ── Helpers ──────────────────────────────────────────────────────────

    @staticmethod
    def _has_valid_urls(mime) -> bool:
        if not mime.hasUrls():
            return False
        return any(
            u.isLocalFile() and Path(u.toLocalFile()).suffix.lower() in _MEDIA_EXTS
            for u in mime.urls()
        )

    def _first_free_row(self) -> int:
        """Devuelve el índice de la primera fila sin archivo asignado, o -1."""
        mdl = self.model()
        if mdl is None:
            return -1
        for row in range(mdl.rowCount()):
            item = mdl.item(row, COL_NAME)
            if item is not None and not item.data(Qt.ItemDataRole.UserRole):
                return row
        return -1

    def _set_drop_bg(self, color: str) -> None:
        self.viewport().setStyleSheet(f"background-color: {color};")


# ---------------------------------------------------------------------------
# MainWindow
# ---------------------------------------------------------------------------

class MainWindow(QMainWindow):
    """Ventana principal. Construye la UI y expone señales para el controlador."""

    # ── Señales existentes ────────────────────────────────────────────────
    go_pressed:          Signal = Signal()
    stop_pressed:        Signal = Signal()
    stress_test_pressed: Signal = Signal()

    # ── Paso 4: guardar / cargar / editar ─────────────────────────────────
    cue_edit_committed: Signal = Signal(int, dict)  # (row_index, fields)
    save_requested:     Signal = Signal()
    load_requested:     Signal = Signal()

    # ── Paso 5: navegación por teclado ───────────────────────────────────
    prev_pressed:  Signal = Signal()
    jump_to_cue:   Signal = Signal(int)   # (cue_number, 1-based list index)

    # ── Añadir cue nuevo ─────────────────────────────────────────────────
    cue_add_requested: Signal = Signal(str)  # (cue_type)

    def __init__(self, model: CueModel) -> None:
        super().__init__()
        self._model:        CueModel           = model
        self._qt_model:     QStandardItemModel = QStandardItemModel()
        self._active_index: int = 0
        self._prev_active:  int = -1

        # Buffer para acumular dígitos de salto de cue (ej: "1","5" → 15)
        self._key_buffer:   str    = ""
        self._key_timer:    QTimer = QTimer(self)
        self._key_timer.setSingleShot(True)
        self._key_timer.setInterval(800)  # 800 ms de silencio → ejecutar salto
        self._key_timer.timeout.connect(self._on_key_timer_fired)

        # Zona del countdown (0=idle, 1=verde, 2=ámbar, 3=rojo)
        # Guardada para evitar llamadas redundantes a setStyleSheet
        self._cd_zone: int = 0

        self._setup_window()
        self._apply_global_stylesheet()
        self._build_ui()
        self._setup_shortcuts()
        self._populate_table()
        self._check_ffmpeg()

        # ── DEBUG: verificar el VideoContainer 500 ms después de show() ─────
        def _debug_container() -> None:
            w = self._video_container
            print(f"[DEBUG] video_container size:                {w.size()}")
            print(f"[DEBUG] video_container winId:               {int(w.winId())}")
            print(f"[DEBUG] isVisible:                           {w.isVisible()}")
            print(f"[DEBUG] WA_NativeWindow:                     {w.testAttribute(Qt.WidgetAttribute.WA_NativeWindow)}")
            print(f"[DEBUG] WA_DontCreateNativeAncestors:        {w.testAttribute(Qt.WidgetAttribute.WA_DontCreateNativeAncestors)}")
            print(f"[DEBUG] mapToGlobal(0,0):                    {w.mapToGlobal(w.rect().topLeft())}")

        QTimer.singleShot(500, _debug_container)

    def _check_ffmpeg(self) -> None:
        from audio_decoder import AudioDecoder
        if not AudioDecoder.is_ffmpeg_available():
            self.statusBar().showMessage(
                "⚠ FFmpeg no encontrado — solo WAV, FLAC y OGG disponibles",
                0,  # 0 = permanente hasta que cambie
            )

    # ------------------------------------------------------------------
    # Configuración inicial
    # ------------------------------------------------------------------

    def _setup_window(self) -> None:
        self.setWindowTitle("CUE CONTROLLER v0.1 — OFFLINE")
        self.setMinimumSize(1024, 600)
        self.resize(1280, 720)

    def _apply_global_stylesheet(self) -> None:
        self.setStyleSheet(f"""
            QMainWindow, QWidget {{
                background-color: {COLOR_BG};
                color: {COLOR_TEXT};
                font-family: "{FONT_MONO}", "{FONT_MONO_ALT}", monospace;
                font-size: 12px;
            }}
            QTableView {{
                background-color: {COLOR_BG};
                alternate-background-color: {COLOR_BG_ALT};
                gridline-color: {COLOR_BORDER};
                border: 1px solid {COLOR_BORDER};
                selection-background-color: transparent;
                selection-color: {COLOR_TEXT};
                outline: none;
            }}
            QHeaderView::section {{
                background-color: {COLOR_HEADER_BG};
                color: {COLOR_ACCENT};
                font-family: "{FONT_MONO}", monospace;
                font-size: 11px;
                font-weight: bold;
                padding: 4px 8px;
                border: none;
                border-right: 1px solid {COLOR_BORDER};
                border-bottom: 1px solid {COLOR_BORDER};
                letter-spacing: 1px;
            }}
            QPushButton {{
                border: none;
                border-radius: 4px;
                font-family: "{FONT_MONO}", monospace;
                font-weight: bold;
                color: #ffffff;
            }}
            QPushButton:pressed {{ opacity: 0.85; }}
            QLabel {{ background-color: transparent; }}
            QSplitter::handle {{ background-color: {COLOR_BORDER}; }}
        """)

    # ------------------------------------------------------------------
    # Layout principal
    # ------------------------------------------------------------------

    def _build_ui(self) -> None:
        central = QWidget()
        self.setCentralWidget(central)
        root = QVBoxLayout(central)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)
        root.addWidget(self._build_top_bar())
        root.addWidget(self._build_splitter(), stretch=1)

    # ------------------------------------------------------------------
    # Barra superior
    # ------------------------------------------------------------------

    def _build_top_bar(self) -> QWidget:
        bar = QWidget()
        bar.setFixedHeight(36)
        bar.setStyleSheet(
            f"background-color: #111111; border-bottom: 1px solid {COLOR_BORDER};"
        )
        lo = QHBoxLayout(bar)
        lo.setContentsMargins(12, 0, 12, 0)
        lo.setSpacing(6)

        lbl = QLabel("CUE LIST")
        lbl.setStyleSheet(
            f"color: {COLOR_ACCENT}; font-size: 11px; font-weight: bold; letter-spacing: 2px;"
        )
        lo.addWidget(lbl)
        lo.addStretch()

        self._btn_save = QPushButton("GUARDAR")
        self._btn_save.setFixedSize(78, 24)
        self._btn_save.setToolTip("Guardar la cue list en JSON  (Ctrl+S)")
        self._btn_save.setStyleSheet("""
            QPushButton {
                background-color: #1a3a1a; color: #81c784;
                font-size: 10px; letter-spacing: 1px; border-radius: 3px;
            }
            QPushButton:hover   { background-color: #254a25; color: #a5d6a7; }
            QPushButton:pressed { background-color: #1a2a1a; }
        """)
        self._btn_save.clicked.connect(self.save_requested)
        lo.addWidget(self._btn_save)

        self._btn_load = QPushButton("ABRIR")
        self._btn_load.setFixedSize(64, 24)
        self._btn_load.setToolTip("Cargar cue list desde JSON  (Ctrl+O)")
        self._btn_load.setStyleSheet("""
            QPushButton {
                background-color: #1a2a3a; color: #64b5f6;
                font-size: 10px; letter-spacing: 1px; border-radius: 3px;
            }
            QPushButton:hover   { background-color: #253a4a; color: #90caf9; }
            QPushButton:pressed { background-color: #1a2030; }
        """)
        self._btn_load.clicked.connect(self.load_requested)
        lo.addWidget(self._btn_load)

        self._btn_stress = QPushButton("STRESS TEST")
        self._btn_stress.setFixedSize(120, 24)
        self._btn_stress.setStyleSheet(f"""
            QPushButton {{
                background-color: {COLOR_STRESS}; color: #aaaaaa;
                font-size: 10px; letter-spacing: 1px; border-radius: 3px;
            }}
            QPushButton:hover {{ background-color: #555555; color: #ffffff; }}
            QPushButton[active="true"] {{ background-color: #e8a200; color: #000000; }}
        """)
        self._btn_stress.clicked.connect(self.stress_test_pressed)
        lo.addWidget(self._btn_stress)

        self._btn_add_cue = QPushButton("＋ CUE")
        self._btn_add_cue.setFixedHeight(24)
        self._btn_add_cue.setStyleSheet("""
            QPushButton {
                background: #2a2a2a; color: #e8a200;
                border: 1px solid #e8a200;
                border-radius: 3px;
                padding: 0 12px;
                font-size: 10px; font-weight: bold;
            }
            QPushButton:hover   { background: #3a3a00; }
            QPushButton:pressed { background: #1a1a00; }
        """)
        self._btn_add_cue.clicked.connect(self._on_add_cue_clicked)
        lo.addWidget(self._btn_add_cue)

        self._btn_media = QPushButton("📁 MEDIA")
        self._btn_media.setFixedSize(88, 24)
        self._btn_media.setToolTip("Importar archivos de media a cues libres")
        self._btn_media.setStyleSheet("""
            QPushButton {
                background-color: #1a2a3a; color: #80b0d0;
                font-size: 10px; letter-spacing: 1px; border-radius: 3px;
                border: 1px solid #2a4050;
            }
            QPushButton:hover   { background-color: #253545; color: #a0c8e8; }
            QPushButton:pressed { background-color: #152030; }
        """)
        self._btn_media.clicked.connect(self._on_import_media)
        lo.addWidget(self._btn_media)

        self._btn_settings = QPushButton("⚙ SETTINGS")
        self._btn_settings.setFixedSize(96, 24)
        self._btn_settings.setToolTip("Abrir configuración")
        self._btn_settings.setStyleSheet("""
            QPushButton {
                background-color: #1a1a2a; color: #9090c0;
                font-size: 10px; letter-spacing: 1px; border-radius: 3px;
                border: 1px solid #333355;
            }
            QPushButton:hover   { background-color: #252535; color: #b0b0e0; }
            QPushButton:pressed { background-color: #111120; }
        """)
        self._btn_settings.clicked.connect(self._open_settings)
        lo.addWidget(self._btn_settings)

        return bar

    def _open_settings(self) -> None:
        dlg = SettingsDialog(self)
        dlg.exec()

    def _on_add_cue_clicked(self) -> None:
        menu = QMenu(self)
        menu.addAction("＋ Cue de Audio",  lambda: self.cue_add_requested.emit("AUDIO"))
        menu.addAction("＋ Cue de Vídeo",  lambda: self.cue_add_requested.emit("VIDEO"))
        menu.addAction("＋ Cue de Espera", lambda: self.cue_add_requested.emit("WAIT"))
        menu.addAction("＋ Cue de Script", lambda: self.cue_add_requested.emit("SCRIPT"))
        menu.exec(self._btn_add_cue.mapToGlobal(
            self._btn_add_cue.rect().bottomLeft()
        ))

    def _on_import_media(self) -> None:
        """Abre diálogo de selección múltiple y asigna archivos a cues libres."""
        paths, _ = QFileDialog.getOpenFileNames(
            self,
            "Importar archivos de media",
            "",
            MediaImporter.get_filter_string(),
        )
        if not paths:
            return

        start_row = self._find_first_free_row()
        if start_row == -1:
            # Sin filas libres: usar fila seleccionada o la primera
            sel = self._table_view.selectionModel().currentIndex()
            start_row = sel.row() if sel.isValid() else 0

        total = self._model.count()
        rows_affected: list[int] = []
        for i, path in enumerate(paths):
            row = start_row + i
            if row >= total:
                break
            result = MediaImporter.validate_file(path)
            if result["valid"]:
                self.cue_edit_committed.emit(row, {"file_path": result["filepath"]})
                rows_affected.append(row + 1)

        if rows_affected:
            if len(rows_affected) == 1:
                msg = f"Archivo asignado al cue {rows_affected[0]}."
            else:
                nums = ", ".join(str(r) for r in rows_affected)
                msg = f"{len(rows_affected)} archivos asignados a cues {nums}."
            self.statusBar().showMessage(msg, 4000)

    def _find_first_free_row(self) -> int:
        """Devuelve el índice de la primera fila del modelo sin file_path, o -1."""
        for i, cue in enumerate(self._model.cues):
            if not cue.file_path:
                return i
        return -1

    def _append_row_to_qt_model(self, cue: Cue) -> None:
        """Añade una fila al QStandardItemModel sin reconstruir toda la tabla."""
        self._qt_model.appendRow(self._build_row_items(cue))

    def _on_cues_removed(self, rows: list) -> None:
        """Borra cues del modelo de datos y del modelo Qt (de abajo a arriba)."""
        for row in sorted(rows, reverse=True):
            self._model.remove_cue(row)
            self._qt_model.removeRow(row)
        # Ajustar índice activo
        count = self._model.count()
        if count == 0:
            self._active_index = 0
            self._prev_active = -1
        else:
            self._active_index = min(self._active_index, count - 1)
            self._prev_active = self._active_index
            self.highlight_active_row(self._active_index)

    def _on_cue_moved(self, from_row: int, to_row: int) -> None:
        """Reordena el modelo de datos y reconstruye la tabla Qt."""
        self._model.move_cue(from_row, to_row)
        self._populate_table()

    # ------------------------------------------------------------------
    # QSplitter y columnas
    # ------------------------------------------------------------------

    def _build_splitter(self) -> QSplitter:
        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.setHandleWidth(0)
        splitter.setChildrenCollapsible(False)
        splitter.addWidget(self._build_left_column())
        splitter.addWidget(self._build_right_column())
        splitter.setStretchFactor(0, 35)
        splitter.setStretchFactor(1, 65)
        splitter.setSizes([448, 832])
        return splitter

    def _build_left_column(self) -> QWidget:
        """Columna izquierda: video + VU + countdown + GO + STOP + etiquetas."""
        col = QWidget()
        col.setObjectName("leftPanel")
        col.setMinimumWidth(340)
        col.setStyleSheet(f"#leftPanel {{ background-color: {COLOR_LEFT_BG}; }}")

        lo = QVBoxLayout(col)
        lo.setContentsMargins(10, 10, 10, 10)
        lo.setSpacing(6)
        lo.setAlignment(Qt.AlignmentFlag.AlignTop)

        # ── VideoContainer ───────────────────────────────────────────────
        self._video_container = VideoContainer()
        self._video_container.setFixedSize(320, 180)
        lo.addWidget(self._video_container)

        # ── VU meter ────────────────────────────────────────────────────
        # Debajo del video para confirmar visualmente que el audio sale.
        self._vu_meter = VuMeter()
        lo.addWidget(self._vu_meter)

        # ── Countdown ───────────────────────────────────────────────────
        # Muestra el tiempo restante del cue activo. Verde → ámbar → rojo.
        self._lbl_countdown = QLabel("—")
        self._lbl_countdown.setFixedSize(320, 38)
        self._lbl_countdown.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._lbl_countdown.setStyleSheet(_CD_IDLE_STYLE)
        lo.addWidget(self._lbl_countdown)

        # ── GO ──────────────────────────────────────────────────────────
        self._btn_go = QPushButton("▶  GO")
        self._btn_go.setFixedSize(320, 52)
        self._btn_go.setStyleSheet(f"""
            QPushButton {{
                background-color: {COLOR_GO}; color: #ffffff;
                font-size: 16pt; font-weight: bold;
                border: none; border-radius: 4px;
            }}
            QPushButton:hover   {{ background-color: #27ae60; }}
            QPushButton:pressed {{ background-color: #1e8449; }}
        """)
        self._btn_go.clicked.connect(self.go_pressed)
        lo.addWidget(self._btn_go)

        # ── STOP ─────────────────────────────────────────────────────────
        self._btn_stop = QPushButton("■  STOP")
        self._btn_stop.setFixedSize(320, 34)
        self._btn_stop.setStyleSheet(f"""
            QPushButton {{
                background-color: {COLOR_STOP}; color: #ffffff;
                font-size: 11pt; font-weight: bold;
                border: none; border-radius: 4px;
            }}
            QPushButton:hover   {{ background-color: #c0392b; }}
            QPushButton:pressed {{ background-color: #922b21; }}
        """)
        self._btn_stop.clicked.connect(self.stop_pressed)
        lo.addWidget(self._btn_stop)

        # ── Cue activo ───────────────────────────────────────────────────
        self._lbl_active_cue = QLabel("Cue activo: —")
        self._lbl_active_cue.setStyleSheet(
            f"color: {COLOR_TEXT}; font-size: 11px; "
            "background-color: transparent; padding: 3px 2px 1px 2px;"
        )
        self._lbl_active_cue.setWordWrap(True)
        lo.addWidget(self._lbl_active_cue)

        # ── Siguiente cue ────────────────────────────────────────────────
        self._lbl_next_cue = QLabel("NEXT → —")
        self._lbl_next_cue.setStyleSheet(
            "background-color: #1a1200; color: #e8a200; "
            "font-size: 11px; padding: 4px 6px;"
        )
        self._lbl_next_cue.setWordWrap(True)
        lo.addWidget(self._lbl_next_cue)

        # ── Teclas activas (mini-leyenda) ────────────────────────────────
        # Se actualiza en _setup_shortcuts con las teclas reales cargadas.
        self._lbl_keys = QLabel("")
        self._lbl_keys.setStyleSheet(
            "color: #444444; font-size: 9px; padding: 2px 2px;"
        )
        lo.addWidget(self._lbl_keys)

        # Estado del sistema — invisible, mantiene update_status_bar() API
        self._lbl_sys_state = QLabel("")
        self._lbl_sys_state.setVisible(False)
        lo.addWidget(self._lbl_sys_state)

        lo.addStretch()
        return col

    def _build_right_column(self) -> QWidget:
        col = QWidget()
        lo = QVBoxLayout(col)
        lo.setContentsMargins(0, 0, 0, 0)
        lo.setSpacing(0)
        self._table_view = self._build_table()
        lo.addWidget(self._table_view)
        return col

    # ------------------------------------------------------------------
    # Tabla de cues
    # ------------------------------------------------------------------

    def _build_table(self) -> CueTableView:
        view = CueTableView(self)
        view.setModel(self._qt_model)
        view.setAlternatingRowColors(True)
        view.setSelectionBehavior(QTableView.SelectionBehavior.SelectRows)
        view.setSelectionMode(QTableView.SelectionMode.SingleSelection)
        view.setEditTriggers(QTableView.EditTrigger.NoEditTriggers)
        view.setShowGrid(True)
        view.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        view.setFocus()
        view.verticalHeader().setVisible(False)
        view.setWordWrap(False)
        view.setTextElideMode(Qt.TextElideMode.ElideRight)
        view.setFont(QFont(FONT_MONO, 11))
        view.verticalHeader().setDefaultSectionSize(28)

        hdr = view.horizontalHeader()
        hdr.setSectionResizeMode(QHeaderView.ResizeMode.Fixed)
        hdr.setHighlightSections(False)
        for col, w in COL_WIDTHS.items():
            view.setColumnWidth(col, w)

        view.doubleClicked.connect(self._on_row_double_clicked)
        view.file_assigned.connect(
            lambda row, path: self.cue_edit_committed.emit(row, {"file_path": path})
        )
        view.status_message.connect(
            lambda msg, ms: self.statusBar().showMessage(msg, ms)
        )
        view.cue_removed.connect(self._on_cues_removed)
        view.cue_moved.connect(self._on_cue_moved)
        return view

    # ------------------------------------------------------------------
    # Shortcuts + keybindings
    # ------------------------------------------------------------------

    def _setup_shortcuts(self) -> None:
        """Crea QShortcuts a partir de keybindings.json (o defaults si no existe)."""
        kb = self._load_keybindings()

        def _sc(key: str, signal) -> None:
            try:
                sc = QShortcut(QKeySequence(key), self)
                sc.setContext(Qt.ShortcutContext.WindowShortcut)
                sc.activated.connect(signal)
            except Exception:
                pass   # tecla inválida en el JSON → ignorar silenciosamente

        _sc(kb["go"],   self.go_pressed)
        _sc(kb["stop"], self.stop_pressed)
        _sc(kb["prev"], self.prev_pressed)
        _sc(kb["save"], self.save_requested)
        _sc(kb["load"], self.load_requested)

        # Leyenda visible de las teclas activas
        self._lbl_keys.setText(
            f"GO: {kb['go']}   STOP: {kb['stop']}   PREV: {kb['prev']}   "
            f"0-9: saltar a cue"
        )

    @staticmethod
    def _load_keybindings() -> dict[str, str]:
        """Carga keybindings.json; usa defaults para las claves ausentes o inválidas."""
        kb = dict(_DEFAULT_KB)
        kb_path = Path(__file__).parent / "keybindings.json"
        if not kb_path.exists():
            return kb
        try:
            with open(kb_path, encoding="utf-8") as fh:
                overrides = json.load(fh)
            for key, val in overrides.items():
                # Ignorar la clave de comentario "_doc" y valores no-string
                if key in kb and isinstance(val, str) and val:
                    kb[key] = val
        except Exception:
            pass   # JSON malformado → usar defaults
        return kb

    # ------------------------------------------------------------------
    # Teclado numérico — salto rápido a cue
    # ------------------------------------------------------------------

    def keyPressEvent(self, event: QKeyEvent) -> None:  # noqa: N802
        """Captura dígitos 0–9 para salto directo de cue.

        El número acumulado en _key_buffer se envía como jump_to_cue
        800 ms después de la última tecla pulsada. Ejemplo: 1,5 → cue #15.
        Teclas no numéricas se propagan normalmente a la cadena de eventos.
        """
        key = event.key()
        if Qt.Key.Key_0 <= key <= Qt.Key.Key_9 and not event.isAutoRepeat():
            digit = key - Qt.Key.Key_0
            self._key_buffer += str(digit)
            self._key_timer.start()   # reinicia el temporizador de 800 ms
            return
        super().keyPressEvent(event)

    def _on_key_timer_fired(self) -> None:
        """800 ms de silencio: dispara el salto y limpia el buffer."""
        buf = self._key_buffer
        self._key_buffer = ""
        if buf:
            try:
                self.jump_to_cue.emit(int(buf))
            except ValueError:
                pass

    # ------------------------------------------------------------------
    # Población y reconstrucción de la tabla
    # ------------------------------------------------------------------

    def _populate_table(self) -> None:
        self._qt_model.clear()
        self._qt_model.setHorizontalHeaderLabels(COL_HEADERS)
        for cue in self._model.cues:
            self._qt_model.appendRow(self._build_row_items(cue))
        if self._model.count() > 0:
            self.highlight_active_row(0)

    def _build_row_items(self, cue: Cue) -> list[QStandardItem]:
        lat = f"{cue.latency_ms:.1f}" if cue.latency_ms is not None else ""
        items = [
            self._make_item(str(cue.id),            Qt.AlignmentFlag.AlignCenter),
            self._make_item(cue.type,               Qt.AlignmentFlag.AlignCenter),
            self._make_item(cue.name,               Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
            self._make_item(f"{cue.duration:.1f}s", Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter),
            self._make_item(cue.state,              Qt.AlignmentFlag.AlignCenter),
            self._make_item(lat,                    Qt.AlignmentFlag.AlignCenter),
        ]
        items[COL_STATE].setForeground(QColor(STATE_COLORS.get(cue.state, COLOR_TEXT)))
        # UserRole en COL_NAME: True si el cue tiene archivo asignado (usado por
        # CueTableView para detectar filas libres y por CueDelegate para ●/○).
        items[COL_NAME].setData(bool(cue.file_path), Qt.ItemDataRole.UserRole)
        return items

    @staticmethod
    def _make_item(text: str, align: Qt.AlignmentFlag = Qt.AlignmentFlag.AlignCenter) -> QStandardItem:
        item = QStandardItem(text)
        item.setTextAlignment(align)
        item.setEditable(False)
        return item

    # ------------------------------------------------------------------
    # Métodos públicos — llamados ÚNICAMENTE por MainController
    # ------------------------------------------------------------------

    def refresh_row(self, index: int) -> None:
        cue = self._model.get_cue(index)
        if cue is None:
            return
        row_items = self._build_row_items(cue)
        is_active = (index == self._active_index)
        for col, item in enumerate(row_items):
            existing = self._qt_model.item(index, col)
            if existing is None:
                continue
            existing.setText(item.text())
            existing.setTextAlignment(item.textAlignment())
            # BUG 1: propagar UserRole (has_media) para que ●/○ se actualice
            if col == COL_NAME:
                existing.setData(
                    item.data(Qt.ItemDataRole.UserRole),
                    Qt.ItemDataRole.UserRole,
                )
            if is_active:
                existing.setBackground(QColor(COLOR_ACCENT))
                existing.setForeground(QColor(COLOR_ACCENT_TEXT))
            else:
                existing.setBackground(QColor(0, 0, 0, 0))
                if col == COL_STATE:
                    existing.setForeground(QColor(STATE_COLORS.get(cue.state, COLOR_TEXT)))
                else:
                    existing.setForeground(QColor(COLOR_TEXT))

    def highlight_active_row(self, new_index: int) -> None:
        """Mueve el resaltado ámbar y actualiza etiquetas de cue activo y next."""
        # Restaurar fila anterior
        if 0 <= self._prev_active < self._qt_model.rowCount():
            prev_cue = self._model.get_cue(self._prev_active)
            if prev_cue is not None:
                for col in range(self._qt_model.columnCount()):
                    item = self._qt_model.item(self._prev_active, col)
                    if item:
                        item.setBackground(QColor(0, 0, 0, 0))
                        if col == COL_STATE:
                            item.setForeground(QColor(STATE_COLORS.get(prev_cue.state, COLOR_TEXT)))
                        else:
                            item.setForeground(QColor(COLOR_TEXT))

        self._active_index = new_index
        self._prev_active  = new_index

        for col in range(self._qt_model.columnCount()):
            item = self._qt_model.item(new_index, col)
            if item:
                item.setBackground(QColor(COLOR_ACCENT))
                item.setForeground(QColor(COLOR_ACCENT_TEXT))

        self._table_view.scrollTo(
            self._qt_model.index(new_index, 0), QTableView.ScrollHint.EnsureVisible
        )

        # Etiqueta cue activo
        active_cue = self._model.get_cue(new_index)
        if active_cue is not None:
            self._lbl_active_cue.setText(
                f"Cue activo:  [{active_cue.id:02d}]  —  {active_cue.name}"
            )

        # Etiqueta NEXT
        total = self._model.count()
        if total > 1:
            next_cue = self._model.get_cue((new_index + 1) % total)
            self._lbl_next_cue.setText(
                f"NEXT → [{next_cue.id:02d}]  {next_cue.name}"
                if next_cue else "NEXT → —"
            )
        else:
            self._lbl_next_cue.setText("NEXT → —")

    def update_status_bar(self, cue_index: int, system_state: str) -> None:
        """Actualiza la etiqueta de cue activo y el estado interno del sistema."""
        cue = self._model.get_cue(cue_index)
        if cue is None:
            return
        self._lbl_active_cue.setText(f"Cue activo:  [{cue.id:02d}]  —  {cue.name}")
        _styles = {
            "READY":   f"color: {COLOR_GO};     font-size: 11px; font-weight: bold;",
            "RUNNING": f"color: {COLOR_ACCENT}; font-size: 11px; font-weight: bold;",
            "STOPPED": f"color: {COLOR_STOP};   font-size: 11px; font-weight: bold;",
        }
        self._lbl_sys_state.setText(f"Estado: {system_state}")
        self._lbl_sys_state.setStyleSheet(_styles.get(system_state, _styles["READY"]))

    def update_countdown(self, seconds: float | None) -> None:
        """Actualiza el label del countdown.

        seconds=None → estado IDLE (cue parado).
        seconds=0.0  → terminando.
        Solo cambia el stylesheet cuando cruza un umbral de color para evitar
        recalculaciones de estilo en cada tick de 100 ms.
        """
        if seconds is None or seconds < 0.0:
            if self._cd_zone != 0:
                self._cd_zone = 0
                self._lbl_countdown.setStyleSheet(_CD_IDLE_STYLE)
            self._lbl_countdown.setText("—")
            return

        remaining = max(0.0, seconds)

        # Formato del texto
        m   = int(remaining) // 60
        sec = remaining - m * 60
        if remaining < 10.0:
            text = f"▶  {m}:{sec:04.1f}"    # "▶  0:09.3"
        else:
            text = f"▶  {m}:{int(sec):02d}"  # "▶  0:23"

        # Zona de color (solo reaplica stylesheet al cambiar de zona)
        if remaining > 30.0:
            zone, style = 1, _CD_GREEN_STYLE
        elif remaining > 10.0:
            zone, style = 2, _CD_AMBER_STYLE
        else:
            zone, style = 3, _CD_RED_STYLE

        if zone != self._cd_zone:
            self._cd_zone = zone
            self._lbl_countdown.setStyleSheet(style)

        self._lbl_countdown.setText(text)

    def update_vu_meter(self, rms: float) -> None:
        """Pasa el nivel RMS al VU meter. Llamado por el controlador."""
        self._vu_meter.set_level(rms)

    def reload_table(self) -> None:
        """Reconstruye la tabla. Llamar tras reemplazar el modelo (load show)."""
        self._active_index = 0
        self._prev_active  = -1
        self._vu_meter.reset()
        self.update_countdown(None)
        self._populate_table()

    def show_error(self, title: str, message: str) -> None:
        QMessageBox.critical(self, title, message)

    @property
    def video_container(self) -> VideoContainer:
        return self._video_container

    def set_stress_button_state(self, active: bool) -> None:
        self._btn_stress.setProperty("active", str(active).lower())
        self._btn_stress.style().unpolish(self._btn_stress)
        self._btn_stress.style().polish(self._btn_stress)
        self._btn_stress.setText("■ STRESS RUNNING" if active else "STRESS TEST")

    # ------------------------------------------------------------------
    # Slots privados — tabla
    # ------------------------------------------------------------------

    def _on_row_double_clicked(self, index: QModelIndex) -> None:
        if not index.isValid():
            return
        row = index.row()
        cue = self._model.get_cue(row)
        if cue is None:
            return
        dlg = CueEditDialog(cue, parent=self)
        if dlg.exec() == CueEditDialog.DialogCode.Accepted:
            self.cue_edit_committed.emit(row, dlg.get_values())

