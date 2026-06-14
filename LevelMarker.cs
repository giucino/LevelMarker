using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Linq;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace LevelMarker
{
    // Wo der Level eines Delta-Outlier-Bars verankert wird.
    public enum LevelAnchor
    {
        [Display(Name = "vPOC des Bars")] Poc,
        [Display(Name = "Close des Bars")] Close,
        [Display(Name = "Extrem (High/Low)")] Extreme
    }

    [DisplayName("Level Marker (Delta-Outlier)")]
    [HelpLink("https://giucino.github.io/LevelMarker/LevelMarker_Doku.html")]
    [Description("Stufe 2 — markiert Levels of Interest und projiziert sie nach rechts. " +
                 "Zwei zuschaltbare Detektoren: Delta-Outlier (pro Bar, mean + k*sigma) und " +
                 "Zero Prints (Footprint-Level mit Bid- oder Ask-Volumen = 0). " +
                 "Rein informativ, kein Signal. (Profil-Zonen wie LVN/HVN: Indikator 'Profile Levels'.)")]
    public class LevelMarker : Indicator
    {
        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Delta-Outlier
        // ─────────────────────────────────────────────────────────────────
        private bool _enableOutlier = true;    // Default AN (bestehendes Verhalten)
        private int _lookbackBars = 50;
        private int _minBarsForStats = 10;
        private decimal _outlierStdDev = 2.0m;
        private decimal _minAbsDelta = 0m;
        private LevelAnchor _anchor = LevelAnchor.Poc;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Zero Prints
        // ─────────────────────────────────────────────────────────────────
        private bool _enableZeroPrints = false;
        private decimal _zpMinVolume = 50m;    // Mindestvolumen auf der gehandelten Seite

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Darstellung
        // ─────────────────────────────────────────────────────────────────
        private int _maxLevels = 50;           // max. Zonen PRO Typ
        private int _lineWidth = 2;
        private bool _showLabels = true;
        private bool _fullWidthLines = false;
        private int _fontSize = 11;

        private Color _colorUp = Color.FromArgb(230, 50, 205, 80);     // Kauf-Outlier
        private Color _colorDown = Color.FromArgb(230, 225, 60, 60);   // Verkauf-Outlier
        private Color _colorZeroPrint = Color.FromArgb(230, 235, 165, 45);  // Zero Print

        // ─────────────────────────────────────────────────────────────────
        //  STATE
        // ─────────────────────────────────────────────────────────────────
        private enum ZoneKind { OutlierUp, OutlierDown, ZeroPrint }

        private readonly struct Zone
        {
            public readonly int Bar;
            public readonly decimal Price;
            public readonly string Label;
            public readonly ZoneKind Kind;
            public Zone(int bar, decimal price, string label, ZoneKind kind)
            { Bar = bar; Price = price; Label = label; Kind = kind; }
        }

        private readonly List<Zone> _outliers = new();
        private readonly List<Zone> _zeroPrints = new();

        // Rollierendes Fenster der |Delta| (ohne den jeweils getesteten Bar).
        private readonly Queue<decimal> _absWindow = new();

        private int _lastProcessedBar = -1;
        private int _lastDrawnCount = -1;
        private decimal _tickEstimate = 0m;    // aus den Footprint-Preisen abgeleitet

        private RenderFont _font = null!;

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Delta-Outlier
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Delta-Outlier aktiv", GroupName = "Delta-Outlier", Order = 1,
            Description = "Schaltet den Delta-Outlier-Detektor ein/aus. Markiert Bars mit statistisch " +
                          "aussergewoehnlich hohem |Delta| (mean + k*sigma ueber rollierendem Fenster).")]
        public bool EnableOutlier { get => _enableOutlier; set { _enableOutlier = value; RecalculateValues(); } }

        [Display(Name = "Lookback (Bars)", GroupName = "Delta-Outlier", Order = 2,
            Description = "Groesse des rollierenden Vergleichsfensters fuer den Delta-Outlier. Klein = schnell, " +
                          "mehr Marker. Gross = traeger, nur extreme Ausreisser.")]
        [Range(5, 1000)]
        public int LookbackBars { get => _lookbackBars; set { _lookbackBars = Math.Max(5, value); RecalculateValues(); } }

        [Display(Name = "Min. Bars fuer Statistik", GroupName = "Delta-Outlier", Order = 3,
            Description = "Mindestanzahl Bars im Fenster, bevor gewertet wird. Verhindert Fehlalarme am Chart-Start, " +
                          "wenn die Statistik noch instabil ist.")]
        [Range(2, 500)]
        public int MinBarsForStats { get => _minBarsForStats; set { _minBarsForStats = Math.Max(2, value); RecalculateValues(); } }

        [Display(Name = "Outlier-Schwelle (Std-Abw.)", GroupName = "Delta-Outlier", Order = 4,
            Description = "Der Faktor k in 'Mittelwert + k x Standardabweichung'. Hoeher = strenger, niedriger = " +
                          "mehr Marker. Uebliche Werte 2,0-3,0. Passt sich relativ zur Streuung automatisch an.")]
        [Range(0.5, 10)]
        public decimal OutlierStdDev { get => _outlierStdDev; set { _outlierStdDev = Math.Max(0.1m, value); RecalculateValues(); } }

        [Display(Name = "Min. |Delta| (0 = aus)", GroupName = "Delta-Outlier", Order = 5,
            Description = "Absoluter Mindest-|Delta| zusaetzlich zur Statistik. Filtert Rausch in ruhigen Phasen. " +
                          "0 = aus. Auf Tick-Charts meist unnoetig.")]
        [Range(0, 1000000)]
        public decimal MinAbsDelta { get => _minAbsDelta; set { _minAbsDelta = Math.Max(0m, value); RecalculateValues(); } }

        [Display(Name = "Level-Anker", GroupName = "Delta-Outlier", Order = 6,
            Description = "Auf welchem Preis des Outlier-Bars die Linie sitzt. vPOC = Preis mit dem meisten Volumen " +
                          "(empfohlen). Close = Schlusskurs. Extrem = High bei Kauf-, Low bei Verkauf-Outlier.")]
        public LevelAnchor Anchor { get => _anchor; set { _anchor = value; RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Zero Prints
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Zero Prints aktiv", GroupName = "Zero Prints", Order = 10,
            Description = "Schaltet den Zero-Print-Detektor ein/aus. Markiert Footprint-Level, auf denen eine Seite " +
                          "gar nicht gehandelt hat (Bid = 0 oder Ask = 0). Solche 'unfairen' Preise sind oft " +
                          "magnetische Zielzonen, zu denen der Markt zurueckkehrt.")]
        public bool EnableZeroPrints { get => _enableZeroPrints; set { _enableZeroPrints = value; RecalculateValues(); } }

        [Display(Name = "Min. Volumen (gehandelte Seite)", GroupName = "Zero Prints", Order = 11,
            Description = "Mindestvolumen auf der tatsaechlich gehandelten Seite, damit ein Zero Print zaehlt. " +
                          "Filtert belanglose 1-2-Kontrakt-Luecken weg.")]
        [Range(0, 100000)]
        public decimal ZeroPrintMinVolume { get => _zpMinVolume; set { _zpMinVolume = Math.Max(0m, value); RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Darstellung
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Max. Zonen pro Typ", GroupName = "Darstellung", Order = 30,
            Description = "Wie viele der juengsten Zonen je Detektor (Outlier / Zero Print) gehalten und " +
                          "gezeichnet werden. Aeltere fallen heraus.")]
        [Range(1, 500)]
        public int MaxLevels { get => _maxLevels; set { _maxLevels = Math.Max(1, value); RecalculateValues(); } }

        [Display(Name = "Linienbreite", GroupName = "Darstellung", Order = 31,
            Description = "Dicke der Linien in Pixeln.")]
        [Range(1, 8)]
        public int LineWidth { get => _lineWidth; set { _lineWidth = Math.Clamp(value, 1, 8); RedrawChart(); } }

        [Display(Name = "Labels anzeigen", GroupName = "Darstellung", Order = 32,
            Description = "Blendet die Text-Labels (Delta-Wert, 'ZP') an den Zonen ein/aus.")]
        public bool ShowLabels { get => _showLabels; set { _showLabels = value; RedrawChart(); } }

        [Display(Name = "Linie durchgehend (aus = ab Signal-Bar)", GroupName = "Darstellung", Order = 33,
            Description = "Aus (Standard): Linie beginnt am Signal-Bar und laeuft nach rechts. " +
                          "An: durchgehende Linie ueber die volle Chart-Breite.")]
        public bool FullWidthLines { get => _fullWidthLines; set { _fullWidthLines = value; RedrawChart(); } }

        [Display(Name = "Schriftgroesse", GroupName = "Darstellung", Order = 34,
            Description = "Schriftgroesse der Labels.")]
        [Range(8, 24)]
        public int FontSize { get => _fontSize; set { _fontSize = Math.Clamp(value, 8, 24); BuildFonts(); RedrawChart(); } }

        [Display(Name = "Farbe Kauf-Outlier", GroupName = "Farben", Order = 40,
            Description = "Farbe fuer Kauf-Outlier (positives Delta).")]
        public Color ColorUp { get => _colorUp; set { _colorUp = value; RedrawChart(); } }

        [Display(Name = "Farbe Verkauf-Outlier", GroupName = "Farben", Order = 41,
            Description = "Farbe fuer Verkauf-Outlier (negatives Delta).")]
        public Color ColorDown { get => _colorDown; set { _colorDown = value; RedrawChart(); } }

        [Display(Name = "Farbe Zero Print", GroupName = "Farben", Order = 42,
            Description = "Farbe fuer Zero-Print-Zonen.")]
        public Color ColorZeroPrint { get => _colorZeroPrint; set { _colorZeroPrint = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  CTOR
        // ─────────────────────────────────────────────────────────────────
        public LevelMarker() : base(true)
        {
            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            DataSeries[0].IsHidden = true;

            // Pflicht fuer persistentes Custom-Drawing (siehe CLAUDE.md): ohne dies
            // zeichnet ATAS nur im LatestBar-Durchgang und alles verschwindet, sobald
            // man vom aktuellsten Bar wegnavigiert (Drag/Zoom/ZoomXY).
            SubscribeToDrawingEvents(DrawingLayouts.Historical | DrawingLayouts.Final);

            BuildFonts();
        }

        private void BuildFonts()
        {
            _font = new RenderFont("Consolas", _fontSize);
        }

        // ─────────────────────────────────────────────────────────────────
        //  HAUPTBERECHNUNG
        // ─────────────────────────────────────────────────────────────────
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
                ResetState();

            // Jeden ABGESCHLOSSENEN Bar genau einmal verarbeiten (Outlier + Zero Prints).
            int lastClosed = CurrentBar - 2;
            bool advanced = false;
            while (_lastProcessedBar < lastClosed)
            {
                _lastProcessedBar++;
                ProcessClosedBar(_lastProcessedBar);
                advanced = true;
            }

            if (bar == CurrentBar - 1)
            {
                int total = _outliers.Count + _zeroPrints.Count;
                // advanced = neuer Bar geschlossen -> neu zeichnen (Zonen koennen sich
                // geaendert haben, auch wenn die Gesamtzahl gleich bleibt).
                if (advanced || total != _lastDrawnCount)
                {
                    _lastDrawnCount = total;
                    RedrawChart();
                }
            }
        }

        private void ResetState()
        {
            _outliers.Clear();
            _zeroPrints.Clear();
            _absWindow.Clear();
            _lastProcessedBar = -1;
            _lastDrawnCount = -1;
            _tickEstimate = 0m;
        }

        // ─────────────────────────────────────────────────────────────────
        //  PRO-BAR-VERARBEITUNG (Delta-Outlier + Zero Prints)
        // ─────────────────────────────────────────────────────────────────
        private void ProcessClosedBar(int bar)
        {
            var c = GetCandle(bar);
            if (c == null)
                return;

            if (_tickEstimate <= 0m)
                UpdateTickEstimate(c);

            // --- Delta-Outlier ---
            if (_enableOutlier)
            {
                decimal absDelta = Math.Abs(c.Delta);
                if (_absWindow.Count >= _minBarsForStats)
                {
                    decimal mean = _absWindow.Average();
                    double varSum = 0;
                    foreach (var v in _absWindow)
                    {
                        double d = (double)(v - mean);
                        varSum += d * d;
                    }
                    decimal std = (decimal)Math.Sqrt(varSum / _absWindow.Count);
                    decimal threshold = mean + _outlierStdDev * std;

                    if (std > 0m && absDelta >= threshold && absDelta >= _minAbsDelta)
                        AddOutlier(bar, c);
                }

                _absWindow.Enqueue(absDelta);
                while (_absWindow.Count > _lookbackBars)
                    _absWindow.Dequeue();
            }

            // --- Zero Prints ---
            if (_enableZeroPrints)
            {
                // Bestehende Zero Prints, die DIESER (spaetere) Bar preislich beruehrt,
                // sind "gefuellt" -> nicht mehr gueltig, entfernen.
                if (_zeroPrints.Count > 0)
                    _zeroPrints.RemoveAll(z => z.Bar < bar && c.Low <= z.Price && z.Price <= c.High);

                // Neue Zero Prints dieses Bars (eine Seite gar nicht gehandelt).
                foreach (var pv in c.GetAllPriceLevels())
                {
                    bool bidZero = pv.Bid == 0m && pv.Ask >= _zpMinVolume;
                    bool askZero = pv.Ask == 0m && pv.Bid >= _zpMinVolume;
                    if (bidZero || askZero)
                        AddZeroPrint(bar, pv.Price);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  HELPER
        // ─────────────────────────────────────────────────────────────────
        private void AddOutlier(int bar, IndicatorCandle c)
        {
            bool up = c.Delta >= 0;
            decimal price = _anchor switch
            {
                LevelAnchor.Close => c.Close,
                LevelAnchor.Extreme => up ? c.High : c.Low,
                _ => GetPoc(c)
            };
            string label = $"Δ {c.Delta:+0;-0}";
            AddZone(_outliers, new Zone(bar, price, label, up ? ZoneKind.OutlierUp : ZoneKind.OutlierDown), 0m);
        }

        private void AddZeroPrint(int bar, decimal price)
        {
            // benachbarte Zero-Print-Ticks zu EINER Zone zusammenfassen
            decimal tol = _tickEstimate > 0m ? _tickEstimate * 4m : 0m;
            AddZone(_zeroPrints, new Zone(bar, price, "ZP", ZoneKind.ZeroPrint), tol);
        }

        // Fuegt eine Zone hinzu, sofern noch keine im Toleranzabstand existiert; begrenzt die Liste.
        private void AddZone(List<Zone> list, Zone zone, decimal tol)
        {
            if (tol > 0m)
            {
                foreach (var z in list)
                    if (Math.Abs(z.Price - zone.Price) <= tol) return;
            }
            list.Add(zone);
            while (list.Count > _maxLevels)
                list.RemoveAt(0);
        }

        // Farbe erst beim Zeichnen aus den aktuellen Einstellungen aufloesen
        // (sonst wuerden Farbaenderungen erst nach Neuberechnung greifen).
        private Color ColorOf(ZoneKind k) => k switch
        {
            ZoneKind.OutlierUp => _colorUp,
            ZoneKind.OutlierDown => _colorDown,
            ZoneKind.ZeroPrint => _colorZeroPrint,
            _ => Color.White
        };

        private static decimal GetPoc(IndicatorCandle c)
        {
            decimal pocPrice = c.Close;
            decimal maxVol = -1m;
            foreach (var pv in c.GetAllPriceLevels())
            {
                if (pv.Volume > maxVol) { maxVol = pv.Volume; pocPrice = pv.Price; }
            }
            return pocPrice;
        }

        // Tick aus den Footprint-Preisen ableiten (kleinster positiver Abstand).
        private void UpdateTickEstimate(IndicatorCandle c)
        {
            var prices = c.GetAllPriceLevels().Select(p => p.Price).OrderBy(p => p).ToList();
            for (int i = 1; i < prices.Count; i++)
            {
                var d = prices[i] - prices[i - 1];
                if (d > 0m && (_tickEstimate <= 0m || d < _tickEstimate))
                    _tickEstimate = d;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  RENDER
        // ─────────────────────────────────────────────────────────────────
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (_font == null)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;
            int lastBar = CurrentBar - 1;
            if (lastBar < 0)
                return;

            DrawZones(context, cont, region, lastBar, _outliers);
            DrawZones(context, cont, region, lastBar, _zeroPrints);
        }

        private void DrawZones(RenderContext context, IChartContainer cont, Rectangle region, int lastBar, List<Zone> list)
        {
            foreach (var z in list)
            {
                int y;
                try { y = cont.GetYByPrice(z.Price, false); }
                catch { continue; }

                int x1 = region.Left;
                int x2 = region.Right;

                if (!_fullWidthLines)
                {
                    try
                    {
                        int b = Math.Min(Math.Max(z.Bar, 0), lastBar);
                        int xOrigin = cont.GetXByBar(b, false);
                        if (xOrigin > region.Right) continue;   // Ursprung rechts ausserhalb
                        x1 = Math.Max(xOrigin, region.Left);
                    }
                    catch { x1 = region.Left; }
                }

                var col = ColorOf(z.Kind);
                context.DrawLine(new RenderPen(col, _lineWidth), x1, y, x2, y);

                if (_showLabels && !string.IsNullOrEmpty(z.Label))
                {
                    try
                    {
                        var sz = context.MeasureString(z.Label, _font);
                        int b = Math.Min(Math.Max(z.Bar, 0), lastBar);
                        int xOrigin = cont.GetXByBar(b, false);
                        int labelX = Math.Min(Math.Max(xOrigin + 3, region.Left + 3),
                                              region.Right - sz.Width - 3);
                        context.DrawString(z.Label, _font, col, labelX, y - sz.Height - 1);
                    }
                    catch { /* Label diesmal weglassen */ }
                }
            }
        }
    }
}
