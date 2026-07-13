# =============================================================================
# inspector_panel.py — Inspector acoplado con pestañas (fiel a QLab)
# =============================================================================
#
# Panel bajo la lista de cues que edita EN VIVO el cue seleccionado. Pestañas
# que varían según el tipo: Básicos, Triggers y (para media) Time & Loops.
# Botón de pop-out en la esquina de la barra de pestañas. Contador de selección.
#
# Cada cambio emite `changed({campo: valor})`, que la vista reenvía al
# controlador. `set_cue(cue, all_cues)` carga los valores (sin reemitir).
#
# Alcance de backends: hotkey, second-trigger, fade&stop y wall-clock funcionan;
# la UI de MIDI/Timecode/Duck se guarda pero su motor es fase 2 (marcado).
# =============================================================================

from __future__ import annotations

from typing import Optional

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QButtonGroup,
    QCheckBox,
    QComboBox,
    QDoubleSpinBox,
    QFormLayout,
    QGridLayout,
    QHBoxLayout,
    QKeySequenceEdit,
    QLabel,
    QLineEdit,
    QPlainTextEdit,
    QPushButton,
    QRadioButton,
    QSlider,
    QSpinBox,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from cue_model import (
    CONTINUE_MODES,
    FADE_SCOPES,
    GROUP_MODES,
    RETRIGGER_MODES,
    Cue,
)
from inspector_widgets import ColorSwatches, TimeEdit, VerticalVu
from waveform_widget import WaveformWidget
from geometry_preview import GeometryPreview

_CONT_LABELS = {"NONE": "Do not continue", "AUTO_CONTINUE": "Auto-continue", "AUTO_FOLLOW": "Auto-follow"}
_GROUP_LABELS = {"TIMELINE": "Timeline (todos)", "ENTER": "Entrar (1º)", "GO_NEXT": "1º + salir", "RANDOM": "Azar"}
_RETRIG_LABELS = {"NONE": "does nothing", "PANIC": "panics", "STOP": "stops", "HARD_STOP": "hard stops", "HARD_RESTART": "hard stops & restarts"}
_SCOPE_LABELS = {"PEERS": "peers", "LIST": "list or cart", "ALL": "all"}
_MIDI_TYPES = {"note_on": "Note On", "note_off": "Note Off", "control_change": "Control Change", "program_change": "Program Change"}

_STYLE = """
QWidget#inspectorPanel { background-color: #1c1c1c; border-top: 1px solid #2a2a2a; }
QTabWidget::pane { border: 1px solid #2a2a2a; background: #1c1c1c; }
QTabBar::tab { background: #111; color: #999; padding: 4px 14px; border: 1px solid #2a2a2a; border-bottom: none; font-size: 11px; }
QTabBar::tab:selected { background: #1c1c1c; color: #e8a200; }
QLabel { color: #aaa; background: transparent; font-size: 11px; }
QLineEdit, QDoubleSpinBox, QComboBox, QSpinBox, QKeySequenceEdit, QPlainTextEdit {
    background: #2a2a2a; color: #e0e0e0; border: 1px solid #3a3a3a; border-radius: 3px;
    padding: 2px 6px; font-size: 11px; min-height: 20px;
}
QLineEdit:read-only { color: #777; background: #232323; }
QLineEdit:focus, QDoubleSpinBox:focus, QComboBox:focus, QSpinBox:focus, QKeySequenceEdit:focus, QPlainTextEdit:focus { border-color: #e8a200; }
QComboBox QAbstractItemView { background: #2a2a2a; color: #e0e0e0; selection-background-color: #e8a200; selection-color: #000; border: 1px solid #3a3a3a; }
QCheckBox, QRadioButton { color: #bbb; font-size: 11px; }
QCheckBox::indicator, QRadioButton::indicator { width: 14px; height: 14px; }
QCheckBox::indicator:unchecked { border: 1px solid #555; background: #2a2a2a; border-radius: 3px; }
QCheckBox::indicator:checked { border: 1px solid #e8a200; background: #e8a200; border-radius: 3px; }
QRadioButton::indicator:unchecked { border: 1px solid #555; background: #2a2a2a; border-radius: 7px; }
QRadioButton::indicator:checked { border: 1px solid #e8a200; background: #e8a200; border-radius: 7px; }
QPushButton { background: #333; color: #ccc; border: 1px solid #444; border-radius: 3px; padding: 2px 8px; font-size: 10px; min-height: 20px; }
QPushButton:hover { background: #3c3c3c; }
QPushButton#popout { border: none; background: transparent; color: #888; font-size: 14px; }
QPushButton#popout:hover { color: #e8a200; }
"""


class InspectorPanel(QWidget):
    """Inspector acoplado fiel a QLab. Edita en vivo el cue seleccionado."""

    changed: Signal = Signal(dict)     # {campo: valor}
    goto_target: Signal = Signal(int)  # → : saltar al cue objetivo
    popout_requested: Signal = Signal()

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setObjectName("inspectorPanel")
        self.setStyleSheet(_STYLE)
        self._loading = False
        self._current_id: Optional[int] = None

        root = QVBoxLayout(self)
        root.setContentsMargins(6, 4, 6, 6)
        root.setSpacing(3)

        self._tabs = QTabWidget()
        self._tabs.addTab(self._build_basics(), "Basics")
        self._tabs.addTab(self._build_triggers(), "Triggers")
        self._idx_geo = self._tabs.addTab(self._build_display_geometry(), "Display & Geometry")
        self._idx_tl = self._tabs.addTab(self._build_timeloops(), "Time & Loops")
        self._idx_audio = self._tabs.addTab(self._build_audio_levels(), "Audio Levels")

        # Botón pop-out en la esquina de la barra de pestañas
        btn_pop = QPushButton("⇱")
        btn_pop.setObjectName("popout")
        btn_pop.setToolTip("Sacar el inspector a una ventana flotante")
        btn_pop.setFixedSize(22, 22)
        btn_pop.clicked.connect(self.popout_requested)
        self._tabs.setCornerWidget(btn_pop, Qt.Corner.TopRightCorner)

        root.addWidget(self._tabs)

        # Pie: contador de selección
        self._lbl_count = QLabel("sin selección")
        self._lbl_count.setStyleSheet("color:#666; font-size:10px;")
        root.addWidget(self._lbl_count)

        self.set_cue(None, [])

    # ==================================================================
    # Helpers
    # ==================================================================

    def _spin(self, lo, hi, suffix="", step=1.0, width=64, decimals=0):
        s = QDoubleSpinBox() if decimals else QSpinBox()
        s.setRange(lo, hi)
        if decimals:
            s.setDecimals(decimals)
            s.setSingleStep(step)
        if suffix:
            s.setSuffix(suffix)
        s.setFixedWidth(width)
        return s

    def _emit(self, field, value):
        if self._loading or self._current_id is None:
            return
        self.changed.emit({field: value})

    # ==================================================================
    # Pestaña Basics
    # ==================================================================

    def _build_basics(self):
        w = QWidget()
        outer = QHBoxLayout(w)
        outer.setContentsMargins(8, 8, 8, 8)
        outer.setSpacing(16)

        # ── Columna izquierda: number / duration / pre / post / continue ──
        left = QFormLayout()
        left.setLabelAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        left.setHorizontalSpacing(8)
        left.setVerticalSpacing(6)

        self._e_number = QLineEdit(); self._e_number.setFixedWidth(90)
        self._t_dur = TimeEdit(); self._t_dur.setFixedWidth(90)
        self._t_pre = TimeEdit()
        self._t_post = TimeEdit()
        self._c_cont = QComboBox()
        for k in CONTINUE_MODES:
            self._c_cont.addItem(_CONT_LABELS[k], k)
        self._c_cont.setFixedWidth(130)

        left.addRow("Number:", self._e_number)
        left.addRow("Duration:", self._t_dur)
        left.addRow("Pre Wait:", self._t_pre)
        left.addRow("Post Wait:", self._t_post)
        left.addRow("Continue:", self._c_cont)
        lw = QWidget(); lw.setLayout(left); lw.setFixedWidth(230)
        outer.addWidget(lw)

        # ── Columna derecha: name+color / target / checks / notes ─────────
        right = QVBoxLayout()
        right.setSpacing(6)

        name_row = QHBoxLayout(); name_row.setSpacing(8)
        name_row.addWidget(QLabel("Name:"))
        self._e_name = QLineEdit()
        name_row.addWidget(self._e_name, 1)
        self._colors = ColorSwatches()
        name_row.addWidget(self._colors)
        right.addLayout(name_row)

        # Target: combo de cues (targeting) o ruta de archivo (media) + →
        tgt_row = QHBoxLayout(); tgt_row.setSpacing(6)
        self._lbl_target = QLabel("Target:")
        tgt_row.addWidget(self._lbl_target)
        self._c_target = QComboBox(); self._c_target.setMinimumWidth(160)
        self._e_targetfile = QLineEdit(); self._e_targetfile.setReadOnly(True)
        self._btn_goto = QPushButton("→"); self._btn_goto.setFixedWidth(30)
        self._btn_goto.setToolTip("Ir al objetivo")
        self._btn_goto.clicked.connect(self._on_goto)
        tgt_row.addWidget(self._c_target, 1)
        tgt_row.addWidget(self._e_targetfile, 2)
        tgt_row.addWidget(self._btn_goto)
        self._tgt_widget = QWidget(); self._tgt_widget.setLayout(tgt_row)
        right.addWidget(self._tgt_widget)

        checks = QHBoxLayout(); checks.setSpacing(16)
        self._chk_flag = QCheckBox("Flagged")
        self._chk_autoload = QCheckBox("Auto-load")
        self._chk_armed = QCheckBox("Armed")
        checks.addWidget(self._chk_flag); checks.addWidget(self._chk_autoload)
        checks.addWidget(self._chk_armed); checks.addStretch()
        # Modo de grupo (solo GROUP) en la misma fila
        self._c_group = QComboBox()
        for k in GROUP_MODES:
            self._c_group.addItem(_GROUP_LABELS.get(k, k), k)
        self._lbl_group = QLabel("Modo grupo:")
        checks.addWidget(self._lbl_group); checks.addWidget(self._c_group)
        right.addLayout(checks)

        notes_row = QHBoxLayout(); notes_row.setSpacing(8)
        notes_row.addWidget(QLabel("Notes:"), 0, Qt.AlignmentFlag.AlignTop)
        self._e_notes = QPlainTextEdit()
        self._e_notes.setFixedHeight(60)
        notes_row.addWidget(self._e_notes, 1)
        right.addLayout(notes_row)

        outer.addLayout(right, 1)

        # Conexiones
        self._e_number.editingFinished.connect(lambda: self._emit("number", self._e_number.text().strip()))
        self._t_dur.valueChanged.connect(lambda v: self._emit("duration", v))
        self._t_pre.valueChanged.connect(lambda v: self._emit("pre_wait", v))
        self._t_post.valueChanged.connect(lambda v: self._emit("post_wait", v))
        self._c_cont.currentIndexChanged.connect(lambda: self._emit("continue_mode", self._c_cont.currentData()))
        self._e_name.editingFinished.connect(lambda: self._emit("name", self._e_name.text().strip()))
        self._colors.colorChanged.connect(lambda k: self._emit("color", k))
        self._c_target.currentIndexChanged.connect(lambda: self._emit("target_id", self._c_target.currentData()))
        self._chk_flag.toggled.connect(lambda v: self._emit("flagged", bool(v)))
        self._chk_autoload.toggled.connect(lambda v: self._emit("auto_load", bool(v)))
        self._chk_armed.toggled.connect(lambda v: self._emit("armed", bool(v)))
        self._c_group.currentIndexChanged.connect(lambda: self._emit("group_mode", self._c_group.currentData()))
        self._e_notes.textChanged.connect(lambda: self._emit("notes", self._e_notes.toPlainText()))
        return w

    # ==================================================================
    # Pestaña Triggers
    # ==================================================================

    def _build_triggers(self):
        w = QWidget()
        outer = QHBoxLayout(w)
        outer.setContentsMargins(8, 8, 8, 8)
        outer.setSpacing(20)

        # ── Izquierda: 4 disparadores ─────────────────────────────────
        left = QGridLayout(); left.setHorizontalSpacing(6); left.setVerticalSpacing(8)

        # Hotkey
        self._chk_hotkey = QCheckBox()
        self._e_hotkey = QKeySequenceEdit(); self._e_hotkey.setFixedWidth(150)
        btn_hk_clear = QPushButton("✕"); btn_hk_clear.setFixedWidth(26)
        btn_hk_clear.clicked.connect(lambda: (self._e_hotkey.clear(), self._emit("hotkey", "")))
        left.addWidget(QLabel("Hotkey Trigger:"), 0, 0)
        hk = QHBoxLayout(); hk.setSpacing(4); hk.addWidget(self._chk_hotkey); hk.addWidget(self._e_hotkey); hk.addWidget(btn_hk_clear)
        hkw = QWidget(); hkw.setLayout(hk); left.addWidget(hkw, 0, 1)

        # MIDI
        self._chk_midi = QCheckBox()
        self._c_midi_type = QComboBox()
        for k, v in _MIDI_TYPES.items():
            self._c_midi_type.addItem(v, k)
        self._c_midi_type.setFixedWidth(120)
        self._s_midi_note = self._spin(0, 127, "", 1, 52)
        self._s_midi_vel = self._spin(-1, 127, "", 1, 52)   # -1 = any
        self._btn_capture = QPushButton("Capture"); self._btn_capture.setFixedWidth(70)
        self._s_midi_chan = self._spin(1, 16, "", 1, 52)
        left.addWidget(QLabel("MIDI Trigger:"), 1, 0)
        mrow = QHBoxLayout(); mrow.setSpacing(4)
        mrow.addWidget(self._chk_midi); mrow.addWidget(self._c_midi_type)
        mrow.addWidget(QLabel("Note")); mrow.addWidget(self._s_midi_note)
        mrow.addWidget(QLabel("Vel")); mrow.addWidget(self._s_midi_vel)
        mrow.addWidget(self._btn_capture)
        mw = QWidget(); mw.setLayout(mrow); left.addWidget(mw, 1, 1)
        crow = QHBoxLayout(); crow.setSpacing(4)
        crow.addWidget(QLabel("Channel")); crow.addWidget(self._s_midi_chan); crow.addStretch()
        cw = QWidget(); cw.setLayout(crow); left.addWidget(cw, 2, 1)

        # Wall clock
        self._chk_wall = QCheckBox()
        self._e_wall = QLineEdit(); self._e_wall.setFixedWidth(110); self._e_wall.setPlaceholderText("HH:MM:SS")
        self._btn_days = QPushButton("Every Day"); self._btn_days.setFixedWidth(90)
        left.addWidget(QLabel("Wall Clock Trigger:"), 3, 0)
        wrow = QHBoxLayout(); wrow.setSpacing(4)
        wrow.addWidget(self._chk_wall); wrow.addWidget(self._e_wall); wrow.addWidget(self._btn_days); wrow.addStretch()
        ww = QWidget(); ww.setLayout(wrow); left.addWidget(ww, 3, 1)

        # Timecode
        self._chk_tc = QCheckBox()
        self._e_tc = QLineEdit(); self._e_tc.setFixedWidth(140); self._e_tc.setPlaceholderText("HH:MM:SS:FF")
        left.addWidget(QLabel("Timecode Trigger:"), 4, 0)
        trow = QHBoxLayout(); trow.setSpacing(4)
        trow.addWidget(self._chk_tc); trow.addWidget(self._e_tc); trow.addStretch()
        tw = QWidget(); tw.setLayout(trow); left.addWidget(tw, 4, 1)

        note = QLabel("MIDI / Timecode: la captura escucha en fase 2 (los valores se guardan).")
        note.setStyleSheet("color:#555; font-size:9px;")
        left.addWidget(note, 5, 0, 1, 2)
        outer.addLayout(left, 1)

        # ── Derecha: al iniciar la acción de este cue ─────────────────
        right = QVBoxLayout(); right.setSpacing(8)
        right.addWidget(QLabel("When starting the action of this cue:"))

        fo = QHBoxLayout(); fo.setSpacing(6)
        self._chk_fadeothers = QCheckBox("Fade & stop")
        self._c_foscope = QComboBox()
        for k in FADE_SCOPES:
            self._c_foscope.addItem(_SCOPE_LABELS[k], k)
        self._c_foscope.setFixedWidth(90)
        self._t_fotime = TimeEdit(width=80)
        fo.addWidget(self._chk_fadeothers); fo.addWidget(self._c_foscope)
        fo.addWidget(QLabel("over time:")); fo.addWidget(self._t_fotime); fo.addStretch()
        right.addLayout(fo)

        self._chk_duck = QCheckBox("Duck/boost audio of other cues in this list while running")
        right.addWidget(self._chk_duck)
        dk = QHBoxLayout(); dk.setSpacing(6)
        self._s_duck_level = self._spin(-60, 24, " dB", 1, 70, decimals=1)
        self._t_ducktime = TimeEdit(width=80)
        dk.addWidget(QLabel("Duck by:")); dk.addWidget(self._s_duck_level)
        dk.addWidget(QLabel("Over time:")); dk.addWidget(self._t_ducktime); dk.addStretch()
        right.addLayout(dk)

        sr = QHBoxLayout(); sr.setSpacing(6)
        sr.addWidget(QLabel("If running, a second trigger:"))
        self._c_retrig = QComboBox()
        for k in RETRIGGER_MODES:
            self._c_retrig.addItem(_RETRIG_LABELS[k], k)
        self._c_retrig.setFixedWidth(180)
        sr.addWidget(self._c_retrig); sr.addStretch()
        right.addLayout(sr)

        self._chk_release = QCheckBox("Second trigger on release. (Hotkey, MIDI Note)")
        right.addWidget(self._chk_release)
        right.addStretch()
        outer.addLayout(right, 1)

        # Conexiones
        self._chk_hotkey.toggled.connect(lambda v: self._on_hotkey_enable(bool(v)))
        self._e_hotkey.editingFinished.connect(lambda: self._on_hotkey_edit())
        self._chk_midi.toggled.connect(lambda v: self._emit("midi_enabled", bool(v)))
        self._c_midi_type.currentIndexChanged.connect(lambda: self._emit("midi_type", self._c_midi_type.currentData()))
        self._s_midi_note.valueChanged.connect(lambda v: self._emit("midi_note", int(v)))
        self._s_midi_vel.valueChanged.connect(lambda v: self._emit("midi_velocity", int(v)))
        self._s_midi_chan.valueChanged.connect(lambda v: self._emit("midi_channel", int(v)))
        self._chk_wall.toggled.connect(lambda v: self._emit("wallclock_enabled", bool(v)))
        self._e_wall.editingFinished.connect(lambda: self._emit("wallclock_time", self._e_wall.text().strip()))
        self._chk_tc.toggled.connect(lambda v: self._emit("timecode_enabled", bool(v)))
        self._e_tc.editingFinished.connect(lambda: self._emit("timecode_value", self._e_tc.text().strip()))
        self._chk_fadeothers.toggled.connect(lambda v: self._emit("fade_others", bool(v)))
        self._c_foscope.currentIndexChanged.connect(lambda: self._emit("fade_others_scope", self._c_foscope.currentData()))
        self._t_fotime.valueChanged.connect(lambda v: self._emit("fade_others_time", v))
        self._chk_duck.toggled.connect(lambda v: self._emit("duck_enabled", bool(v)))
        self._s_duck_level.valueChanged.connect(lambda v: self._emit("duck_level", float(v)))
        self._t_ducktime.valueChanged.connect(lambda v: self._emit("duck_time", v))
        self._c_retrig.currentIndexChanged.connect(lambda: self._emit("retrigger", self._c_retrig.currentData()))
        self._chk_release.toggled.connect(lambda v: self._emit("second_trigger_on_release", bool(v)))
        return w

    def _on_hotkey_enable(self, enabled: bool) -> None:
        self._e_hotkey.setEnabled(enabled)
        if not enabled:
            self._emit("hotkey", "")
        else:
            self._on_hotkey_edit()

    def _on_hotkey_edit(self) -> None:
        if self._chk_hotkey.isChecked():
            self._emit("hotkey", self._e_hotkey.keySequence().toString())

    # ==================================================================
    # Pestaña Audio Levels (volumen + fundidos)
    # ==================================================================

    def _build_audio_levels(self):
        w = QWidget()
        outer = QHBoxLayout(w)
        outer.setContentsMargins(12, 8, 12, 8)
        outer.setSpacing(24)

        # ── Tira de canal: fader vertical + VU + lectura ─────────────────
        strip = QVBoxLayout(); strip.setSpacing(4)
        strip.addWidget(QLabel("VOLUME"), 0, Qt.AlignmentFlag.AlignHCenter)
        fv = QHBoxLayout(); fv.setSpacing(8)
        self._fader = QSlider(Qt.Orientation.Vertical)
        self._fader.setRange(0, 100)
        self._fader.setFixedHeight(140)
        self._fader.setStyleSheet(
            "QSlider::groove:vertical{width:6px;background:#333;border-radius:3px;}"
            "QSlider::handle:vertical{height:16px;background:#e8a200;border-radius:3px;margin:0 -6px;}"
            "QSlider::sub-page:vertical{background:#333;}"
            "QSlider::add-page:vertical{background:#e8a200;border-radius:3px;}")
        self._vu = VerticalVu()
        self._vu.setFixedHeight(140)
        fv.addWidget(self._fader); fv.addWidget(self._vu)
        fvw = QWidget(); fvw.setLayout(fv)
        strip.addWidget(fvw, 0, Qt.AlignmentFlag.AlignHCenter)
        self._lbl_vol = QLabel("100 %")
        self._lbl_vol.setStyleSheet("color:#e0e0e0; font-size:11px;")
        strip.addWidget(self._lbl_vol, 0, Qt.AlignmentFlag.AlignHCenter)
        sw = QWidget(); sw.setLayout(strip)
        outer.addWidget(sw)

        # ── Fundidos ─────────────────────────────────────────────────────
        fades = QFormLayout(); fades.setVerticalSpacing(8); fades.setHorizontalSpacing(10)
        self._t_fin = TimeEdit()
        self._t_fout = TimeEdit()
        fades.addRow("Fade in:", self._t_fin)
        fades.addRow("Fade out:", self._t_fout)
        note = QLabel("Matriz de canales y curvas de fundido: fase 2.")
        note.setStyleSheet("color:#555; font-size:9px;")
        fades.addRow("", note)
        fw = QWidget(); fw.setLayout(fades)
        outer.addWidget(fw, 1)

        self._fader.valueChanged.connect(self._on_fader)
        self._t_fin.valueChanged.connect(lambda v: self._emit("fade_in", v))
        self._t_fout.valueChanged.connect(lambda v: self._emit("fade_out", v))
        return w

    def _on_fader(self, v):
        self._lbl_vol.setText(f"{int(v)} %")
        self._emit("volume", float(v) / 100.0)

    def set_vu_level(self, rms: float) -> None:
        """Alimenta el VU del Audio Levels (llamado por la vista)."""
        if hasattr(self, "_vu"):
            self._vu.set_level(rms)

    # ==================================================================
    # Pestaña Display & Geometry (solo vídeo)
    # ==================================================================

    def _build_display_geometry(self):
        w = QWidget()
        outer = QHBoxLayout(w)
        outer.setContentsMargins(10, 8, 10, 8)
        outer.setSpacing(16)

        form = QFormLayout()
        form.setLabelAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        form.setHorizontalSpacing(10)
        form.setVerticalSpacing(7)
        form.setFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.FieldsStayAtSizeHint)

        def hbox(*ws, spacing=6):
            box = QHBoxLayout(); box.setContentsMargins(0, 0, 0, 0); box.setSpacing(spacing)
            for x in ws:
                box.addWidget(x) if not isinstance(x, str) else box.addWidget(QLabel(x))
            box.addStretch()
            cont = QWidget(); cont.setLayout(box); return cont

        # Video Surface + ellipsis
        self._c_surface = QComboBox(); self._c_surface.addItem("Surface 1 (salida)", 0)
        self._c_surface.setMinimumWidth(180)
        btn_ell = QPushButton("…"); btn_ell.setFixedWidth(30); btn_ell.setEnabled(False)
        form.addRow("Video Surface:", hbox(self._c_surface, btn_ell))

        # Mode
        self._c_geomode = QComboBox()
        self._c_geomode.addItem("Full Surface", "FULL")
        self._c_geomode.addItem("Custom Geometry", "CUSTOM")
        self._c_geomode.setMinimumWidth(150)
        form.addRow("Mode:", self._c_geomode)

        # Layer + Opacity (misma fila, bien separados)
        self._c_layer = QComboBox(); self._c_layer.setEditable(True)
        for lv in ("top", "999", "500", "100", "10", "1", "bottom"):
            self._c_layer.addItem(lv)
        self._c_layer.setFixedWidth(84)
        self._s_opacity = self._spin(0, 100, " %", 5, 74)
        form.addRow("Layer / Opacity:", hbox(self._c_layer, QLabel("  Opacity:"), self._s_opacity))

        # Preserve aspect (solo Full)
        self._chk_pasp = QCheckBox("Preserve Aspect Ratio")
        self._row_pasp = self._chk_pasp
        form.addRow("", self._chk_pasp)

        # ── Custom Geometry ──
        self._s_tx = self._spin(-9999, 9999, "", 10, 74, decimals=0)
        self._s_ty = self._spin(-9999, 9999, "", 10, 74, decimals=0)
        self._lbl_tr = QLabel("Translation:")
        self._w_tr = hbox("X", self._s_tx, "  Y", self._s_ty)
        form.addRow(self._lbl_tr, self._w_tr)

        self._s_sx = self._spin(0.01, 100, "", 0.1, 68, decimals=2)
        self._s_sy = self._spin(0.01, 100, "", 0.1, 68, decimals=2)
        self._btn_lock = QPushButton("🔒"); self._btn_lock.setFixedWidth(30); self._btn_lock.setCheckable(True); self._btn_lock.setChecked(True)
        self._btn_lock.setToolTip("Bloquear proporción X/Y")
        self._lbl_sc = QLabel("Scale:")
        self._w_sc = hbox("X", self._s_sx, self._btn_lock, "Y", self._s_sy)
        form.addRow(self._lbl_sc, self._w_sc)

        self._s_ax = self._spin(-9999, 9999, "", 10, 74, decimals=0)
        self._s_ay = self._spin(-9999, 9999, "", 10, 74, decimals=0)
        self._lbl_an = QLabel("Anchor:")
        self._w_an = hbox("X", self._s_ax, "  Y", self._s_ay)
        form.addRow(self._lbl_an, self._w_an)

        self._s_rz = self._spin(-360, 360, "°", 15, 74, decimals=0)
        self._btn_rreset = QPushButton("Reset"); self._btn_rreset.setFixedWidth(58)
        self._lbl_rot = QLabel("Rotation Z:")
        self._w_rot = hbox(self._s_rz, self._btn_rreset)
        form.addRow(self._lbl_rot, self._w_rot)

        note = QLabel("Opacity · Layers · rotación 3D · varias surfaces · efectos → fase 2 (compositor)")
        note.setStyleSheet("color:#555; font-size:9px;")
        note.setWordWrap(True)
        form.addRow("", note)

        fw = QWidget(); fw.setLayout(form); fw.setMinimumWidth(360)
        outer.addWidget(fw)

        # Preview
        self._geo_preview = GeometryPreview()
        outer.addWidget(self._geo_preview, 1)

        # Conexiones
        self._c_geomode.currentIndexChanged.connect(self._on_geomode)
        self._c_layer.currentTextChanged.connect(lambda t: self._emit("layer", t))
        self._s_opacity.valueChanged.connect(lambda v: (self._emit("opacity", float(v)), self._sync_geo_preview()))
        self._chk_pasp.toggled.connect(lambda v: (self._emit("preserve_aspect", bool(v)), self._sync_geo_preview()))
        self._s_tx.valueChanged.connect(lambda v: (self._emit("translate_x", float(v)), self._sync_geo_preview()))
        self._s_ty.valueChanged.connect(lambda v: (self._emit("translate_y", float(v)), self._sync_geo_preview()))
        self._s_sx.valueChanged.connect(self._on_scale_x)
        self._s_sy.valueChanged.connect(self._on_scale_y)
        self._s_ax.valueChanged.connect(lambda v: self._emit("anchor_x", float(v)))
        self._s_ay.valueChanged.connect(lambda v: self._emit("anchor_y", float(v)))
        self._s_rz.valueChanged.connect(lambda v: (self._emit("rotate_z", float(v)), self._sync_geo_preview()))
        self._btn_rreset.clicked.connect(self._on_rot_reset)
        self._geo_preview.geometry_changed.connect(self._on_preview_geo)
        return w

    def _on_geomode(self):
        mode = self._c_geomode.currentData()
        custom = (mode == "CUSTOM")
        for wdg in (self._lbl_tr, self._w_tr, self._lbl_sc, self._w_sc,
                    self._lbl_an, self._w_an, self._lbl_rot, self._w_rot):
            wdg.setVisible(custom)
        self._chk_pasp.setVisible(not custom)
        self._emit("geo_mode", mode)
        self._sync_geo_preview()

    def _on_scale_x(self, v):
        if self._btn_lock.isChecked() and not self._loading:
            self._loading = True
            self._s_sy.setValue(v)
            self._loading = False
            self._emit("scale_y", float(v))
        self._emit("scale_x", float(v))
        self._sync_geo_preview()

    def _on_scale_y(self, v):
        if self._btn_lock.isChecked() and not self._loading:
            self._loading = True
            self._s_sx.setValue(v)
            self._loading = False
            self._emit("scale_x", float(v))
        self._emit("scale_y", float(v))
        self._sync_geo_preview()

    def _on_rot_reset(self):
        self._s_rz.setValue(0)
        self._emit("rotate_z", 0.0)
        self._sync_geo_preview()

    def _on_preview_geo(self, fields: dict):
        # Arrastre/scroll en el preview → actualizar spinboxes + modelo
        self._loading = True
        if "translate_x" in fields: self._s_tx.setValue(fields["translate_x"])
        if "translate_y" in fields: self._s_ty.setValue(fields["translate_y"])
        if "scale_x" in fields: self._s_sx.setValue(fields["scale_x"])
        if "scale_y" in fields: self._s_sy.setValue(fields["scale_y"])
        self._loading = False
        self.changed.emit(fields)

    def _sync_geo_preview(self):
        self._geo_preview.set_geometry(
            self._c_geomode.currentData(), self._s_tx.value(), self._s_ty.value(),
            self._s_sx.value(), self._s_sy.value(), self._s_rz.value(),
            self._chk_pasp.isChecked(), self._s_opacity.value())

    # ==================================================================
    # Pestaña Time & Loops
    # ==================================================================

    def _build_timeloops(self):
        w = QWidget()
        outer = QHBoxLayout(w)
        outer.setContentsMargins(8, 8, 8, 8)
        outer.setSpacing(14)

        left = QFormLayout(); left.setVerticalSpacing(6); left.setHorizontalSpacing(8)
        left.setLabelAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self._t_start = TimeEdit()
        self._t_end = TimeEdit()
        left.addRow("Start time:", self._t_start)
        left.addRow("End time:", self._t_end)

        self._rb_count = QRadioButton("Play count:")
        self._rb_inf = QRadioButton("Infinite loop  ∞")
        self._grp_loop = QButtonGroup(w)
        self._grp_loop.addButton(self._rb_count); self._grp_loop.addButton(self._rb_inf)
        self._s_count = self._spin(1, 9999, "", 1, 70)
        cnt = QHBoxLayout(); cnt.setSpacing(6); cnt.addWidget(self._rb_count); cnt.addWidget(self._s_count)
        cntw = QWidget(); cntw.setLayout(cnt)
        left.addRow("", cntw)
        left.addRow("", self._rb_inf)
        lw = QWidget(); lw.setLayout(left); lw.setFixedWidth(240)
        outer.addWidget(lw)

        # Centro: forma de onda
        self._waveform = WaveformWidget()
        outer.addWidget(self._waveform, 1)

        # Derecha: rate / preserve pitch
        right = QVBoxLayout(); right.setSpacing(8)
        rr = QHBoxLayout(); rr.setSpacing(6)
        self._s_rate = self._spin(0.1, 8.0, "", 0.1, 70, decimals=2)
        rr.addWidget(QLabel("Rate:")); rr.addWidget(self._s_rate); rr.addStretch()
        right.addLayout(rr)
        self._chk_preserve = QCheckBox("Preserve pitch")
        right.addWidget(self._chk_preserve)
        right.addStretch()
        outer.addLayout(right)

        # Conexiones
        self._t_start.valueChanged.connect(lambda v: (self._emit("trim_start", v), self._waveform.set_markers(v, self._t_end.value())))
        self._t_end.valueChanged.connect(lambda v: (self._emit("trim_end", v), self._waveform.set_markers(self._t_start.value(), v)))
        self._rb_count.toggled.connect(self._on_loop_mode)
        self._s_count.valueChanged.connect(lambda v: self._emit("play_count", int(v)))
        self._s_rate.valueChanged.connect(lambda v: self._emit("rate", float(v)))
        self._chk_preserve.toggled.connect(lambda v: self._emit("preserve_pitch", bool(v)))
        # Arrastrar los marcadores de la onda actualiza trim
        self._waveform.markers_changed.connect(self._on_wave_markers)
        return w

    def _on_loop_mode(self, count_selected: bool) -> None:
        if self._loading:
            return
        # count seleccionado → loop False (play_count veces); infinite → loop True
        self._emit("loop", not count_selected)

    def _on_wave_markers(self, start: float, end: float) -> None:
        self._loading = True
        self._t_start.setValue(start)
        self._t_end.setValue(end)
        self._loading = False
        self.changed.emit({"trim_start": start, "trim_end": end})

    # ==================================================================
    # Carga de un cue
    # ==================================================================

    def set_cue(self, cue: Optional[Cue], all_cues: list, selection_count: int = 1) -> None:
        self._loading = True
        try:
            enabled = cue is not None
            self._tabs.setEnabled(enabled)
            self._current_id = cue.id if cue else None
            if not enabled:
                self._lbl_count.setText("sin selección")
                return
            self._lbl_count.setText(
                f"{selection_count} cue seleccionado" if selection_count == 1
                else f"{selection_count} cues seleccionados")

            # Basics
            self._e_number.setText(cue.display_number)
            self._t_dur.setValue(cue.duration)
            dur_editable = cue.type in ("AUDIO", "VIDEO", "WAIT", "FADE")
            self._t_dur.setReadOnly(not dur_editable)
            self._t_pre.setValue(cue.pre_wait)
            self._t_post.setValue(cue.post_wait)
            i = self._c_cont.findData(cue.continue_mode); self._c_cont.setCurrentIndex(max(0, i))
            self._e_name.setText(cue.name)
            self._colors.setColor(cue.color)

            # Target
            is_target = cue.type in ("FADE", "START", "STOP")
            is_media = cue.type in ("AUDIO", "VIDEO")
            self._tgt_widget.setVisible(is_target or is_media)
            self._c_target.blockSignals(True)
            self._c_target.clear()
            self._c_target.addItem("— ninguno / todos —", None)
            for c in all_cues:
                if c.id == cue.id:
                    continue
                self._c_target.addItem(f"[{c.display_number}] {c.name}", c.id)
            ti = self._c_target.findData(cue.target_id)
            self._c_target.setCurrentIndex(ti if ti >= 0 else 0)
            self._c_target.blockSignals(False)
            self._c_target.setVisible(is_target)
            self._e_targetfile.setVisible(is_media)
            self._e_targetfile.setText(cue.file_path)
            self._btn_goto.setVisible(is_target or is_media)

            self._chk_flag.setChecked(cue.flagged)
            self._chk_autoload.setChecked(cue.auto_load)
            self._chk_armed.setChecked(cue.armed)
            is_group = cue.type == "GROUP"
            self._lbl_group.setVisible(is_group); self._c_group.setVisible(is_group)
            gi = self._c_group.findData(cue.group_mode); self._c_group.setCurrentIndex(max(0, gi))
            self._e_notes.setPlainText(cue.notes)

            # Triggers
            self._chk_hotkey.setChecked(bool(cue.hotkey))
            self._e_hotkey.setKeySequence(cue.hotkey)
            self._e_hotkey.setEnabled(bool(cue.hotkey))
            self._chk_midi.setChecked(cue.midi_enabled)
            mi = self._c_midi_type.findData(cue.midi_type); self._c_midi_type.setCurrentIndex(max(0, mi))
            self._s_midi_note.setValue(cue.midi_note)
            self._s_midi_vel.setValue(cue.midi_velocity)
            self._s_midi_chan.setValue(cue.midi_channel)
            self._chk_wall.setChecked(cue.wallclock_enabled)
            self._e_wall.setText(cue.wallclock_time)
            self._chk_tc.setChecked(cue.timecode_enabled)
            self._e_tc.setText(cue.timecode_value)
            self._chk_fadeothers.setChecked(cue.fade_others)
            fi = self._c_foscope.findData(cue.fade_others_scope); self._c_foscope.setCurrentIndex(max(0, fi))
            self._t_fotime.setValue(cue.fade_others_time)
            self._chk_duck.setChecked(cue.duck_enabled)
            self._s_duck_level.setValue(cue.duck_level)
            self._t_ducktime.setValue(cue.duck_time)
            ri = self._c_retrig.findData(cue.retrigger); self._c_retrig.setCurrentIndex(max(0, ri))
            self._chk_release.setChecked(cue.second_trigger_on_release)

            # Time & Loops
            self._t_start.setValue(cue.trim_start)
            self._t_end.setValue(cue.trim_end)
            if cue.loop:
                self._rb_inf.setChecked(True)
            else:
                self._rb_count.setChecked(True)
            self._s_count.setValue(cue.play_count)
            self._s_rate.setValue(cue.rate)
            self._chk_preserve.setChecked(cue.preserve_pitch)
            from media_import import IMAGE_FORMATS
            from pathlib import Path as _Path
            is_image = _Path(cue.file_path).suffix.lower() in IMAGE_FORMATS
            has_tl = is_media and bool(cue.file_path) and not is_image
            self._tabs.setTabVisible(self._idx_tl, has_tl)
            if has_tl:
                if cue.type == "VIDEO":
                    self._waveform.load_video(cue.file_path, cue.trim_start, cue.trim_end)
                else:
                    self._waveform.load(cue.file_path, cue.trim_start, cue.trim_end)
            else:
                self._waveform.clear()

            # Display & Geometry (solo vídeo, no imagen fija)
            is_video = cue.type == "VIDEO"
            self._tabs.setTabVisible(self._idx_geo, is_video)
            if is_video:
                gi = self._c_geomode.findData(cue.geo_mode); self._c_geomode.setCurrentIndex(max(0, gi))
                self._c_layer.setCurrentText(cue.layer)
                self._s_opacity.setValue(cue.opacity)
                self._chk_pasp.setChecked(cue.preserve_aspect)
                self._s_tx.setValue(cue.translate_x); self._s_ty.setValue(cue.translate_y)
                self._s_sx.setValue(cue.scale_x); self._s_sy.setValue(cue.scale_y)
                self._s_ax.setValue(cue.anchor_x); self._s_ay.setValue(cue.anchor_y)
                self._s_rz.setValue(cue.rotate_z)
                self._on_geomode()   # ajusta visibilidad Full/Custom
                if cue.file_path and not is_image:
                    self._geo_preview.load(cue.file_path)
                else:
                    self._geo_preview.clear()
                self._sync_geo_preview()

            # Audio Levels (volumen + fundidos) para audio y vídeo
            self._tabs.setTabVisible(self._idx_audio, is_media)
            if is_media:
                vpct = int(round(cue.volume * 100.0))
                self._fader.setValue(vpct)
                self._lbl_vol.setText(f"{vpct} %")
                self._t_fin.setValue(cue.fade_in)
                self._t_fout.setValue(cue.fade_out)
        finally:
            self._loading = False

    # ==================================================================
    # Acciones
    # ==================================================================

    def _on_goto(self) -> None:
        tid = self._c_target.currentData()
        if tid is not None:
            self.goto_target.emit(int(tid))
