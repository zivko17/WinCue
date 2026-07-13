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
    QApplication,
    QFileDialog,
    QFrame,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QMainWindow,
    QMenu,
    QMessageBox,
    QPushButton,
    QSizePolicy,
    QSlider,
    QSplitter,
    QStackedLayout,
    QTableView,
    QVBoxLayout,
    QWidget,
)

from cue_edit_dialog import CueEditDialog
from inspector_panel import InspectorPanel
from monitor_widget import MonitorWidget
from cue_model import CUE_COLORS, STATE_COLORS, Cue, CueModel
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
    "QLabel {{ color: {color}; font-size: 30pt; font-weight: bold; "
    "background-color: #0d0d0d; border: 1px solid #222; border-radius: 5px; "
    f'font-family: "{FONT_MONO}", monospace; }}}}'
)
_CD_IDLE_STYLE  = _CD_STYLE.format(color="#3a3a3a")
_CD_GREEN_STYLE = _CD_STYLE.format(color="#2ecc71")
_CD_AMBER_STYLE = _CD_STYLE.format(color="#e8a200")
_CD_RED_STYLE   = _CD_STYLE.format(color="#e74c3c")

# Teclas por defecto (sobrescritas por keybindings.json si existe)
_DEFAULT_KB: dict[str, str] = {
    "go":    "Space",
    "stop":  "S",
    "panic": "Esc",
    "pause": "P",
    "prev":  "Up",
    "next":  "Down",
    "save":  "Ctrl+S",
    "load":  "Ctrl+O",
}

# Columnas de la tabla (estilo QLab: número, tipo, nombre, temporización, estado)
COL_NUM, COL_TYPE, COL_NAME, COL_PRE, COL_DUR, COL_POST, COL_CONT, COL_TARGET, COL_STATE = range(9)
COL_HEADERS = ["#", "TIPO", "NOMBRE", "PRE", "ACCIÓN", "POST", "CONT", "→", "ESTADO"]
COL_WIDTHS: dict[int, int] = {
    COL_NUM: 46, COL_TYPE: 66, COL_NAME: 240, COL_PRE: 56, COL_DUR: 70,
    COL_POST: 56, COL_CONT: 44, COL_TARGET: 44, COL_STATE: 88,
}

# Símbolos del modo de continuación en la columna CONT
_CONT_SYMBOL: dict[str, str] = {
    "NONE": "", "AUTO_CONTINUE": "▸", "AUTO_FOLLOW": "↳",
}

# Etiqueta corta del modo de un grupo (mostrada junto al nombre)
_GROUP_MODE_TAG: dict[str, str] = {
    "TIMELINE": "todos a la vez",
    "ENTER":    "entrar",
    "GO_NEXT":  "1º + salir",
    "RANDOM":   "azar",
}


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
    files_dropped:  Signal = Signal(int, list)  # (target_row, list[path]) — crear cues
    status_message: Signal = Signal(str, int)   # (message, timeout_ms)
    cue_removed:    Signal = Signal(list)        # list[int] filas a borrar
    cue_moved:      Signal = Signal(int, int)    # (from_row, to_row)
    collapse_clicked: Signal = Signal(int)       # clic en el triángulo ▼/▶ de un grupo

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.edit_enabled = True          # False en modo Show: bloquea edición
        self.setAcceptDrops(True)
        self.setDragEnabled(True)
        # DragDrop permite tanto drag interno (reordenar) como drop externo
        self.setDragDropMode(QAbstractItemView.DragDropMode.DragDrop)
        self.setDefaultDropAction(Qt.DropAction.MoveAction)
        self.setDropIndicatorShown(True)

    # ── Teclado — Delete para borrar cues ────────────────────────────────

    def keyPressEvent(self, event) -> None:
        if event.key() == Qt.Key.Key_Delete:
            if not self.edit_enabled:
                return   # modo Show: no borrar cues
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

    # ── Clic en el triángulo de colapso de un grupo ──────────────────────

    def mousePressEvent(self, event) -> None:
        idx = self.indexAt(event.position().toPoint())
        if idx.isValid() and idx.column() == COL_NUM:
            mdl = self.model()
            item = mdl.item(idx.row(), COL_NUM) if mdl is not None else None
            if item is not None and bool(item.data(Qt.ItemDataRole.UserRole)):
                # Es la celda de número de un GROUP → alternar colapso
                self.collapse_clicked.emit(idx.row())
                return
        super().mousePressEvent(event)

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

    def _index_at_drop(self, event: QDropEvent) -> QModelIndex:
        """Fila bajo el cursor al soltar.

        `indexAt()` espera coordenadas del viewport; si `event.position()` viene
        en coordenadas de la vista (incluye el header), el crudo da inválido y
        el archivo acababa en el primer cue libre ("cualquier cue"). Probamos el
        crudo y, si falla, lo remapeamos al viewport.
        """
        pos = event.position().toPoint()
        idx = self.indexAt(pos)
        if not idx.isValid():
            idx = self.indexAt(self.viewport().mapFrom(self, pos))
        return idx

    def _handle_internal_drop(self, event: QDropEvent) -> None:
        """Reordenado de filas: drag desde la misma tabla."""
        source_row = self.currentIndex().row()
        target_idx = self._index_at_drop(event)
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
        """Suelta archivos desde el explorador → crea un cue nuevo por archivo
        (tipado según el medio), insertándolos donde se suelta (estilo QLab)."""
        self._set_drop_bg(COLOR_BG)
        mime = event.mimeData()
        if not mime.hasUrls():
            event.ignore()
            return

        valid_paths: list[str] = []
        for url in mime.urls():
            if url.isLocalFile():
                result = MediaImporter.validate_file(url.toLocalFile())
                if result["valid"]:
                    valid_paths.append(result["filepath"])

        if not valid_paths:
            event.ignore()
            return

        # Insertar justo debajo de la fila sobre la que se suelta; -1 = al final.
        idx = self._index_at_drop(event)
        target_row = idx.row() + 1 if idx.isValid() else -1
        self.files_dropped.emit(target_row, valid_paths)
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

    # ── Transporte ────────────────────────────────────────────────────────
    go_pressed:      Signal = Signal()
    stop_pressed:    Signal = Signal()   # Stop All (Esc / S)
    panic_pressed:   Signal = Signal()   # fade-out global + parada
    pause_pressed:   Signal = Signal()   # pausa/reanuda
    next_pressed:    Signal = Signal()   # mover playhead abajo
    playhead_set:    Signal = Signal(int)  # fijar playhead a una fila (clic)
    stop_cue:        Signal = Signal(int)  # detener el cue de una fila concreta
    cue_insert_requested: Signal = Signal(str, int)  # (tipo, fila destino)
    master_volume_changed: Signal = Signal(int)   # volumen master 0-100 (modo Show)
    group_requested:  Signal = Signal(list)  # agrupar las filas seleccionadas
    ungroup_requested: Signal = Signal(int)  # deshacer el grupo de una fila
    collapse_toggled:  Signal = Signal(int)  # contraer/expandir un grupo
    hotkey_triggered:  Signal = Signal(int)  # disparar un cue por su hotkey (cue_id)

    # ── Paso 4: guardar / cargar / editar ─────────────────────────────────
    cue_edit_committed: Signal = Signal(int, dict)  # (row_index, fields)
    save_requested:     Signal = Signal()
    load_requested:     Signal = Signal()

    # ── Paso 5: navegación por teclado ───────────────────────────────────
    prev_pressed:  Signal = Signal()
    jump_to_cue:   Signal = Signal(int)   # (cue_number, 1-based list index)

    # ── Añadir cue nuevo ─────────────────────────────────────────────────
    cue_add_requested: Signal = Signal(str)  # (cue_type)
    # Soltar archivos → crear cues nuevos (target_row, list[path]); -1 = al final
    cues_from_files_requested: Signal = Signal(int, list)

    def __init__(self, model: CueModel) -> None:
        super().__init__()
        self._model:        CueModel           = model
        self._qt_model:     QStandardItemModel = QStandardItemModel()
        self._playhead:     int = 0            # índice del cue standby (GO lo dispara)
        self._running_rows: set[int] = set()   # filas con cues en ejecución
        self._syncing_sel:  bool = False       # evita bucle selección↔playhead
        self._hotkeys:      list = []          # QShortcuts de hotkeys por cue
        self._output_window = None             # ventana de salida a otro monitor
        self._output_content: str = "NEGRO"    # qué hay ahora en la salida
        self._show_mode:    bool = False       # False = Edit, True = Show (bloqueado)

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
        self.setWindowTitle("WinCue — reproductor de cues (estilo QLab)")
        self.setMinimumSize(1024, 600)
        self.resize(1320, 760)

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

        # Inspector acoplado BAJO LA LISTA de cues (columna derecha). Toggle Ctrl+I.
        self._root_layout = root
        self._inspector = InspectorPanel()
        self._inspector.setMinimumHeight(150)
        self._inspector.changed.connect(self._on_inspector_changed)
        self._inspector.goto_target.connect(
            lambda cid: self.playhead_set.emit(self._model.index_of_id(cid)))
        self._inspector.popout_requested.connect(self.toggle_inspector_popout)
        self._inspector_popped = False

        # Splitter horizontal: izquierda (transporte, altura completa) | derecha
        # (lista de cues + inspector debajo).
        root.addWidget(self._build_splitter(), stretch=1)

    # ------------------------------------------------------------------
    # Barra superior
    # ------------------------------------------------------------------

    def _build_top_bar(self) -> QWidget:
        bar = QWidget()
        bar.setFixedHeight(40)
        bar.setStyleSheet(
            f"background-color: #111111; border-bottom: 1px solid {COLOR_BORDER};"
        )
        lo = QHBoxLayout(bar)
        lo.setContentsMargins(10, 0, 12, 0)
        lo.setSpacing(6)

        # ── Toggle EDIT / SHOW (izquierda) ────────────────────────────────
        self._btn_edit = QPushButton("EDIT")
        self._btn_show = QPushButton("SHOW")
        for b in (self._btn_edit, self._btn_show):
            b.setCheckable(True)
            b.setFixedSize(58, 26)
            b.setStyleSheet("""
                QPushButton { background:#222; color:#888; font-size:10px;
                    font-weight:bold; letter-spacing:1px; border:1px solid #333; }
                QPushButton:checked { background:#e8a200; color:#000; border-color:#e8a200; }
            """)
        self._btn_edit.setChecked(True)
        self._btn_edit.clicked.connect(lambda: self.set_mode(False))
        self._btn_show.clicked.connect(lambda: self.set_mode(True))
        lo.addWidget(self._btn_edit)
        lo.addWidget(self._btn_show)
        lo.addSpacing(14)

        self._lbl_title = QLabel("CUE LIST")
        self._lbl_title.setStyleSheet(
            f"color: {COLOR_ACCENT}; font-size: 11px; font-weight: bold; letter-spacing: 2px;"
        )
        lo.addWidget(self._lbl_title)
        lo.addStretch()

        # ── Volumen master (siempre visible; único control en modo Show) ──
        mlbl = QLabel("MASTER")
        mlbl.setStyleSheet("color:#888; font-size:9px; font-weight:bold; letter-spacing:1px;")
        lo.addWidget(mlbl)
        self._master_slider = QSlider(Qt.Orientation.Horizontal)
        self._master_slider.setRange(0, 100)
        self._master_slider.setValue(100)
        self._master_slider.setFixedWidth(130)
        self._master_slider.setToolTip("Volumen general de toda la reproducción")
        self._master_slider.setStyleSheet(
            "QSlider::groove:horizontal{height:5px;background:#333;border-radius:2px;}"
            "QSlider::handle:horizontal{width:14px;background:#e8a200;border-radius:3px;margin:-5px 0;}"
            "QSlider::sub-page:horizontal{background:#e8a200;border-radius:2px;}")
        self._lbl_master_val = QLabel("100%")
        self._lbl_master_val.setStyleSheet("color:#e0e0e0; font-size:10px; min-width:34px;")
        self._master_slider.valueChanged.connect(self._on_master_slider)
        lo.addWidget(self._master_slider)
        lo.addWidget(self._lbl_master_val)
        lo.addSpacing(10)

        # ── Grupo de edición (oculto en modo Show) ────────────────────────
        def _sep():
            s = QLabel(); s.setFixedSize(1, 22)
            s.setStyleSheet("background:#333;")
            return s

        self._btn_save = self._topbtn("GUARDAR", "#1a3a1a", "#81c784", 78,
                                      "Guardar la cue list (Ctrl+S)", self.save_requested)
        self._btn_load = self._topbtn("ABRIR", "#1a2a3a", "#64b5f6", 64,
                                      "Cargar cue list (Ctrl+O)", self.load_requested)
        self._btn_add_cue = self._topbtn("＋ CUE", "#2a2a2a", "#e8a200", 74,
                                         "Añadir cue", None)
        self._btn_add_cue.clicked.connect(self._on_add_cue_clicked)
        self._btn_media = self._topbtn("📁 MEDIA", "#1a2a3a", "#80b0d0", 88,
                                       "Importar media", None)
        self._btn_media.clicked.connect(self._on_import_media)
        self._btn_settings = self._topbtn("⚙ SETTINGS", "#1a1a2a", "#9090c0", 96,
                                          "Configuración", None)
        self._btn_settings.clicked.connect(self._open_settings)

        s1, s2 = _sep(), _sep()
        self._edit_widgets = [self._btn_save, self._btn_load, s1,
                              self._btn_add_cue, self._btn_media, s2, self._btn_settings]
        for wdg in self._edit_widgets:
            lo.addWidget(wdg)

        return bar

    def _topbtn(self, text, bg, fg, width, tip, signal):
        b = QPushButton(text)
        b.setFixedSize(width, 26)
        b.setToolTip(tip)
        b.setStyleSheet(f"""
            QPushButton {{ background-color:{bg}; color:{fg}; font-size:10px;
                letter-spacing:1px; border-radius:3px; border:1px solid #2a2a2a; }}
            QPushButton:hover {{ border-color:{fg}; }}
        """)
        if signal is not None:
            b.clicked.connect(signal)
        return b

    def _on_master_slider(self, v: int) -> None:
        self._lbl_master_val.setText(f"{v}%")
        self.master_volume_changed.emit(v)

    # ------------------------------------------------------------------
    # Modo Edit / Show
    # ------------------------------------------------------------------

    def set_mode(self, show: bool) -> None:
        """Alterna entre modo edición (todo) y modo show (mínimo, bloqueado)."""
        self._show_mode = show
        self._btn_edit.setChecked(not show)
        self._btn_show.setChecked(show)
        self._lbl_title.setText("▶ SHOW" if show else "CUE LIST")
        # Ocultar botones de edición y el inspector en modo Show
        for wdg in self._edit_widgets:
            wdg.setVisible(not show)
        if not self._inspector_popped:
            self._inspector.setVisible(not show)
        # Bloquear edición de la lista
        self._table_view.edit_enabled = not show
        self._table_view.setDragEnabled(not show)
        self._table_view.setAcceptDrops(not show)
        self.flash_status("MODO SHOW — edición bloqueada (solo reproducción)"
                          if show else "MODO EDIT")

    def _open_settings(self) -> None:
        dlg = SettingsDialog(self)
        dlg.settings_saved.connect(self.refresh_video_output)
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
        # Ajustar playhead
        count = self._model.count()
        self._running_rows.clear()
        if count == 0:
            self._playhead = 0
            self._update_playhead_labels()
        else:
            self.set_playhead(min(self._playhead, count - 1))

    def _on_cue_moved(self, from_row: int, to_row: int) -> None:
        """Reordena el modelo de datos y reconstruye la tabla Qt."""
        self._model.move_cue(from_row, to_row)
        self._populate_table()

    # ------------------------------------------------------------------
    # QSplitter y columnas
    # ------------------------------------------------------------------

    def _build_splitter(self) -> QSplitter:
        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.setHandleWidth(4)
        splitter.setChildrenCollapsible(False)
        splitter.addWidget(self._build_left_column())
        splitter.addWidget(self._build_right_column())
        splitter.setStretchFactor(0, 0)
        splitter.setStretchFactor(1, 1)
        splitter.setSizes([400, 900])
        return splitter

    def _build_left_column(self) -> QWidget:
        """Columna izquierda organizada en tarjetas: monitor, transporte, cues."""
        col = QWidget()
        col.setObjectName("leftPanel")
        col.setMinimumWidth(376)
        col.setMaximumWidth(420)
        col.setStyleSheet(f"#leftPanel {{ background-color: {COLOR_LEFT_BG}; }}")

        lo = QVBoxLayout(col)
        lo.setContentsMargins(12, 12, 12, 12)
        lo.setSpacing(10)

        def _card(title):
            frame = QFrame()
            frame.setObjectName("card")
            frame.setStyleSheet(
                "QFrame#card{background:#141414; border:1px solid #2a2a2a; border-radius:8px;}")
            v = QVBoxLayout(frame)
            v.setContentsMargins(12, 10, 12, 12)
            v.setSpacing(9)
            if title:
                h = QLabel(title)
                h.setStyleSheet("color:#e8a200; font-size:10px; font-weight:bold; "
                                "letter-spacing:2px; background:transparent; border:none;")
                v.addWidget(h)
            return frame, v

        # ══ TARJETA: MONITOR ══════════════════════════════════════════════
        card_mon, cm = _card("MONITOR DE SALIDA")

        # Conmutador PROGRAMA / PREVIEW
        self._monitor_toggle = QWidget()
        mt = QHBoxLayout(self._monitor_toggle)
        mt.setContentsMargins(0, 0, 0, 0); mt.setSpacing(6)
        self._btn_pgm = QPushButton("● PROGRAMA")
        self._btn_pvw = QPushButton("■ PREVIEW")
        for b in (self._btn_pgm, self._btn_pvw):
            b.setCheckable(True); b.setFixedHeight(28)
            b.setStyleSheet("""
                QPushButton { background:#1a1a1a; color:#999; font-size:11px;
                    font-weight:bold; letter-spacing:1px; border:1px solid #2a2a2a; border-radius:4px; }
                QPushButton#pgm:checked { background:#7a1010; color:#fff; border-color:#a03030; }
                QPushButton#pvw:checked { background:#106a3a; color:#fff; border-color:#2a9a5a; }
            """)
        self._btn_pgm.setObjectName("pgm"); self._btn_pvw.setObjectName("pvw")
        self._btn_pvw.setChecked(True)
        self._monitor_mode = "PVW"
        self._btn_pgm.clicked.connect(lambda: self._set_monitor_mode("PGM"))
        self._btn_pvw.clicked.connect(lambda: self._set_monitor_mode("PVW"))
        mt.addWidget(self._btn_pgm); mt.addWidget(self._btn_pvw)
        cm.addWidget(self._monitor_toggle)

        # Monitor (mpv local o miniatura PGM/PVW)
        mon_holder = QWidget(); mon_holder.setFixedSize(320, 180)
        self._monitor_stack = QStackedLayout(mon_holder)
        self._monitor_stack.setContentsMargins(0, 0, 0, 0)
        self._video_container = VideoContainer()
        self._video_container.setFixedSize(320, 180)
        self._monitor = MonitorWidget()
        self._monitor_stack.addWidget(self._video_container)
        self._monitor_stack.addWidget(self._monitor)
        self._monitor_stack.setCurrentWidget(self._video_container)
        self._monitor.set_badge("PVW", "#2ecc71")
        cm.addWidget(mon_holder, 0, Qt.AlignmentFlag.AlignHCenter)

        # Testigo de salida
        self._lbl_output_status = QLabel("○ Salida: ventana local")
        self._lbl_output_status.setStyleSheet(
            "color:#888; font-size:11px; background:#0d0d0d; "
            "border:1px solid #222; border-radius:4px; padding:4px 8px;")
        cm.addWidget(self._lbl_output_status)

        # VU meter
        self._vu_meter = VuMeter()
        cm.addWidget(self._vu_meter, 0, Qt.AlignmentFlag.AlignHCenter)
        lo.addWidget(card_mon)

        # ══ TARJETA: TRANSPORTE ═══════════════════════════════════════════
        card_tr, ct = _card("TRANSPORTE")

        self._lbl_countdown = QLabel("—")
        self._lbl_countdown.setFixedHeight(58)
        self._lbl_countdown.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._lbl_countdown.setStyleSheet(_CD_IDLE_STYLE)
        ct.addWidget(self._lbl_countdown)

        self._btn_go = QPushButton("▶  GO")
        self._btn_go.setFixedHeight(66)
        self._btn_go.setStyleSheet(f"""
            QPushButton {{
                background-color: {COLOR_GO}; color: #ffffff;
                font-size: 22pt; font-weight: bold; border: none; border-radius: 6px;
            }}
            QPushButton:hover   {{ background-color: #27ae60; }}
            QPushButton:pressed {{ background-color: #1e8449; }}
        """)
        self._btn_go.clicked.connect(self.go_pressed)
        ct.addWidget(self._btn_go)

        transport = QWidget()
        tlo = QHBoxLayout(transport)
        tlo.setContentsMargins(0, 0, 0, 0); tlo.setSpacing(8)

        self._btn_stop = QPushButton("■\nSTOP ALL")
        self._btn_stop.setFixedHeight(50)
        self._btn_stop.setStyleSheet(f"""
            QPushButton {{ background-color: {COLOR_STOP}; color: #ffffff;
                font-size: 11pt; font-weight: bold; border: none; border-radius: 6px; }}
            QPushButton:hover   {{ background-color: #c0392b; }}
            QPushButton:pressed {{ background-color: #922b21; }}
        """)
        self._btn_stop.setToolTip("Detener todos los cues  (S)")
        self._btn_stop.clicked.connect(self.stop_pressed)
        tlo.addWidget(self._btn_stop, stretch=3)

        self._btn_panic = QPushButton("⚠\nPANIC")
        self._btn_panic.setFixedHeight(50)
        self._btn_panic.setToolTip("PANIC: fundido global y parada  (Esc)")
        self._btn_panic.setStyleSheet("""
            QPushButton { background-color: #7a1f10; color: #ffb199;
                font-size: 11pt; font-weight: bold; border: none; border-radius: 6px; }
            QPushButton:hover   { background-color: #9a2a15; color: #ffffff; }
            QPushButton:pressed { background-color: #5a160b; }
        """)
        self._btn_panic.clicked.connect(self.panic_pressed)
        tlo.addWidget(self._btn_panic, stretch=2)

        self._btn_pause = QPushButton("❚❚\nPAUSE")
        self._btn_pause.setFixedHeight(50)
        self._btn_pause.setToolTip("Pausar / reanudar  (P)")
        self._btn_pause.setStyleSheet("""
            QPushButton { background-color: #333355; color: #c0c0e8;
                font-size: 11pt; font-weight: bold; border: none; border-radius: 6px; }
            QPushButton:hover   { background-color: #40406a; color: #ffffff; }
            QPushButton[paused="true"] { background-color: #e8a200; color: #000000; }
        """)
        self._btn_pause.clicked.connect(self.pause_pressed)
        tlo.addWidget(self._btn_pause, stretch=3)
        ct.addWidget(transport)
        lo.addWidget(card_tr)

        # ══ TARJETA: STANDBY / SIGUIENTE ══════════════════════════════════
        card_sb, cs = _card("SIGUIENTE (STANDBY)")
        self._lbl_active_cue = QLabel("STANDBY → —")
        self._lbl_active_cue.setWordWrap(True)
        self._lbl_active_cue.setStyleSheet(
            "color:#ffffff; font-size:14px; font-weight:bold; "
            "background:#1c1400; border:1px solid #3a2e00; border-radius:4px; padding:6px 8px;")
        cs.addWidget(self._lbl_active_cue)
        self._lbl_next_cue = QLabel("NEXT → —")
        self._lbl_next_cue.setWordWrap(True)
        self._lbl_next_cue.setStyleSheet(
            "color:#e8a200; font-size:12px; background:transparent; padding:2px 4px;")
        cs.addWidget(self._lbl_next_cue)
        lo.addWidget(card_sb)

        # ══ TARJETA: EN EJECUCIÓN (crece con el espacio) ══════════════════
        card_ex, ce = _card("EN EJECUCIÓN")
        self._lbl_active_panel = QLabel(
            "<span style='color:#555'>— sin cues en ejecución —</span>")
        self._lbl_active_panel.setWordWrap(True)
        self._lbl_active_panel.setTextFormat(Qt.TextFormat.RichText)
        self._lbl_active_panel.setAlignment(
            Qt.AlignmentFlag.AlignTop | Qt.AlignmentFlag.AlignLeft)
        self._lbl_active_panel.setStyleSheet(
            "background-color:#0d0d0d; border:1px solid #222; border-radius:4px; "
            "font-size:12px; padding:8px 10px;")
        self._lbl_active_panel.setSizePolicy(
            QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        ce.addWidget(self._lbl_active_panel, stretch=1)
        lo.addWidget(card_ex, stretch=1)

        # Leyenda de teclas (pie)
        self._lbl_keys = QLabel("")
        self._lbl_keys.setStyleSheet("color:#555; font-size:10px; padding:2px;")
        lo.addWidget(self._lbl_keys)

        # Estado del sistema — invisible, mantiene update_status_bar() API
        self._lbl_sys_state = QLabel("")
        self._lbl_sys_state.setVisible(False)
        lo.addWidget(self._lbl_sys_state)
        return col

    def _build_right_column(self) -> QWidget:
        # Divisor vertical arrastrable: lista de cues arriba, inspector debajo.
        self._table_view = self._build_table()
        vs = QSplitter(Qt.Orientation.Vertical)
        vs.setChildrenCollapsible(False)
        vs.setHandleWidth(4)
        vs.addWidget(self._table_view)
        vs.addWidget(self._inspector)
        vs.setStretchFactor(0, 1)
        vs.setStretchFactor(1, 0)
        vs.setSizes([620, 320])
        self._vsplit = vs
        return vs

    # ------------------------------------------------------------------
    # Tabla de cues
    # ------------------------------------------------------------------

    def _build_table(self) -> CueTableView:
        view = CueTableView(self)
        view.setModel(self._qt_model)
        view.setAlternatingRowColors(True)
        view.setSelectionBehavior(QTableView.SelectionBehavior.SelectRows)
        view.setSelectionMode(QTableView.SelectionMode.ExtendedSelection)
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
        # La selección de la tabla ES el playhead (navegación con flechas incluida)
        view.selectionModel().currentRowChanged.connect(self._on_selection_changed)
        view.file_assigned.connect(
            lambda row, path: self.cue_edit_committed.emit(row, {"file_path": path})
        )
        view.files_dropped.connect(self.cues_from_files_requested)
        view.status_message.connect(
            lambda msg, ms: self.statusBar().showMessage(msg, ms)
        )
        view.cue_removed.connect(self._on_cues_removed)
        view.cue_moved.connect(self._on_cue_moved)

        view.collapse_clicked.connect(self.collapse_toggled)
        view.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        view.customContextMenuRequested.connect(self._show_cue_menu)
        return view

    def _selected_rows(self) -> list:
        """Filas seleccionadas actualmente (ordenadas)."""
        sel = self._table_view.selectionModel()
        if sel is None:
            return []
        return sorted({i.row() for i in sel.selectedRows()})

    def _show_cue_menu(self, pos) -> None:
        """Menú contextual estilo QLab sobre la fila bajo el cursor."""
        index = self._table_view.indexAt(pos)
        menu = QMenu(self)
        row = index.row() if index.isValid() else -1

        if row >= 0:
            cue = self._model.get_cue(row)
            menu.addAction("▶  Disparar (GO desde aquí)",
                           lambda: (self.playhead_set.emit(row), self.go_pressed.emit()))
            menu.addAction("◉  Fijar como standby",
                           lambda: self.playhead_set.emit(row))
            menu.addAction("■  Detener este cue",
                           lambda: self.stop_cue.emit(row))

        # En modo Show solo acciones de reproducción, nada de edición.
        if self._show_mode:
            if row >= 0:
                menu.exec(self._table_view.viewport().mapToGlobal(pos))
            return

        if row >= 0:
            menu.addSeparator()
            menu.addAction("✎  Editar propiedades…",
                           lambda: self._on_row_double_clicked(
                               self._qt_model.index(row, 0)))
            menu.addSeparator()

            # ── Grupos ────────────────────────────────────────────────
            sel_rows = self._selected_rows()
            if not sel_rows:
                sel_rows = [row]
            act_group = menu.addAction(
                f"⧉  Agrupar {len(sel_rows)} cue(s)  (Ctrl+G)",
                lambda: self.group_requested.emit(sel_rows))
            if cue is not None and cue.type == "GROUP":
                menu.addAction(
                    ("▶ Expandir grupo" if cue.collapsed else "▼ Contraer grupo"),
                    lambda: self.collapse_toggled.emit(row))
                menu.addAction("⤨  Desagrupar",
                               lambda: self.ungroup_requested.emit(row))

        insert = menu.addMenu("＋  Insertar cue")
        labels = [("Audio", "AUDIO"), ("Vídeo", "VIDEO"), ("Espera", "WAIT"),
                  ("Fade", "FADE"), ("Start", "START"), ("Stop", "STOP"),
                  ("Script", "SCRIPT"), ("Memo", "MEMO"), ("Grupo", "GROUP")]
        for label, ctype in labels:
            insert.addAction(
                label, lambda ct=ctype: self.cue_insert_requested.emit(ct, row))

        if row >= 0:
            menu.addSeparator()
            menu.addAction("🗑  Borrar cue",
                           lambda: self._on_cues_removed([row]))

        menu.exec(self._table_view.viewport().mapToGlobal(pos))

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

        _sc(kb["go"],    self.go_pressed)
        _sc(kb["stop"],  self.stop_pressed)
        _sc(kb["panic"], self.panic_pressed)
        _sc(kb["pause"], self.pause_pressed)
        _sc(kb["save"],  self.save_requested)
        _sc(kb["load"],  self.load_requested)

        # Ctrl+G — agrupar la selección
        try:
            sc_group = QShortcut(QKeySequence("Ctrl+G"), self)
            sc_group.setContext(Qt.ShortcutContext.WindowShortcut)
            sc_group.activated.connect(
                lambda: self.group_requested.emit(self._selected_rows()))
        except Exception:
            pass

        # Ctrl+I — mostrar/ocultar el inspector acoplado
        try:
            sc_insp = QShortcut(QKeySequence("Ctrl+I"), self)
            sc_insp.setContext(Qt.ShortcutContext.WindowShortcut)
            sc_insp.activated.connect(self.toggle_inspector)
        except Exception:
            pass

        # PREV/NEXT (▲▼) NO se enlazan como shortcuts globales para no chocar
        # con la navegación nativa de la tabla: mover la selección mueve el
        # playhead (ver _on_selection_changed).

        # Leyenda visible de las teclas activas
        self._lbl_keys.setText(
            f"GO: {kb['go']}   STOP: {kb['stop']}   PANIC: {kb['panic']}   "
            f"PAUSE: {kb['pause']}   ▲▼: standby   0-9: saltar"
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
        self.apply_group_visibility()
        self.rebuild_hotkeys()
        if self._model.count() > 0:
            self.set_playhead(0)

    def rebuild_hotkeys(self) -> None:
        """Recrea los QShortcut de las hotkeys por cue definidas en el modelo."""
        for sc in self._hotkeys:
            sc.setEnabled(False)
            sc.deleteLater()
        self._hotkeys = []
        for cue in self._model.cues:
            if not cue.hotkey:
                continue
            try:
                sc = QShortcut(QKeySequence(cue.hotkey), self)
                sc.setContext(Qt.ShortcutContext.WindowShortcut)
                sc.activated.connect(
                    lambda cid=cue.id: self.hotkey_triggered.emit(cid))
                self._hotkeys.append(sc)
            except Exception:
                pass

    def apply_group_visibility(self) -> None:
        """Oculta las filas cuyos grupos ancestros estén colapsados."""
        for row, cue in enumerate(self._model.cues):
            hidden = self._model.is_ancestor_collapsed(cue)
            self._table_view.setRowHidden(row, hidden)

    @staticmethod
    def _fmt_time(v: float) -> str:
        """Formatea un tiempo en segundos de forma compacta ('' si es 0)."""
        if v <= 0.0:
            return ""
        if v >= 60.0:
            m = int(v) // 60
            s = v - m * 60
            return f"{m}:{s:04.1f}"
        return f"{v:.1f}"

    def _build_row_items(self, cue: Cue) -> list[QStandardItem]:
        L  = Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter
        C  = Qt.AlignmentFlag.AlignCenter
        R  = Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter

        target_num = ""
        if cue.target_id is not None:
            tgt = self._model.get_by_id(cue.target_id)
            target_num = tgt.display_number if tgt else "?"

        depth = self._model.depth(cue)
        is_group = (cue.type == "GROUP")
        indent = "   " * depth

        # Número: para grupos, triángulo de colapso ▼/▶ antes del número.
        if is_group:
            tri = "▶" if cue.collapsed else "▼"
            num_text = f"{indent}{tri} {cue.display_number}"
        else:
            num_text = f"{indent}{cue.display_number}"

        # Nombre: indentado; ⚑ si está flagged; grupos añaden su modo.
        flag = "⚑ " if cue.flagged else ""
        if is_group:
            name_text = f"{indent}{flag}▸ {cue.name}  ({_GROUP_MODE_TAG.get(cue.group_mode, cue.group_mode.lower())})"
        else:
            name_text = f"{indent}{flag}{cue.name}"

        items = [None] * 9
        items[COL_NUM]    = self._make_item(num_text, L if (is_group or depth) else C)
        items[COL_TYPE]   = self._make_item(cue.type, C)
        items[COL_NAME]   = self._make_item(name_text, L)
        items[COL_PRE]    = self._make_item(self._fmt_time(cue.pre_wait), R)
        items[COL_DUR]    = self._make_item(self._fmt_time(cue.duration), R)
        items[COL_POST]   = self._make_item(self._fmt_time(cue.post_wait), R)
        items[COL_CONT]   = self._make_item(_CONT_SYMBOL.get(cue.continue_mode, ""), C)
        items[COL_TARGET] = self._make_item(target_num, C)
        items[COL_STATE]  = self._make_item("" if is_group else cue.state, C)

        items[COL_STATE].setForeground(QColor(STATE_COLORS.get(cue.state, COLOR_TEXT)))
        # UserRole en COL_NAME: has_media (para el icono ●/○ del delegado).
        items[COL_NAME].setData(bool(cue.file_path), Qt.ItemDataRole.UserRole)
        # UserRole en COL_NUM: marca de grupo (para detectar clic en el triángulo).
        items[COL_NUM].setData(is_group, Qt.ItemDataRole.UserRole)
        if is_group:
            gfont = QFont(FONT_MONO, 11); gfont.setBold(True)
            items[COL_NAME].setFont(gfont)
            items[COL_NUM].setFont(gfont)
        if not cue.armed:
            for it in items:
                it.setForeground(QColor("#555555"))
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
        """Reconstruye el texto de una fila y reaplica sus colores."""
        cue = self._model.get_cue(index)
        if cue is None:
            return
        row_items = self._build_row_items(cue)
        for col, item in enumerate(row_items):
            existing = self._qt_model.item(index, col)
            if existing is None:
                self._qt_model.setItem(index, col, item)
                continue
            existing.setText(item.text())
            existing.setTextAlignment(item.textAlignment())
            if col == COL_NAME:
                existing.setData(
                    item.data(Qt.ItemDataRole.UserRole), Qt.ItemDataRole.UserRole)
        self._apply_row_colors(index)
        # NEXT/STANDBY labels dependen del contenido del playhead
        if index in (self._playhead, self._playhead + 1):
            self._update_playhead_labels()

    def set_playhead(self, index: int) -> None:
        """Fija el playhead (standby) y actualiza resaltado + etiquetas."""
        old = self._playhead
        self._playhead = index
        if 0 <= old < self._qt_model.rowCount():
            self._apply_row_colors(old)
        if 0 <= index < self._qt_model.rowCount():
            self._apply_row_colors(index)
            # Sincronizar la selección de la tabla sin reentrar en el slot
            self._syncing_sel = True
            self._table_view.selectRow(index)
            self._syncing_sel = False
            self._table_view.scrollTo(
                self._qt_model.index(index, 0),
                QTableView.ScrollHint.EnsureVisible)
        self._update_playhead_labels()
        self._update_inspector()
        self.update_pvw()

    def _on_selection_changed(self, current, previous) -> None:
        """La fila seleccionada pasa a ser el playhead (salvo sync programático)."""
        if self._syncing_sel:
            return
        if current.isValid():
            self.playhead_set.emit(current.row())

    def append_cue_row(self, cue: Cue) -> None:
        """Añade una fila al final de la tabla (sin reconstruir todo)."""
        self._qt_model.appendRow(self._build_row_items(cue))

    def update_active_cues(self, rows: list) -> None:
        """Recibe la lista de cues en ejecución y actualiza panel + resaltado
        + cuenta atrás en la propia fila de cada cue (columna ACCIÓN)."""
        new_running: set[int] = set()
        id_to_time: dict[int, str] = {}
        for r in rows:
            idx = self._model.index_of_id(r.get("id", -1))
            if idx >= 0:
                new_running.add(idx)
                rem = r.get("remaining")
                if rem is not None:
                    m = int(rem) // 60
                    id_to_time[idx] = f"↓{m}:{int(rem) % 60:02d}"
                else:
                    el = r.get("elapsed", 0.0)
                    m = int(el) // 60
                    id_to_time[idx] = f"▲{m}:{int(el) % 60:02d}"

        # Restaurar el texto estático de las filas que dejaron de sonar
        stopped = self._running_rows - new_running
        changed = new_running.symmetric_difference(self._running_rows)
        self._running_rows = new_running
        for idx in changed:
            if 0 <= idx < self._qt_model.rowCount():
                self._apply_row_colors(idx)
        for idx in stopped:
            cue = self._model.get_cue(idx)
            item = self._qt_model.item(idx, COL_DUR)
            if cue is not None and item is not None:
                item.setText(self._fmt_time(cue.duration))

        # Escribir la cuenta atrás en la columna ACCIÓN de cada cue activo
        for idx, txt in id_to_time.items():
            item = self._qt_model.item(idx, COL_DUR)
            if item is not None:
                item.setText(txt)

        self._render_active_panel(rows)

    def update_transport(self, paused: bool) -> None:
        """Actualiza el botón de pausa según el estado del transporte."""
        self._btn_pause.setText("▶ RESUME" if paused else "❚❚ PAUSE")
        self._btn_pause.setProperty("paused", str(paused).lower())
        self._btn_pause.style().unpolish(self._btn_pause)
        self._btn_pause.style().polish(self._btn_pause)

    def flash_status(self, message: str, ms: int = 4000) -> None:
        self.statusBar().showMessage(message, ms)

    # ── Helpers internos de pintado / etiquetas ──────────────────────────

    def _apply_row_colors(self, row: int) -> None:
        cue = self._model.get_cue(row)
        if cue is None:
            return
        is_playhead = (row == self._playhead)
        is_running  = (row in self._running_rows)
        for col in range(self._qt_model.columnCount()):
            item = self._qt_model.item(row, col)
            if item is None:
                continue
            if is_playhead:
                item.setBackground(QColor(COLOR_ACCENT))
                item.setForeground(QColor(COLOR_ACCENT_TEXT))
            elif is_running:
                item.setBackground(QColor("#173d24"))
                item.setForeground(
                    QColor("#2ecc71") if col == COL_STATE else QColor("#eafff0"))
            else:
                # Tira de color del cue en la celda de número (estilo QLab)
                if col == COL_NUM and cue.color and CUE_COLORS.get(cue.color):
                    item.setBackground(QColor(CUE_COLORS[cue.color]))
                    item.setForeground(QColor("#ffffff"))
                    continue
                item.setBackground(QColor(0, 0, 0, 0))
                if col == COL_STATE:
                    item.setForeground(QColor(STATE_COLORS.get(cue.state, COLOR_TEXT)))
                elif not cue.armed:
                    item.setForeground(QColor("#555555"))
                else:
                    item.setForeground(QColor(COLOR_TEXT))

    def _update_inspector(self) -> None:
        """Sincroniza el inspector acoplado con el cue del playhead."""
        if not hasattr(self, "_inspector"):
            return
        # No repoblar el inspector si el cambio viene DEL propio inspector:
        # evita re-decodificar audio / re-extraer frames en cada edición y
        # que se robe el foco a los campos.
        if getattr(self, "_suppress_inspector", False):
            return
        cue = self._model.get_cue(self._playhead)
        count = len(self._selected_rows()) or (1 if cue else 0)
        self._inspector.set_cue(cue, self._model.cues, count)

    def _on_inspector_changed(self, fields: dict) -> None:
        """Un cambio en el inspector se aplica al cue del playhead (sin repoblarlo)."""
        if 0 <= self._playhead < self._model.count():
            self._suppress_inspector = True
            try:
                self.cue_edit_committed.emit(self._playhead, fields)
            finally:
                self._suppress_inspector = False

    def toggle_inspector(self) -> None:
        self._inspector.setVisible(not self._inspector.isVisible())

    def toggle_inspector_popout(self) -> None:
        """Saca el inspector a una ventana flotante (o lo vuelve a acoplar)."""
        if not self._inspector_popped:
            self._inspector.setParent(None)
            self._inspector.setWindowTitle("WinCue — Inspector")
            self._inspector.setWindowFlags(Qt.WindowType.Window)
            self._inspector.resize(820, 340)
            self._inspector.show()
            self._inspector_popped = True
        else:
            self._inspector.setWindowFlags(Qt.WindowType.Widget)
            self._vsplit.addWidget(self._inspector)
            self._vsplit.setSizes([460, 300])
            self._inspector.show()
            self._inspector_popped = False

    def _update_playhead_labels(self) -> None:
        standby = self._model.get_cue(self._playhead)
        if standby is not None:
            self._lbl_active_cue.setText(
                f"STANDBY → [{standby.display_number}]  {standby.name}")
        else:
            self._lbl_active_cue.setText("STANDBY → — (fin de la lista)")
        nxt = self._model.get_cue(self._playhead + 1)
        self._lbl_next_cue.setText(
            f"NEXT → [{nxt.display_number}]  {nxt.name}" if nxt else "NEXT → —")

    def _render_active_panel(self, rows: list) -> None:
        """Dibuja el panel de cues en ejecución (texto con barra de progreso)."""
        if not rows:
            self._lbl_active_panel.setText(
                "<span style='color:#555'>— sin cues en ejecución —</span>")
            return
        lines: list[str] = []
        for r in rows:
            color = CUE_COLORS.get(r.get("color", ""), "") or "#2ecc71"
            num = r.get("number", "")
            name = r.get("name", "")
            if len(name) > 22:
                name = name[:21] + "…"
            rem = r.get("remaining")
            frac = r.get("fraction")
            if rem is not None:
                m = int(rem) // 60
                s = rem - m * 60
                time_txt = f"{m}:{s:04.1f}"
                bar_n = int((frac or 0.0) * 12)
                bar = "█" * bar_n + "░" * (12 - bar_n)
            else:
                el = r.get("elapsed", 0.0)
                m = int(el) // 60
                s = el - m * 60
                time_txt = f"{m}:{s:04.1f}↑"
                bar = "▶ " + "·" * 10
            lines.append(
                f"<span style='color:{color}'>●</span> "
                f"<b>[{num}]</b> {name}<br>"
                f"<span style='color:#888;font-size:10px'>{bar} {time_txt}</span>")
        self._lbl_active_panel.setText("<br>".join(lines))

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
        """Pasa el nivel RMS al VU meter y al del inspector (Audio Levels)."""
        self._vu_meter.set_level(rms)
        if hasattr(self, "_inspector"):
            self._inspector.set_vu_level(rms)

    def reload_table(self) -> None:
        """Reconstruye la tabla. Llamar tras reemplazar el modelo (load show)."""
        self._playhead = 0
        self._running_rows.clear()
        self._vu_meter.reset()
        self.update_countdown(None)
        self.update_active_cues([])
        self._populate_table()

    def show_error(self, title: str, message: str) -> None:
        QMessageBox.critical(self, title, message)

    @property
    def video_container(self) -> VideoContainer:
        return self._video_container

    # ------------------------------------------------------------------
    # Salida de vídeo a otro monitor (pantalla completa)
    # ------------------------------------------------------------------

    def surface_dimensions(self) -> tuple:
        """Dimensiones del canvas: las de Settings, o las de la pantalla de salida."""
        from settings_manager import SettingsManager
        sm = SettingsManager.instance()
        w = int(sm.get("surface_width", 0) or 0)
        h = int(sm.get("surface_height", 0) or 0)
        if w > 0 and h > 0:
            return w, h
        scr = self._output_target_screen() or self.screen()
        if scr is not None:
            g = scr.geometry()
            if g.width() > 0:
                return g.width(), g.height()
        return 1920, 1080

    def _output_target_screen(self):
        """Devuelve la QScreen de salida según Settings, o None si es local."""
        from settings_manager import SettingsManager
        sm = SettingsManager.instance()
        try:
            idx = int(sm.get("video_output_screen", 0) or 0)
        except (TypeError, ValueError):
            idx = 0
        fullscreen = bool(sm.get("video_fullscreen_output", False))
        screens = QApplication.screens()
        if not screens:
            return None
        main_screen = self.screen()
        try:
            main_idx = screens.index(main_screen)
        except ValueError:
            main_idx = 0
        valid = 0 <= idx < len(screens)
        if valid and (fullscreen or idx != main_idx):
            return screens[idx]
        return None

    def refresh_video_output(self) -> None:
        """Crea o cierra la ventana de salida (blackout permanente) según Settings.

        Llamar al arrancar y cada vez que se guardan los ajustes: en cuanto se
        elige una pantalla de salida, aparece una ventana negra a pantalla
        completa en ese monitor (nunca se ve el escritorio).
        """
        scr = self._output_target_screen()
        if scr is not None:
            self._ensure_output_window(scr)
        else:
            self.close_video_output()
        self._update_output_tally()
        self._set_monitor_external(scr is not None)

    # ------------------------------------------------------------------
    # Mini-monitor PGM / PVW
    # ------------------------------------------------------------------

    def _set_monitor_external(self, external: bool) -> None:
        """Con salida externa muestra el mini-monitor PGM/PVW; local → mpv directo."""
        if not hasattr(self, "_monitor_stack"):
            return
        if external:
            self._monitor_stack.setCurrentWidget(self._monitor)
            # Ocultar explícitamente la ventana nativa de mpv (si no, su HWND
            # se queda visible/solapado por encima de la miniatura).
            self._video_container.setVisible(False)
            self._monitor.setVisible(True)
            self._monitor.raise_()
            self._monitor_toggle.setVisible(True)
            self._set_monitor_mode(self._monitor_mode)
        else:
            self._monitor_stack.setCurrentWidget(self._video_container)
            self._monitor.setVisible(False)
            self._video_container.setVisible(True)
            self._video_container.raise_()
            self._monitor_toggle.setVisible(False)

    def _set_monitor_mode(self, mode: str) -> None:
        self._monitor_mode = mode
        self._btn_pgm.setChecked(mode == "PGM")
        self._btn_pvw.setChecked(mode == "PVW")
        if mode == "PGM":
            self._monitor.set_badge("PGM", "#e74c3c")
            self._monitor.clear("PGM — esperando salida")
        else:
            self._monitor.set_badge("PVW", "#2ecc71")
            self.update_pvw()

    def update_pvw(self) -> None:
        """Preview: primer frame del cue de vídeo en standby."""
        if getattr(self, "_monitor_mode", "PVW") != "PVW":
            return
        if not hasattr(self, "_monitor_stack") or self._monitor_stack.currentWidget() is not self._monitor:
            return
        cue = self._model.get_cue(self._playhead)
        from media_import import IMAGE_FORMATS
        if (cue and cue.type == "VIDEO" and cue.file_path
                and Path(cue.file_path).suffix.lower() not in IMAGE_FORMATS):
            self._monitor.show_frame(cue.file_path, cue.trim_start, force=True)
        else:
            self._monitor.clear("PVW — standby sin vídeo")

    def update_program(self, path: str, t: float) -> None:
        """Programa: frame del vídeo que está en la salida ahora mismo."""
        if getattr(self, "_monitor_mode", "PVW") != "PGM":
            return
        if not hasattr(self, "_monitor_stack") or self._monitor_stack.currentWidget() is not self._monitor:
            return
        if path:
            self._monitor.show_frame(path, t)
        else:
            self._monitor.clear("PGM — nada en salida")

    def video_output_container(self) -> VideoContainer:
        """Contenedor donde mpv debe renderizar: la ventana de salida si está
        activa, o el preview empotrado en caso contrario."""
        if self._output_window is not None and self._output_window.isVisible():
            return self._output_window
        return self._video_container

    def _ensure_output_window(self, screen) -> VideoContainer:
        """Crea (si hace falta) y muestra la ventana de salida en `screen`."""
        if self._output_window is None:
            ow = VideoContainer()
            ow.setWindowTitle("WinCue — Salida de vídeo")
            ow.setWindowFlags(
                Qt.WindowType.Window | Qt.WindowType.FramelessWindowHint)
            self._output_window = ow
        ow = self._output_window
        ow.setGeometry(screen.geometry())
        ow.showFullScreen()
        ow.raise_()
        return ow

    def close_video_output(self) -> None:
        """Cierra la ventana de salida (al apagar la app o volver a preview)."""
        if self._output_window is not None:
            self._output_window.close()
            self._output_window = None

    def set_output_content(self, name) -> None:
        """Registra qué cue está en la salida (o None → NEGRO) y refresca el testigo."""
        self._output_content = f"EN PANTALLA: {name}" if name else "NEGRO"
        self._update_output_tally()

    def _update_output_tally(self) -> None:
        """Actualiza el testigo de salida (monitor externo + contenido)."""
        if not hasattr(self, "_lbl_output_status"):
            return
        ow = self._output_window
        if ow is not None and ow.isVisible():
            screens = QApplication.screens()
            scr = self._output_target_screen()
            idx = screens.index(scr) if (scr in screens) else 0
            g = ow.geometry()
            self._lbl_output_status.setText(
                f"◉ SALIDA → Monitor {idx + 1}  ({g.width()}×{g.height()})   ·   {self._output_content}")
            self._lbl_output_status.setStyleSheet(
                "color:#2ecc71; font-size:10px; background:#0d1a0d; "
                "border:1px solid #1a3a1a; border-radius:3px; padding:3px 6px;")
        else:
            self._lbl_output_status.setText("○ Salida: ventana local (elige monitor en ⚙ Settings)")
            self._lbl_output_status.setStyleSheet(
                "color:#888; font-size:10px; background:#0d0d0d; "
                "border:1px solid #222; border-radius:3px; padding:3px 6px;")

    # ------------------------------------------------------------------
    # Slots privados — tabla
    # ------------------------------------------------------------------

    def _on_row_double_clicked(self, index: QModelIndex) -> None:
        if self._show_mode or not index.isValid():
            return
        row = index.row()
        cue = self._model.get_cue(row)
        if cue is None:
            return
        dlg = CueEditDialog(cue, all_cues=self._model.cues, parent=self)
        if dlg.exec() == CueEditDialog.DialogCode.Accepted:
            self.cue_edit_committed.emit(row, dlg.get_values())

