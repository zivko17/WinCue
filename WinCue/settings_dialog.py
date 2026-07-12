# =============================================================================
# settings_dialog.py — Diálogo de configuración de la aplicación
# =============================================================================
#
# QDialog con 5 pestañas: Audio, Vídeo, Red/Control, Interfaz, Rutas.
# Lee y escribe a través de SettingsManager.instance().
# Emite settings_saved() al confirmar para que los engines puedan
# reconfigurarse sin reiniciar la app.
# =============================================================================

from __future__ import annotations

from pathlib import Path
from typing import Optional

from PySide6.QtCore import Qt, QObject, QRegularExpression, QThread, QTimer, Signal
from PySide6.QtGui import QRegularExpressionValidator
from PySide6.QtWidgets import (
    QApplication,
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QFormLayout,
    QFrame,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QScrollArea,
    QSlider,
    QSpinBox,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from audio_engine import AudioEngine
from settings_manager import DEFAULTS, SettingsManager

import socket as _socket

# ---------------------------------------------------------------------------
# MIDI — dependencia opcional (mido + python-rtmidi)
# ---------------------------------------------------------------------------

try:
    import mido as _mido
    _MIDO_AVAILABLE: bool = True
    _MIDO_ERROR: str = ""
except ImportError:
    _mido = None  # type: ignore[assignment]
    _MIDO_AVAILABLE = False
    _MIDO_ERROR = (
        "mido no está instalado. Ejecuta:\n"
        "  pip install mido python-rtmidi"
    )


def _midi_note_name(note: int) -> str:
    """Convierte un número de nota MIDI (0-127) en nombre: 60 → 'C4'."""
    names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
    return f"{names[note % 12]}{(note // 12) - 1}"


def _build_osc_ping() -> bytes:
    """Construye un mensaje OSC /test/ping mínimo (sin argumentos).

    Formato OSC:  address string (null-padded a 4 bytes) + type tag string ',\\0'.
    """
    def _pad4(b: bytes) -> bytes:
        rem = len(b) % 4
        return b + (b"\x00" * ((4 - rem) % 4))

    return _pad4(b"/test/ping\x00") + _pad4(b",\x00")


# ---------------------------------------------------------------------------
# Trabajador OSC — corre en QThread, no bloquea la UI
# ---------------------------------------------------------------------------

class _OscTester(QObject):
    result: Signal = Signal(str)

    def __init__(self, host: str, port: int) -> None:
        super().__init__()
        self._host = host
        self._port = port

    def run(self) -> None:
        try:
            msg = _build_osc_ping()
            sock = _socket.socket(_socket.AF_INET, _socket.SOCK_DGRAM)
            sock.settimeout(1.0)
            try:
                sock.sendto(msg, (self._host, self._port))
                try:
                    _, addr = sock.recvfrom(256)
                    self.result.emit(f"✓ Respuesta de {addr[0]}:{addr[1]}")
                except _socket.timeout:
                    self.result.emit("Sin respuesta en 1 s  (ping enviado correctamente)")
            finally:
                sock.close()
        except Exception as exc:
            self.result.emit(f"Error: {exc}")


# ---------------------------------------------------------------------------
# Trabajador MIDI learn — corre en QThread, escucha nota durante 5 s
# ---------------------------------------------------------------------------

class _MidiLearnWorker(QObject):
    note_received: Signal = Signal(int)
    timed_out:     Signal = Signal()

    def __init__(self, port_name: str) -> None:
        super().__init__()
        self._port_name = port_name
        self._done = False

    def run(self) -> None:
        import time
        try:
            def _on_msg(msg: object) -> None:  # type: ignore[type-arg]
                if (
                    getattr(msg, "type", "") == "note_on"
                    and getattr(msg, "velocity", 0) > 0
                    and not self._done
                ):
                    self._done = True
                    self.note_received.emit(msg.note)  # type: ignore[attr-defined]

            with _mido.open_input(self._port_name, callback=_on_msg):
                deadline = time.monotonic() + 5.0
                while not self._done and time.monotonic() < deadline:
                    time.sleep(0.05)

            if not self._done:
                self.timed_out.emit()
        except Exception:
            self.timed_out.emit()

# ---------------------------------------------------------------------------
# Estilos internos (dark theme)
# ---------------------------------------------------------------------------

_STYLE_DIALOG = """
QDialog, QWidget {
    background-color: #1a1a1a;
    color: #dddddd;
    font-family: "Consolas", "Courier New", monospace;
    font-size: 12px;
}
QTabWidget::pane {
    border: 1px solid #333333;
    background-color: #1e1e1e;
}
QTabBar::tab {
    background-color: #111111;
    color: #888888;
    padding: 6px 16px;
    border: 1px solid #333333;
    border-bottom: none;
    min-width: 80px;
}
QTabBar::tab:selected {
    background-color: #1e1e1e;
    color: #e8a200;
    border-bottom: 1px solid #1e1e1e;
}
QGroupBox {
    border: 1px solid #333333;
    border-radius: 4px;
    margin-top: 8px;
    padding-top: 8px;
    color: #aaaaaa;
    font-size: 11px;
    font-weight: bold;
    letter-spacing: 1px;
}
QGroupBox::title {
    subcontrol-origin: margin;
    left: 8px;
    padding: 0 4px;
}
QComboBox, QSpinBox, QLineEdit {
    background-color: #2a2a2a;
    border: 1px solid #444444;
    border-radius: 3px;
    padding: 3px 6px;
    color: #dddddd;
    min-height: 22px;
}
QComboBox:focus, QSpinBox:focus, QLineEdit:focus {
    border-color: #e8a200;
}
QComboBox::drop-down { border: none; }
QComboBox::down-arrow { width: 10px; }
QSlider::groove:horizontal {
    height: 4px;
    background-color: #333333;
    border-radius: 2px;
}
QSlider::handle:horizontal {
    width: 14px;
    height: 14px;
    margin: -5px 0;
    background-color: #e8a200;
    border-radius: 7px;
}
QSlider::sub-page:horizontal {
    background-color: #e8a200;
    border-radius: 2px;
}
QCheckBox::indicator {
    width: 14px; height: 14px;
    border: 1px solid #555555;
    border-radius: 2px;
    background-color: #2a2a2a;
}
QCheckBox::indicator:checked {
    background-color: #e8a200;
    border-color: #e8a200;
}
QPushButton {
    background-color: #2a2a2a;
    border: 1px solid #444444;
    border-radius: 3px;
    padding: 4px 12px;
    color: #dddddd;
    min-height: 26px;
}
QPushButton:hover  { background-color: #333333; border-color: #666666; }
QPushButton:pressed { background-color: #222222; }
"""

_STYLE_ASIO_WARN = (
    "background-color: #3a2a00; color: #e8a200; border: 1px solid #5a4000; "
    "border-radius: 3px; padding: 6px 8px; font-size: 11px;"
)
_STYLE_BADGE_ASIO  = (
    "background-color: #1a3a1a; color: #81c784; border-radius: 3px; "
    "padding: 2px 6px; font-size: 10px; font-weight: bold;"
)
_STYLE_BADGE_OTHER = (
    "background-color: #2a2a2a; color: #888888; border-radius: 3px; "
    "padding: 2px 6px; font-size: 10px;"
)


# ---------------------------------------------------------------------------
# SettingsDialog
# ---------------------------------------------------------------------------

class SettingsDialog(QDialog):
    """Diálogo de configuración con 5 pestañas."""

    settings_saved: Signal = Signal()

    def __init__(self, parent: Optional[QWidget] = None) -> None:
        super().__init__(parent)
        self.setWindowTitle("Configuración")
        self.setMinimumSize(620, 520)
        self.resize(680, 560)
        self.setStyleSheet(_STYLE_DIALOG)
        self.setWindowModality(Qt.WindowModality.ApplicationModal)

        self._sm = SettingsManager.instance()

        # Mantener referencias a los widgets para leer valores al guardar
        self._widgets: dict[str, QWidget] = {}

        self._build_ui()
        self._load_values()

    # ------------------------------------------------------------------
    # Layout principal
    # ------------------------------------------------------------------

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(12, 12, 12, 12)
        root.setSpacing(8)

        self._tabs = QTabWidget()
        self._tabs.addTab(self._build_tab_audio(),     "Audio")
        self._tabs.addTab(self._build_tab_video(),     "Vídeo")
        self._tabs.addTab(self._build_tab_network(),   "Red / Control")
        self._tabs.addTab(self._build_tab_interface(), "Interfaz")
        self._tabs.addTab(self._build_tab_paths(),     "Rutas")
        root.addWidget(self._tabs, stretch=1)

        # ── Botones inferiores ────────────────────────────────────────
        btn_row = QHBoxLayout()
        btn_row.setSpacing(6)

        btn_defaults = QPushButton("Restaurar defaults")
        btn_defaults.setToolTip("Restablecer todos los valores de fábrica")
        btn_defaults.clicked.connect(self._on_reset_defaults)
        btn_row.addWidget(btn_defaults)

        btn_row.addStretch()

        btn_cancel = QPushButton("Cancelar")
        btn_cancel.clicked.connect(self.reject)
        btn_row.addWidget(btn_cancel)

        btn_save = QPushButton("Guardar")
        btn_save.setDefault(True)
        btn_save.setStyleSheet(
            "QPushButton { background-color: #1a3a1a; color: #81c784; "
            "border-color: #2a5a2a; font-weight: bold; }"
            "QPushButton:hover { background-color: #254a25; }"
        )
        btn_save.clicked.connect(self._on_save)
        btn_row.addWidget(btn_save)

        root.addLayout(btn_row)

    # ------------------------------------------------------------------
    # Pestaña 1 — Audio
    # ------------------------------------------------------------------

    def _build_tab_audio(self) -> QWidget:
        scroll = _make_scroll()
        form = QVBoxLayout(scroll.widget())
        form.setSpacing(10)

        # ── Sección: Dispositivo de salida ─────────────────────────────
        grp_dev = QGroupBox("DISPOSITIVO DE SALIDA")
        g_lo = QFormLayout(grp_dev)
        g_lo.setSpacing(6)

        # Combo de dispositivos + badge ASIO/WASAPI
        dev_row = QHBoxLayout()
        dev_row.setSpacing(6)

        combo_dev = QComboBox()
        combo_dev.setMinimumWidth(280)
        self._widgets["audio_device"] = combo_dev
        dev_row.addWidget(combo_dev, stretch=1)

        lbl_badge = QLabel("WASAPI")
        lbl_badge.setStyleSheet(_STYLE_BADGE_OTHER)
        lbl_badge.setFixedHeight(22)
        self._widgets["_audio_badge"] = lbl_badge
        dev_row.addWidget(lbl_badge)

        dev_container = QWidget()
        dev_container.setLayout(dev_row)
        g_lo.addRow("Dispositivo:", dev_container)

        # Poblar combo con AudioEngine.list_devices()
        self._audio_devices: list[dict] = AudioEngine.list_devices()
        combo_dev.addItem("Sistema (por defecto)", userData=-1)
        for dev in self._audio_devices:
            label = f"{dev['name']}  [{dev['hostapi_name']}]"
            combo_dev.addItem(label, userData=dev["index"])

        combo_dev.currentIndexChanged.connect(self._on_audio_device_changed)

        # Driver
        combo_driver = QComboBox()
        combo_driver.addItems(["WASAPI", "WASAPI Exclusive", "ASIO", "DirectSound"])
        self._widgets["audio_driver"] = combo_driver
        g_lo.addRow("Driver:", combo_driver)
        combo_driver.currentTextChanged.connect(self._on_driver_changed)

        # Aviso ASIO
        self._lbl_asio_warn = QLabel(
            "ASIO requiere que no haya otras aplicaciones usando el dispositivo.\n"
            "Cierra DAWs antes del evento."
        )
        self._lbl_asio_warn.setStyleSheet(_STYLE_ASIO_WARN)
        self._lbl_asio_warn.setWordWrap(True)
        self._lbl_asio_warn.setVisible(False)
        g_lo.addRow("", self._lbl_asio_warn)

        form.addWidget(grp_dev)

        # ── Sección: Calidad ────────────────────────────────────────────
        grp_q = QGroupBox("CALIDAD")
        q_lo = QFormLayout(grp_q)
        q_lo.setSpacing(6)

        # Sample rate
        combo_sr = QComboBox()
        for sr in (44100, 48000, 96000):
            combo_sr.addItem(f"{sr} Hz", userData=sr)
        self._widgets["audio_sample_rate"] = combo_sr

        # Buffer size + etiqueta de latencia en tiempo real
        buf_row = QHBoxLayout()
        buf_row.setSpacing(8)
        combo_buf = QComboBox()
        for bs in (128, 256, 512, 1024, 2048):
            combo_buf.addItem(str(bs), userData=bs)
        self._widgets["audio_buffer_size"] = combo_buf
        buf_row.addWidget(combo_buf)

        self._lbl_latency = QLabel("≈ 10.7 ms")
        self._lbl_latency.setStyleSheet("color: #888888; font-size: 11px;")
        buf_row.addWidget(self._lbl_latency)
        buf_row.addStretch()

        buf_container = QWidget()
        buf_container.setLayout(buf_row)

        combo_sr.currentIndexChanged.connect(self._update_latency_label)
        combo_buf.currentIndexChanged.connect(self._update_latency_label)

        q_lo.addRow("Sample Rate:", combo_sr)
        q_lo.addRow("Buffer Size:", buf_container)

        # Master volume
        vol_row = QHBoxLayout()
        vol_row.setSpacing(8)
        slider_vol = QSlider(Qt.Orientation.Horizontal)
        slider_vol.setRange(0, 100)
        slider_vol.setFixedWidth(200)
        self._widgets["master_volume"] = slider_vol
        lbl_vol = QLabel("100")
        lbl_vol.setFixedWidth(28)
        lbl_vol.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        slider_vol.valueChanged.connect(lambda v: lbl_vol.setText(str(v)))
        vol_row.addWidget(slider_vol)
        vol_row.addWidget(lbl_vol)
        vol_row.addStretch()

        vol_container = QWidget()
        vol_container.setLayout(vol_row)
        q_lo.addRow("Volumen master:", vol_container)

        form.addWidget(grp_q)

        # ── Sección: Pre-roll ───────────────────────────────────────────
        grp_pre = QGroupBox("PRE-ROLL")
        p_lo = QFormLayout(grp_pre)
        p_lo.setSpacing(6)

        spin_pre = QSpinBox()
        spin_pre.setRange(0, 500)
        spin_pre.setSuffix(" ms")
        spin_pre.setFixedWidth(90)
        self._widgets["pre_roll_ms"] = spin_pre
        p_lo.addRow("Silencio antes de reproducir:", spin_pre)

        form.addWidget(grp_pre)
        form.addStretch()

        return scroll

    # ------------------------------------------------------------------
    # Pestaña 2 — Vídeo
    # ------------------------------------------------------------------

    def _build_tab_video(self) -> QWidget:
        scroll = _make_scroll()
        form = QVBoxLayout(scroll.widget())
        form.setSpacing(10)

        # ── Sección: Pantalla de salida ────────────────────────────────
        grp_screen = QGroupBox("PANTALLA DE SALIDA")
        s_lo = QFormLayout(grp_screen)
        s_lo.setSpacing(6)

        # Combo populado con las pantallas detectadas por Qt en tiempo real
        combo_screen = QComboBox()
        combo_screen.setMinimumWidth(300)
        self._widgets["video_output_screen"] = combo_screen

        primary = QApplication.primaryScreen()
        for idx, screen in enumerate(QApplication.screens()):
            geom = screen.geometry()
            w, h = geom.width(), geom.height()
            tag = "Principal" if screen is primary else screen.name()
            combo_screen.addItem(f"Pantalla {idx + 1}  ({w}×{h})  — {tag}", userData=idx)

        s_lo.addRow("Pantalla de salida:", combo_screen)

        chk_fs = QCheckBox("Pantalla completa automático al reproducir")
        self._widgets["video_fullscreen_output"] = chk_fs
        s_lo.addRow("", chk_fs)

        btn_preview = QPushButton("PREVISUALIZAR")
        btn_preview.setFixedWidth(130)
        btn_preview.setToolTip(
            "Muestra una ventana de prueba en la pantalla seleccionada durante 3 s"
        )
        btn_preview.setStyleSheet(
            "QPushButton { background-color: #1a2a3a; color: #64b5f6; "
            "border: 1px solid #2a4a6a; border-radius: 3px; }"
            "QPushButton:hover { background-color: #253545; }"
            "QPushButton:pressed { background-color: #111a22; }"
        )
        btn_preview.clicked.connect(self._on_preview_video_output)
        s_lo.addRow("", btn_preview)

        form.addWidget(grp_screen)

        # ── Sección: Reproducción ──────────────────────────────────────
        grp_pb = QGroupBox("REPRODUCCIÓN")
        p_lo = QFormLayout(grp_pb)
        p_lo.setSpacing(6)

        spin_fade = QSpinBox()
        spin_fade.setRange(0, 60)
        spin_fade.setSuffix(" frames")
        spin_fade.setFixedWidth(90)
        self._widgets["video_fade_frames"] = spin_fade
        p_lo.addRow("Frames de fade in/out:", spin_fade)

        chk_loop = QCheckBox("Loop por defecto en nuevos cues de vídeo")
        self._widgets["video_loop_default"] = chk_loop
        p_lo.addRow("", chk_loop)

        spin_vol = QSpinBox()
        spin_vol.setRange(0, 100)
        spin_vol.setSuffix(" %")
        spin_vol.setFixedWidth(80)
        self._widgets["video_default_volume"] = spin_vol
        p_lo.addRow("Volumen por defecto vídeo:", spin_vol)

        form.addWidget(grp_pb)
        form.addStretch()
        return scroll

    def _on_preview_video_output(self) -> None:
        """Muestra una ventana negra de test en la pantalla elegida durante 3 s."""
        combo: QComboBox = self._widgets["video_output_screen"]  # type: ignore[assignment]
        screen_idx: int = combo.currentData() or 0
        screens = QApplication.screens()
        screen = screens[screen_idx] if screen_idx < len(screens) else screens[0]

        preview = QWidget(None)
        preview.setWindowFlags(
            Qt.WindowType.Window
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
        )
        preview.setStyleSheet("background-color: #000000;")

        lbl = QLabel("SALIDA DE VÍDEO ACTIVA", preview)
        lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lbl.setStyleSheet(
            "color: #ffffff; font-size: 28px; font-weight: bold; background: transparent;"
        )

        lo = QVBoxLayout(preview)
        lo.addWidget(lbl)

        preview.setGeometry(screen.geometry())
        preview.show()

        # Mantener referencia para evitar GC antes del cierre
        self._preview_window = preview
        QTimer.singleShot(3_000, preview.close)

    # ------------------------------------------------------------------
    # Pestaña 3 — Red / Control
    # ------------------------------------------------------------------

    def _build_tab_network(self) -> QWidget:
        scroll = _make_scroll()
        form = QVBoxLayout(scroll.widget())
        form.setSpacing(10)

        # ── Sección OSC ────────────────────────────────────────────────
        grp_osc = QGroupBox("OSC (Open Sound Control)")
        o_lo = QFormLayout(grp_osc)
        o_lo.setSpacing(6)

        chk_osc = QCheckBox("Activar recepción OSC")
        self._widgets["osc_enabled"] = chk_osc
        o_lo.addRow("", chk_osc)

        spin_listen = QSpinBox()
        spin_listen.setRange(1024, 65535)
        spin_listen.setFixedWidth(90)
        self._widgets["osc_listen_port"] = spin_listen
        o_lo.addRow("Puerto de escucha:", spin_listen)

        le_host = QLineEdit()
        le_host.setPlaceholderText("127.0.0.1")
        le_host.setFixedWidth(150)
        _ip_rx = QRegularExpression(
            r"^((25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(25[0-5]|2[0-4]\d|[01]?\d\d?)$"
        )
        le_host.setValidator(QRegularExpressionValidator(_ip_rx, le_host))
        self._widgets["osc_send_host"] = le_host
        o_lo.addRow("IP de envío:", le_host)

        spin_send = QSpinBox()
        spin_send.setRange(1024, 65535)
        spin_send.setFixedWidth(90)
        self._widgets["osc_send_port"] = spin_send
        o_lo.addRow("Puerto de envío:", spin_send)

        # TEST OSC row
        osc_test_row = QHBoxLayout()
        osc_test_row.setSpacing(8)
        btn_test = QPushButton("TEST OSC")
        btn_test.setFixedWidth(88)
        btn_test.setStyleSheet(
            "QPushButton { background-color: #1a2a3a; color: #64b5f6; "
            "border: 1px solid #2a4a6a; border-radius: 3px; }"
            "QPushButton:hover { background-color: #253545; }"
        )
        btn_test.clicked.connect(self._on_test_osc)
        self._lbl_osc_result = QLabel("")
        self._lbl_osc_result.setStyleSheet("color: #888888; font-size: 11px;")
        self._lbl_osc_result.setWordWrap(True)
        osc_test_row.addWidget(btn_test)
        osc_test_row.addWidget(self._lbl_osc_result, stretch=1)
        osc_test_container = QWidget()
        osc_test_container.setLayout(osc_test_row)
        o_lo.addRow("", osc_test_container)

        # Guardar controles para enable/disable; conectar checkbox
        self._osc_controls = [spin_listen, le_host, spin_send, btn_test]
        chk_osc.toggled.connect(self._on_osc_enabled_changed)

        form.addWidget(grp_osc)

        # ── Sección MIDI ───────────────────────────────────────────────
        grp_midi = QGroupBox("MIDI")
        m_lo = QFormLayout(grp_midi)
        m_lo.setSpacing(6)

        chk_midi = QCheckBox("Activar MIDI")
        self._widgets["midi_enabled"] = chk_midi
        m_lo.addRow("", chk_midi)

        self._midi_controls: list[QWidget] = []

        if not _MIDO_AVAILABLE:
            lbl_mido_warn = QLabel(f"⚠  {_MIDO_ERROR}")
            lbl_mido_warn.setStyleSheet(
                "color: #e8a200; font-size: 11px; background: transparent;"
            )
            lbl_mido_warn.setWordWrap(True)
            m_lo.addRow("", lbl_mido_warn)
            chk_midi.setEnabled(False)
        else:
            # Dispositivos MIDI disponibles
            combo_midi = QComboBox()
            combo_midi.setMinimumWidth(260)
            self._widgets["midi_device_name"] = combo_midi
            for port in (_mido.get_input_names() or []):
                combo_midi.addItem(port)
            if combo_midi.count() == 0:
                combo_midi.addItem("(sin dispositivos MIDI detectados)")
                combo_midi.setEnabled(False)
            m_lo.addRow("Dispositivo:", combo_midi)
            self._midi_controls.append(combo_midi)

            # Nota GO con nombre en tiempo real + botón APRENDER
            spin_go = QSpinBox()
            spin_go.setRange(0, 127)
            spin_go.setFixedWidth(70)
            self._widgets["midi_go_note"] = spin_go
            self._lbl_go_note = QLabel(_midi_note_name(60))
            self._lbl_go_note.setStyleSheet(
                "color: #888888; font-size: 11px; min-width: 36px;"
            )
            spin_go.valueChanged.connect(
                lambda v: self._lbl_go_note.setText(_midi_note_name(v))
            )
            self._btn_learn_go = QPushButton("APRENDER GO")
            self._btn_learn_go.setFixedWidth(112)
            self._btn_learn_go.clicked.connect(lambda: self._on_midi_learn("go"))
            go_row = QHBoxLayout()
            go_row.setSpacing(6)
            go_row.addWidget(spin_go)
            go_row.addWidget(self._lbl_go_note)
            go_row.addStretch()
            go_row.addWidget(self._btn_learn_go)
            go_container = QWidget()
            go_container.setLayout(go_row)
            m_lo.addRow("Nota GO:", go_container)
            self._midi_controls += [spin_go, self._btn_learn_go]

            # Nota STOP con nombre en tiempo real + botón APRENDER
            spin_stop = QSpinBox()
            spin_stop.setRange(0, 127)
            spin_stop.setFixedWidth(70)
            self._widgets["midi_stop_note"] = spin_stop
            self._lbl_stop_note = QLabel(_midi_note_name(61))
            self._lbl_stop_note.setStyleSheet(
                "color: #888888; font-size: 11px; min-width: 36px;"
            )
            spin_stop.valueChanged.connect(
                lambda v: self._lbl_stop_note.setText(_midi_note_name(v))
            )
            self._btn_learn_stop = QPushButton("APRENDER STOP")
            self._btn_learn_stop.setFixedWidth(112)
            self._btn_learn_stop.clicked.connect(lambda: self._on_midi_learn("stop"))
            stop_row = QHBoxLayout()
            stop_row.setSpacing(6)
            stop_row.addWidget(spin_stop)
            stop_row.addWidget(self._lbl_stop_note)
            stop_row.addStretch()
            stop_row.addWidget(self._btn_learn_stop)
            stop_container = QWidget()
            stop_container.setLayout(stop_row)
            m_lo.addRow("Nota STOP:", stop_container)
            self._midi_controls += [spin_stop, self._btn_learn_stop]

            chk_midi.toggled.connect(self._on_midi_enabled_changed)

        form.addWidget(grp_midi)
        form.addStretch()
        return scroll

    # ------------------------------------------------------------------
    # Pestaña 4 — Interfaz
    # ------------------------------------------------------------------

    def _build_tab_interface(self) -> QWidget:
        scroll = _make_scroll()
        form = QVBoxLayout(scroll.widget())
        form.setSpacing(10)

        grp_ui = QGroupBox("VISTA")
        u_lo = QFormLayout(grp_ui)
        u_lo.setSpacing(6)

        chk_lat = QCheckBox("Mostrar columna Latencia")
        self._widgets["show_latency_column"] = chk_lat
        u_lo.addRow("", chk_lat)

        chk_next = QCheckBox("Mostrar etiqueta NEXT")
        self._widgets["show_next_cue_label"] = chk_next
        u_lo.addRow("", chk_next)

        chk_confirm = QCheckBox("Confirmar al llegar al último cue")
        self._widgets["confirm_last_cue"] = chk_confirm
        u_lo.addRow("", chk_confirm)

        form.addWidget(grp_ui)

        grp_kb = QGroupBox("ATAJOS DE TECLADO")
        k_lo = QFormLayout(grp_kb)
        k_lo.setSpacing(6)

        for key, label in (
            ("keyboard_go",   "GO:"),
            ("keyboard_stop", "STOP:"),
            ("keyboard_prev", "Cue anterior:"),
        ):
            le = QLineEdit()
            le.setFixedWidth(120)
            le.setPlaceholderText("Ej: Space, S, Left, Ctrl+G")
            self._widgets[key] = le
            k_lo.addRow(label, le)

        form.addWidget(grp_kb)
        form.addStretch()
        return scroll

    # ------------------------------------------------------------------
    # Pestaña 5 — Rutas
    # ------------------------------------------------------------------

    def _build_tab_paths(self) -> QWidget:
        scroll = _make_scroll()
        form = QVBoxLayout(scroll.widget())
        form.setSpacing(10)

        grp = QGroupBox("RUTAS POR DEFECTO")
        g_lo = QFormLayout(grp)
        g_lo.setSpacing(6)

        for key, label, tooltip in (
            ("default_show_path",  "Carpeta de shows:",  "Carpeta por defecto al abrir/guardar shows"),
            ("default_media_path", "Carpeta de media:",  "Carpeta por defecto al asignar archivos a cues"),
            ("ffmpeg_path",        "Ejecutable FFmpeg:", "Ruta completa a ffmpeg.exe (vacío = usar PATH del sistema)"),
        ):
            row = QHBoxLayout()
            row.setSpacing(4)
            le = QLineEdit()
            le.setPlaceholderText("(usar valor del sistema)" if key != "ffmpeg_path" else "ffmpeg")
            le.setToolTip(tooltip)
            self._widgets[key] = le
            row.addWidget(le, stretch=1)

            btn = QPushButton("…")
            btn.setFixedWidth(28)
            btn.setToolTip("Examinar")
            btn.clicked.connect(lambda _checked, k=key, w=le: self._browse_path(k, w))
            row.addWidget(btn)

            container = QWidget()
            container.setLayout(row)
            g_lo.addRow(label, container)

        form.addWidget(grp)
        form.addStretch()
        return scroll

    # ------------------------------------------------------------------
    # Carga y guardado de valores
    # ------------------------------------------------------------------

    def _load_values(self) -> None:
        """Lee todos los valores desde SettingsManager y los aplica a los widgets."""
        sm = self._sm

        # Audio device
        saved_idx = sm.get("audio_device_index", -1)
        combo_dev: QComboBox = self._widgets["audio_device"]  # type: ignore[assignment]
        target = 0
        for i in range(combo_dev.count()):
            if combo_dev.itemData(i) == saved_idx:
                target = i
                break
        combo_dev.setCurrentIndex(target)

        # Audio driver
        driver_map = {
            "wasapi":            "WASAPI",
            "wasapi_exclusive":  "WASAPI Exclusive",
            "asio":              "ASIO",
            "directsound":       "DirectSound",
        }
        combo_driver: QComboBox = self._widgets["audio_driver"]  # type: ignore[assignment]
        driver_text = driver_map.get(sm.get("audio_driver", "wasapi"), "WASAPI")
        idx_d = combo_driver.findText(driver_text)
        if idx_d >= 0:
            combo_driver.setCurrentIndex(idx_d)

        # Sample rate
        combo_sr: QComboBox = self._widgets["audio_sample_rate"]  # type: ignore[assignment]
        sr_val = sm.get("audio_sample_rate", 48000)
        for i in range(combo_sr.count()):
            if combo_sr.itemData(i) == sr_val:
                combo_sr.setCurrentIndex(i)
                break

        # Buffer size
        combo_buf: QComboBox = self._widgets["audio_buffer_size"]  # type: ignore[assignment]
        buf_val = sm.get("audio_buffer_size", 512)
        for i in range(combo_buf.count()):
            if combo_buf.itemData(i) == buf_val:
                combo_buf.setCurrentIndex(i)
                break

        self._update_latency_label()

        # Slider volume
        slider_vol: QSlider = self._widgets["master_volume"]  # type: ignore[assignment]
        slider_vol.setValue(sm.get("master_volume", 100))

        # SpinBox pre-roll
        self._spin_set("pre_roll_ms", sm.get("pre_roll_ms", 0))

        # Vídeo — pantalla de salida (combo por índice)
        saved_screen = sm.get("video_output_screen", 0)
        combo_screen: QComboBox = self._widgets["video_output_screen"]  # type: ignore[assignment]
        screen_target = 0
        for i in range(combo_screen.count()):
            if combo_screen.itemData(i) == saved_screen:
                screen_target = i
                break
        combo_screen.setCurrentIndex(screen_target)
        self._chk_set("video_fullscreen_output", sm.get("video_fullscreen_output", False))
        self._spin_set("video_default_volume", sm.get("video_default_volume", 100))
        self._chk_set("video_loop_default", sm.get("video_loop_default", False))
        self._spin_set("video_fade_frames", sm.get("video_fade_frames", 0))

        # Red — OSC
        osc_on = sm.get("osc_enabled", False)
        self._chk_set("osc_enabled", osc_on)
        self._spin_set("osc_listen_port", sm.get("osc_listen_port", 8000))
        self._le_set("osc_send_host", sm.get("osc_send_host", "127.0.0.1"))
        self._spin_set("osc_send_port", sm.get("osc_send_port", 9000))
        self._on_osc_enabled_changed(osc_on)   # sincronizar enable/disable inicial

        # Red — MIDI
        midi_on = sm.get("midi_enabled", False)
        self._chk_set("midi_enabled", midi_on)
        midi_w = self._widgets.get("midi_device_name")
        saved_dev = sm.get("midi_device_name", "")
        if isinstance(midi_w, QComboBox):
            idx_m = midi_w.findText(saved_dev)
            if idx_m >= 0:
                midi_w.setCurrentIndex(idx_m)
        elif isinstance(midi_w, QLineEdit):
            midi_w.setText(saved_dev)
        if _MIDO_AVAILABLE:
            self._spin_set("midi_go_note", sm.get("midi_go_note", 60))
            self._spin_set("midi_stop_note", sm.get("midi_stop_note", 61))
            self._on_midi_enabled_changed(midi_on)   # sincronizar enable/disable inicial

        # Interfaz
        self._chk_set("show_latency_column", sm.get("show_latency_column", True))
        self._chk_set("show_next_cue_label", sm.get("show_next_cue_label", True))
        self._chk_set("confirm_last_cue", sm.get("confirm_last_cue", True))
        self._le_set("keyboard_go",   sm.get("keyboard_go",   "Space"))
        self._le_set("keyboard_stop", sm.get("keyboard_stop", "S"))
        self._le_set("keyboard_prev", sm.get("keyboard_prev", "Left"))

        # Rutas
        self._le_set("default_show_path",  sm.get("default_show_path",  ""))
        self._le_set("default_media_path", sm.get("default_media_path", ""))
        self._le_set("ffmpeg_path",        sm.get("ffmpeg_path",        ""))

    def _collect_values(self) -> dict:
        """Lee todos los widgets y devuelve el dict de configuración actualizado."""
        sm = self._sm

        combo_dev: QComboBox = self._widgets["audio_device"]  # type: ignore[assignment]
        dev_idx = combo_dev.currentData()
        # Buscar nombre del dispositivo
        dev_name = ""
        for dev in self._audio_devices:
            if dev["index"] == dev_idx:
                dev_name = dev["name"]
                break

        driver_text: str = self._widgets["audio_driver"].currentText()  # type: ignore[union-attr]
        driver_key_map = {
            "WASAPI":           "wasapi",
            "WASAPI Exclusive": "wasapi_exclusive",
            "ASIO":             "asio",
            "DirectSound":      "directsound",
        }

        combo_sr: QComboBox  = self._widgets["audio_sample_rate"]   # type: ignore[assignment]
        combo_buf: QComboBox = self._widgets["audio_buffer_size"]    # type: ignore[assignment]
        slider_vol: QSlider  = self._widgets["master_volume"]        # type: ignore[assignment]

        return {
            # Audio
            "audio_device_index":  dev_idx,
            "audio_device_name":   dev_name,
            "audio_driver":        driver_key_map.get(driver_text, "wasapi"),
            "audio_sample_rate":   combo_sr.currentData(),
            "audio_buffer_size":   combo_buf.currentData(),
            "master_volume":       slider_vol.value(),
            "pre_roll_ms":         self._spin_get("pre_roll_ms"),
            # Vídeo
            "video_output_screen": (
                self._widgets["video_output_screen"].currentData() or 0  # type: ignore[union-attr]
            ),
            "video_fullscreen_output": self._chk_get("video_fullscreen_output"),
            "video_default_volume":    self._spin_get("video_default_volume"),
            "video_loop_default":      self._chk_get("video_loop_default"),
            "video_fade_frames":       self._spin_get("video_fade_frames"),
            # Red — OSC
            "osc_enabled":      self._chk_get("osc_enabled"),
            "osc_listen_port":  self._spin_get("osc_listen_port"),
            "osc_send_host":    self._le_get("osc_send_host"),
            "osc_send_port":    self._spin_get("osc_send_port"),
            # Red — MIDI
            "midi_enabled": self._chk_get("midi_enabled"),
            "midi_device_name": (
                self._widgets["midi_device_name"].currentText()
                if isinstance(self._widgets.get("midi_device_name"), QComboBox)
                else self._sm.get("midi_device_name", "")
            ),
            "midi_go_note":   self._spin_get("midi_go_note")   if _MIDO_AVAILABLE else self._sm.get("midi_go_note",   60),
            "midi_stop_note": self._spin_get("midi_stop_note") if _MIDO_AVAILABLE else self._sm.get("midi_stop_note", 61),
            # Interfaz
            "show_latency_column": self._chk_get("show_latency_column"),
            "show_next_cue_label": self._chk_get("show_next_cue_label"),
            "confirm_last_cue":    self._chk_get("confirm_last_cue"),
            "keyboard_go":   self._le_get("keyboard_go"),
            "keyboard_stop": self._le_get("keyboard_stop"),
            "keyboard_prev": self._le_get("keyboard_prev"),
            # Rutas
            "default_show_path":  self._le_get("default_show_path"),
            "default_media_path": self._le_get("default_media_path"),
            "ffmpeg_path":        self._le_get("ffmpeg_path"),
        }

    # ------------------------------------------------------------------
    # OSC slots
    # ------------------------------------------------------------------

    def _on_osc_enabled_changed(self, enabled: bool) -> None:
        for w in self._osc_controls:
            w.setEnabled(enabled)

    def _on_test_osc(self) -> None:
        host = self._le_get("osc_send_host").strip() or "127.0.0.1"
        port = self._spin_get("osc_send_port")
        self._lbl_osc_result.setText("Enviando…")
        self._lbl_osc_result.setStyleSheet("color: #888888; font-size: 11px;")

        worker = _OscTester(host, port)
        thread = QThread(self)
        worker.moveToThread(thread)
        thread.started.connect(worker.run)
        worker.result.connect(self._on_osc_test_result)
        worker.result.connect(thread.quit)
        thread.finished.connect(thread.deleteLater)
        # Keep refs alive until thread finishes
        self._osc_thread  = thread
        self._osc_worker  = worker
        thread.start()

    def _on_osc_test_result(self, msg: str) -> None:
        ok = msg.startswith("✓")
        self._lbl_osc_result.setText(msg)
        self._lbl_osc_result.setStyleSheet(
            f"color: {'#2ecc71' if ok else '#e8a200'}; font-size: 11px;"
        )

    # ------------------------------------------------------------------
    # MIDI slots
    # ------------------------------------------------------------------

    def _on_midi_enabled_changed(self, enabled: bool) -> None:
        for w in self._midi_controls:
            w.setEnabled(enabled)

    def _on_midi_learn(self, field: str) -> None:
        """Inicia modo escucha MIDI para 'go' o 'stop'. Timeout: 5 s."""
        self._stop_midi_learn()

        if not _MIDO_AVAILABLE:
            return
        combo_midi = self._widgets.get("midi_device_name")
        if not isinstance(combo_midi, QComboBox):
            return
        port_name = combo_midi.currentText()
        if not port_name or "(sin" in port_name:
            return

        self._learn_field = field
        btn = self._btn_learn_go if field == "go" else self._btn_learn_stop
        self._learn_btn = btn
        self._learn_countdown = 5
        btn.setText(f"5 s…")
        btn.setStyleSheet(
            "QPushButton { background-color: #3a2a00; color: #e8a200; "
            "border: 1px solid #5a4000; border-radius: 3px; }"
        )
        btn.setEnabled(False)

        self._learn_tick_timer = QTimer(self)
        self._learn_tick_timer.setInterval(1_000)
        self._learn_tick_timer.timeout.connect(self._on_learn_tick)
        self._learn_tick_timer.start()

        worker = _MidiLearnWorker(port_name)
        thread = QThread(self)
        worker.moveToThread(thread)
        thread.started.connect(worker.run)
        worker.note_received.connect(self._on_midi_note_received)
        worker.timed_out.connect(self._on_midi_learn_done)
        worker.note_received.connect(thread.quit)
        worker.timed_out.connect(thread.quit)
        thread.finished.connect(thread.deleteLater)
        self._learn_thread = thread
        self._learn_worker = worker
        thread.start()

    def _on_learn_tick(self) -> None:
        self._learn_countdown -= 1
        if self._learn_countdown > 0:
            self._learn_btn.setText(f"{self._learn_countdown} s…")
        else:
            self._learn_tick_timer.stop()

    def _on_midi_note_received(self, note: int) -> None:
        key = "midi_go_note" if self._learn_field == "go" else "midi_stop_note"
        spin: QSpinBox = self._widgets[key]  # type: ignore[assignment]
        spin.setValue(note)
        self._on_midi_learn_done()

    def _on_midi_learn_done(self) -> None:
        if hasattr(self, "_learn_tick_timer"):
            self._learn_tick_timer.stop()
        if hasattr(self, "_learn_btn") and self._learn_btn is not None:
            label = "APRENDER GO" if self._learn_field == "go" else "APRENDER STOP"
            self._learn_btn.setText(label)
            self._learn_btn.setStyleSheet("")
            self._learn_btn.setEnabled(True)
            self._learn_btn = None  # type: ignore[assignment]

    def _stop_midi_learn(self) -> None:
        """Cancela cualquier aprendizaje MIDI en curso."""
        if hasattr(self, "_learn_tick_timer"):
            self._learn_tick_timer.stop()
        thread = getattr(self, "_learn_thread", None)
        if thread is not None and thread.isRunning():
            thread.quit()
            thread.wait(500)
        self._on_midi_learn_done()

    # ------------------------------------------------------------------
    # Ciclo de vida del diálogo
    # ------------------------------------------------------------------

    def done(self, result: int) -> None:
        """Limpia hilos en curso antes de cerrar."""
        self._stop_midi_learn()
        super().done(result)

    # ------------------------------------------------------------------
    # Slots internos
    # ------------------------------------------------------------------

    def _on_save(self) -> None:
        self._sm.set_many(self._collect_values())
        self.settings_saved.emit()
        self.accept()

    def _on_reset_defaults(self) -> None:
        self._sm.reset_defaults()
        self._load_values()

    def _on_audio_device_changed(self, index: int) -> None:
        """Actualiza el badge ASIO/WASAPI al cambiar el dispositivo."""
        combo_dev: QComboBox = self._widgets["audio_device"]  # type: ignore[assignment]
        dev_idx = combo_dev.itemData(index)
        is_asio = False
        for dev in self._audio_devices:
            if dev["index"] == dev_idx:
                is_asio = dev.get("is_asio", False)
                break
        badge: QLabel = self._widgets["_audio_badge"]  # type: ignore[assignment]
        if is_asio:
            badge.setText("ASIO ✓")
            badge.setStyleSheet(_STYLE_BADGE_ASIO)
        else:
            badge.setText("WASAPI")
            badge.setStyleSheet(_STYLE_BADGE_OTHER)

    def _on_driver_changed(self, text: str) -> None:
        """Muestra/oculta el aviso de ASIO según el driver seleccionado."""
        self._lbl_asio_warn.setVisible(text == "ASIO")

    def _update_latency_label(self) -> None:
        """Recalcula la latencia estimada: buffer_size / sample_rate * 1000."""
        combo_sr:  QComboBox = self._widgets["audio_sample_rate"]  # type: ignore[assignment]
        combo_buf: QComboBox = self._widgets["audio_buffer_size"]  # type: ignore[assignment]
        sr  = combo_sr.currentData()  or 48000
        buf = combo_buf.currentData() or 512
        ms = buf / sr * 1000.0
        self._lbl_latency.setText(f"≈ {ms:.1f} ms")

    def _browse_path(self, key: str, widget: QLineEdit) -> None:
        """Abre un diálogo de selección de carpeta o archivo según la clave."""
        if key == "ffmpeg_path":
            path, _ = QFileDialog.getOpenFileName(
                self, "Seleccionar ejecutable FFmpeg",
                widget.text() or "",
                "Ejecutable (*.exe);;Todos (*)",
            )
        else:
            path = QFileDialog.getExistingDirectory(
                self, "Seleccionar carpeta",
                widget.text() or "",
            )
        if path:
            widget.setText(path)

    # ------------------------------------------------------------------
    # Helpers de acceso a widgets
    # ------------------------------------------------------------------

    def _spin_set(self, key: str, value: int) -> None:
        w: QSpinBox = self._widgets[key]  # type: ignore[assignment]
        w.setValue(int(value))

    def _spin_get(self, key: str) -> int:
        w: QSpinBox = self._widgets[key]  # type: ignore[assignment]
        return w.value()

    def _chk_set(self, key: str, value: bool) -> None:
        w: QCheckBox = self._widgets[key]  # type: ignore[assignment]
        w.setChecked(bool(value))

    def _chk_get(self, key: str) -> bool:
        w: QCheckBox = self._widgets[key]  # type: ignore[assignment]
        return w.isChecked()

    def _le_set(self, key: str, value: str) -> None:
        w: QLineEdit = self._widgets[key]  # type: ignore[assignment]
        w.setText(str(value))

    def _le_get(self, key: str) -> str:
        w: QLineEdit = self._widgets[key]  # type: ignore[assignment]
        return w.text().strip()


# ---------------------------------------------------------------------------
# Helper: scroll area para pestañas
# ---------------------------------------------------------------------------

def _make_scroll() -> QScrollArea:
    """Crea un QScrollArea con widget interno listo para añadir layout."""
    scroll = QScrollArea()
    scroll.setWidgetResizable(True)
    scroll.setFrameShape(QFrame.Shape.NoFrame)
    inner = QWidget()
    scroll.setWidget(inner)
    return scroll
