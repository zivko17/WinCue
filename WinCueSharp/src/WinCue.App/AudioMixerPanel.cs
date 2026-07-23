// =============================================================================
// AudioMixerPanel.cs — Pestaña AUDIO: la mesa de mezclas del show
// =============================================================================
//
// ORGANIZACIÓN (de izquierda a derecha, como una mesa real)
//   · MASTER    — fader master + VU de PROGRAMA (loopback: lo que sale del
//                 dispositivo) + medidor GR del limitador de bus + info de
//                 dispositivo/driver.
//   · AUDICIÓN  — trim de preescucha (se aplica SOBRE el volumen del cue
//                 auditado) + VU del PVW.
//   · CUES      — un strip por cue ACTIVO: fader en vivo (misma ruta de commit
//                 que el inspector → modelo + motor + undo) y VU real del
//                 stream para cues de AUDIO. Los de VÍDEO no tienen VU por-cue
//                 (mpv → dispositivo directo, el motor no ve las muestras): se
//                 marca honesto "VU n/d" y su nivel se ve en el MASTER.
//
// SINCRONÍA DE FADERS
//   Los faders fijos (master/audición) emiten (valor, final): en vivo a cada
//   cambio y un commit "final" con debounce de 400 ms para no clavar el disco
//   persistiendo settings a cada pixel de arrastre. Los faders de cue commitean
//   como el inspector (cada cambio: el modelo ya es en-memoria).
// =============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinCue.Controller;
using WinCue.Core;

namespace WinCue.App;

public sealed class AudioMixerPanel : ContentControl
{
    // ── Callbacks (los cablea MainWindow.Attach) ───────────────────────
    /// <summary>Fader master (0-100, final=commit para persistir).</summary>
    public Action<int, bool>? MasterVolumeChanged;
    /// <summary>Fader de audición (0-100, final=commit para persistir).</summary>
    public Action<int, bool>? AuditionVolumeChanged;
    /// <summary>Fader de un cue activo (cueId, volumen 0..1).</summary>
    public Action<int, double>? CueVolumeCommitted;
    /// <summary>GR del limitador del bus master (dB ≥ 0), para el medidor.</summary>
    public Func<float>? MasterGainReductionProvider;

    private static readonly Brush FgDim = Frozen(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
    private static readonly Brush AccentPgm = Frozen(new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b)));
    private static readonly Brush AccentPvw = Frozen(new SolidColorBrush(Color.FromRgb(0x2e, 0xcc, 0x71)));
    private static readonly Brush DbGreen = Frozen(new SolidColorBrush(Color.FromRgb(0x2e, 0xcc, 0x71)));

    private readonly Strip _master;
    private readonly Strip _audition;
    private readonly TextBlock _grText;
    private readonly GrBar _grBar = new();
    private readonly TextBlock _deviceInfo;
    private readonly StackPanel _cueHost = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _emptyHint;
    private readonly Dictionary<int, CueStrip> _cueStrips = new();

    public AudioMixerPanel()
    {
        _master = Strip.Build("MASTER", AccentPgm, withVu: true, extraMeter: _grBar);
        _grBar.ToolTip = Loc.T(
            "Limitador del bus (techo −1 dBFS): reducción de ganancia.\nLa suma de cues no puede saturar el dispositivo.",
            "Bus limiter (−1 dBFS ceiling): gain reduction.\nThe sum of cues cannot clip the device.");
        _master.VuTooltip(Loc.T(
            "VU de PROGRAMA (loopback): lo que sale del dispositivo,\naudio + vídeo + todo.",
            "PROGRAM VU (loopback): what leaves the device,\naudio + video + everything."));

        _grText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0xe8, 0xa2, 0x00))),
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "",
        };
        _master.Extras.Children.Add(_grText);
        _deviceInfo = new TextBlock
        {
            FontSize = 10.5, Foreground = FgDim, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        _master.Extras.Children.Add(_deviceInfo);

        _audition = Strip.Build(Loc.T("🎧 AUDICIÓN", "🎧 AUDITION"), AccentPvw, withVu: true);
        _audition.VuTooltip(Loc.T("VU de la preescucha (PVW).", "Audition (PVW) VU."));
        _audition.Extras.Children.Add(new TextBlock
        {
            FontSize = 10.5, Foreground = FgDim, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
            Text = Loc.T("Trim × volumen del cue", "Trim × cue volume"),
        });

        _emptyHint = new TextBlock
        {
            Text = Loc.T("— sin cues sonando —", "— no cues playing —"),
            Foreground = FgDim, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 24, 0),
        };

        var strips = new StackPanel { Orientation = Orientation.Horizontal };
        strips.Children.Add(_master.Root);
        strips.Children.Add(_audition.Root);
        strips.Children.Add(new Border
        {
            Width = 1, Background = Frozen(new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a))),
            Margin = new Thickness(6, 12, 6, 12),
        });
        strips.Children.Add(_cueHost);
        strips.Children.Add(_emptyHint);

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = strips,
        };

        // Faders fijos: en vivo a cada cambio + commit final con debounce
        WireDebouncedFader(_master, (v, final) => MasterVolumeChanged?.Invoke(v, final));
        WireDebouncedFader(_audition, (v, final) => AuditionVolumeChanged?.Invoke(v, final));

        // Medidor GR: sondeo ligero solo con la pestaña visible
        var grTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        grTimer.Tick += (_, _) =>
        {
            if (!IsVisible || MasterGainReductionProvider is null)
                return;
            float gr = MasterGainReductionProvider();
            _grBar.SetDb(gr);
            _grText.Text = gr >= 0.1f ? $"GR −{gr:0.0} dB" : "";
        };
        grTimer.Start();
    }

    // ── API que alimenta MainWindow ────────────────────────────────────

    public void SetMasterLevel(float l, float r) => _master.SetLevels(l, r);

    public void SetAuditionLevel(float l, float r) => _audition.SetLevels(l, r);

    public void SetMasterFader(int value) => _master.SetFaderSilent(value);

    public void SetAuditionFader(int value) => _audition.SetFaderSilent(value);

    public void SetDeviceInfo(string text) => _deviceInfo.Text = text;

    public void UpdateCueLevel(int cueId, float l, float r)
    {
        if (_cueStrips.TryGetValue(cueId, out var s))
            s.SetLevels(l, r);
    }

    /// <summary>Reconcilia los strips con los cues activos (misma cadencia que
    /// el panel PLAYING). Solo Audio y Video tienen strip (los demás tipos no
    /// suenan). No recrea strips existentes (sin parpadeo, sin soltar faders).</summary>
    public void SyncActive(IReadOnlyList<ActiveCueInfo> rows, Func<int, Cue?> resolveCue)
    {
        var seen = new HashSet<int>();
        foreach (var row in rows)
        {
            if (row.Type != CueType.Audio && row.Type != CueType.Video)
                continue;
            seen.Add(row.Id);
            if (_cueStrips.TryGetValue(row.Id, out var strip))
            {
                strip.RefreshFromModel(resolveCue(row.Id));
                continue;
            }
            var s = new CueStrip(row, resolveCue(row.Id),
                (id, vol) => CueVolumeCommitted?.Invoke(id, vol));
            _cueStrips[row.Id] = s;
            _cueHost.Children.Add(s.Root);
        }
        foreach (int id in _cueStrips.Keys.ToList())
        {
            if (seen.Contains(id))
                continue;
            _cueHost.Children.Remove(_cueStrips[id].Root);
            _cueStrips.Remove(id);
        }
        _emptyHint.Visibility = _cueStrips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Interno ────────────────────────────────────────────────────────

    private static void WireDebouncedFader(Strip strip, Action<int, bool> emit)
    {
        int pending = -1;
        var final = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        final.Tick += (_, _) =>
        {
            final.Stop();
            if (pending >= 0)
                emit(pending, true);
            pending = -1;
        };
        strip.FaderChanged = v =>
        {
            emit(v, false);
            pending = v;
            final.Stop();
            final.Start();
        };
    }

    private static string DbLabel(double v01) =>
        v01 <= 0.0001 ? "−∞ dB" : $"{20.0 * Math.Log10(v01):+0.0;-0.0} dB";

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Strip genérico: título, fader vertical + VU estéreo, etiqueta
    /// dB y zona de extras. Base de master/audición y de los strips por cue.</summary>
    private class Strip
    {
        public Border Root = null!;
        public Slider Fader = null!;
        public TextBlock Db = null!;
        public StackPanel Extras = null!;
        public Action<int>? FaderChanged;

        protected VuMeterControl? VuL, VuR;
        protected StackPanel MeterZone = null!;
        protected bool Syncing;

        public static Strip Build(string title, Brush accent, bool withVu,
                                  FrameworkElement? extraMeter = null)
        {
            var s = new Strip();
            s.Init(title, accent, withVu, extraMeter);
            return s;
        }

        protected void Init(string title, Brush accent, bool withVu,
                            FrameworkElement? extraMeter = null)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = title, FontWeight = FontWeights.Bold, FontSize = 12,
                Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            grid.Children.Add(titleText);

            MeterZone = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Fader = new Slider
            {
                Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100,
                Value = 100, Width = 26, MinHeight = 110,
                IsMoveToPointEnabled = true,
            };
            MeterZone.Children.Add(Fader);
            if (withVu)
            {
                // Regla de dB pegada a los VUs: sin referencia, "la barra a la
                // mitad" no dice nada. Mismos márgenes verticales que los VUs
                // para que las marcas coincidan con las suyas.
                MeterZone.Children.Add(new DbScale { Margin = new Thickness(6, 2, 0, 2) });
                VuL = new VuMeterControl(vertical: true) { Width = 7, Margin = new Thickness(2, 2, 0, 2) };
                VuR = new VuMeterControl(vertical: true) { Width = 7, Margin = new Thickness(1, 2, 0, 2) };
                MeterZone.Children.Add(VuL);
                MeterZone.Children.Add(VuR);
            }
            if (extraMeter is not null)
            {
                extraMeter.Margin = new Thickness(4, 2, 0, 2);
                MeterZone.Children.Add(extraMeter);
            }
            Grid.SetRow(MeterZone, 1);
            grid.Children.Add(MeterZone);

            Db = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Foreground = DbGreen, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0), Text = DbLabel(1.0),
            };
            Grid.SetRow(Db, 2);
            grid.Children.Add(Db);

            Extras = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            Grid.SetRow(Extras, 3);
            grid.Children.Add(Extras);

            Root = new Border
            {
                Width = 132, Margin = new Thickness(5, 8, 5, 8),
                Padding = new Thickness(8, 6, 8, 6),
                Background = Frozen(new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2b))),
                BorderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a))),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Child = grid,
            };

            Fader.ValueChanged += (_, e) =>
            {
                Db.Text = DbLabel(e.NewValue / 100.0);
                if (!Syncing)
                    FaderChanged?.Invoke((int)Math.Round(e.NewValue));
            };
        }

        public void SetLevels(float l, float r)
        {
            VuL?.SetLevel(l);
            VuR?.SetLevel(r);
        }

        public void SetFaderSilent(int value)
        {
            Syncing = true;
            Fader.Value = Math.Clamp(value, 0, 100);
            Syncing = false;
        }

        public void VuTooltip(string tip)
        {
            if (VuL is null) return;
            VuL.ToolTip = tip;
            VuR!.ToolTip = tip;
        }
    }

    /// <summary>Strip de un cue activo. Audio → VU real del stream; Video →
    /// sin VU por-cue (mpv directo al dispositivo): aviso honesto.</summary>
    private sealed class CueStrip : Strip
    {
        public readonly int CueId;

        public CueStrip(ActiveCueInfo info, Cue? cue, Action<int, double> commit)
        {
            CueId = info.Id;
            bool isAudio = info.Type == CueType.Audio;
            string glyph = isAudio ? "♪" : "🎬";
            Init($"{glyph} {info.Number} · {info.Name}",
                 isAudio ? Frozen(new SolidColorBrush(Color.FromRgb(0x2e, 0x86, 0xde)))
                         : Frozen(new SolidColorBrush(Color.FromRgb(0xbe, 0x2e, 0xde))),
                 withVu: isAudio);
            Root.ToolTip = $"{info.Number} · {info.Name}";

            if (!isAudio)
            {
                Extras.Children.Add(new TextBlock
                {
                    FontSize = 10.5, Foreground = FgDim, TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
                    Text = Loc.T("VU n/d (mpv → dispositivo);\nsu nivel se ve en MASTER",
                                 "VU n/a (mpv → device);\nits level shows on MASTER"),
                });
            }

            SetFaderSilent((int)Math.Round((cue?.Volume ?? 1.0) * 100.0));
            Db.Text = DbLabel(cue?.Volume ?? 1.0);
            FaderChanged = v => commit(CueId, v / 100.0);
        }

        /// <summary>El volumen del modelo cambió por otra vía (inspector) →
        /// reflejarlo, salvo que el operador esté arrastrando ESTE fader.</summary>
        public void RefreshFromModel(Cue? cue)
        {
            if (cue is null || Fader.IsMouseCaptureWithin || Fader.IsKeyboardFocusWithin)
                return;
            int model = (int)Math.Round(cue.Volume * 100.0);
            if (Math.Abs(Fader.Value - model) >= 1.0)
                SetFaderSilent(model);
        }
    }

    /// <summary>Regla de dB de los VUs: cifras 0/−6/−12/−24/−40 en la misma
    /// escala −60…0 dBFS que VuMeterControl (norm = (db+60)/60).</summary>
    private sealed class DbScale : FrameworkElement
    {
        private static readonly (double Db, string Label)[] Marks =
            { (0, "0"), (-6, "6"), (-12, "12"), (-24, "24"), (-40, "40") };
        private static readonly Typeface Font = new("Consolas");
        private static readonly Brush TextBrush =
            Frozen(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));

        public DbScale() => Width = 16;

        protected override void OnRender(DrawingContext dc)
        {
            double h = ActualHeight;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var (db, label) in Marks)
            {
                double y = h * (1.0 - (db + 60.0) / 60.0);
                var ft = new FormattedText(label,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Font, 8.5, TextBrush, dip);
                dc.DrawText(ft, new Point(Width - 2 - ft.Width,
                    Math.Clamp(y - ft.Height / 2, 0, Math.Max(0, h - ft.Height))));
            }
        }
    }

    /// <summary>Medidor de reducción de ganancia del limitador: barra naranja
    /// que crece de ARRIBA hacia abajo (como en una mesa), escala 0-12 dB.</summary>
    private sealed class GrBar : FrameworkElement
    {
        private static readonly Brush Bg = Frozen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
        private static readonly Pen Frame = MakePen(Color.FromRgb(0x4a, 0x4a, 0x4a));
        private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromRgb(0xe8, 0xa2, 0x00)));

        private double _db;

        public GrBar() => Width = 7;

        public void SetDb(float db)
        {
            double clamped = Math.Clamp(db, 0.0, 12.0);
            if (Math.Abs(clamped - _db) < 0.05)
                return;
            _db = clamped;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Bg, Frame, new Rect(0.5, 0.5, w - 1, h - 1));
            if (_db > 0.05)
                dc.DrawRectangle(Fill, null, new Rect(1, 1, w - 2, (h - 2) * (_db / 12.0)));
        }

        private static Pen MakePen(Color c)
        {
            var p = new Pen(new SolidColorBrush(c), 1);
            p.Freeze();
            return p;
        }
    }
}
