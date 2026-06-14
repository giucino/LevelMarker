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
    // Wo der Level eines Outlier-Bars verankert wird.
    public enum LevelAnchor
    {
        [Display(Name = "vPOC des Bars")] Poc,
        [Display(Name = "Close des Bars")] Close,
        [Display(Name = "Extrem (High/Low)")] Extreme
    }

    [DisplayName("Level Marker (Delta-Outlier)")]
    [HelpLink("https://giucino.github.io/LevelMarker/LevelMarker_Doku.html")]
    [Description("Stufe 2 — markiert Range-Bars mit aussergewoehnlich hohem |Delta| " +
                 "(statistischer Ausreisser ggue. einem rollierenden Fenster) und projiziert " +
                 "den zugehoerigen Level nach rechts. Rein informativ, kein Entry-Signal. " +
                 "Fuer Range-Bar-Charts gedacht.")]
    public class LevelMarker : Indicator
    {
        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Erkennung
        // ─────────────────────────────────────────────────────────────────
        private int _lookbackBars = 50;        // Groesse des rollierenden Fensters
        private int _minBarsForStats = 10;     // Mindestanzahl Bars bevor gewertet wird
        private decimal _outlierStdDev = 2.0m; // Schwelle: mean + k * stdev
        private decimal _minAbsDelta = 0m;     // optionaler absoluter Boden (0 = aus)

        private LevelAnchor _anchor = LevelAnchor.Poc;
        private int _maxLevels = 50;           // wie viele Level max. gehalten/gezeichnet werden

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Darstellung
        // ─────────────────────────────────────────────────────────────────
        private int _lineWidth = 2;
        private bool _showLabels = true;
        private bool _fullWidthLines = false;  // false = ab Signal-Bar (Default), true = durchgehend
        private int _fontSize = 11;

        private Color _colorUp = Color.FromArgb(230, 50, 205, 80);    // Kauf-Outlier
        private Color _colorDown = Color.FromArgb(230, 225, 60, 60);  // Verkauf-Outlier

        // ─────────────────────────────────────────────────────────────────
        //  STATE
        // ─────────────────────────────────────────────────────────────────
        private readonly struct Level
        {
            public readonly int Bar;
            public readonly decimal Price;
            public readonly decimal Delta;
            public readonly bool Up;
            public Level(int bar, decimal price, decimal delta, bool up)
            { Bar = bar; Price = price; Delta = delta; Up = up; }
        }

        private readonly List<Level> _levels = new();
        // Rollierendes Fenster der |Delta| der zuletzt ABGESCHLOSSENEN Bars
        // (ohne den jeweils getesteten Bar -> sauberer Outlier-Test).
        private readonly Queue<decimal> _absWindow = new();

        private int _lastProcessedBar = -1;    // jeder Bar genau einmal verarbeitet
        private int _lastDrawnCount = -1;       // RedrawChart nur bei Aenderung

        private RenderFont _font = null!;

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Erkennung
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Lookback (Bars)", GroupName = "Erkennung", Order = 1,
            Description = "Groesse des rollierenden Vergleichsfensters: ueber so viele zurueckliegende Bars " +
                          "werden Durchschnitt und Streuung des |Delta| berechnet, gegen die der aktuelle Bar " +
                          "getestet wird. Klein = reagiert schnell, mehr Marker. Gross = traeger, nur extreme Ausreisser.")]
        [Range(5, 1000)]
        public int LookbackBars { get => _lookbackBars; set { _lookbackBars = Math.Max(5, value); RecalculateValues(); } }

        [Display(Name = "Min. Bars fuer Statistik", GroupName = "Erkennung", Order = 2,
            Description = "Mindestanzahl Bars im Fenster, bevor ueberhaupt gewertet wird. Verhindert Fehlalarme " +
                          "direkt nach Chart-Start / Symbolwechsel, wenn Durchschnitt und Streuung noch auf zu " +
                          "wenigen Bars beruhen und instabil waeren.")]
        [Range(2, 500)]
        public int MinBarsForStats { get => _minBarsForStats; set { _minBarsForStats = Math.Max(2, value); RecalculateValues(); } }

        [Display(Name = "Outlier-Schwelle (Std-Abw.)", GroupName = "Erkennung", Order = 3,
            Description = "Der Faktor k in der Schwelle 'Mittelwert + k x Standardabweichung'. Hauptregler fuer die " +
                          "Empfindlichkeit. Hoeher = strenger (nur die heftigsten Bars), niedriger = lockerer (mehr Marker). " +
                          "Uebliche Werte 2,0-3,0. Da relativ zur Streuung, passt sich die Schwelle automatisch an ruhige/wilde Phasen an.")]
        [Range(0.5, 10)]
        public decimal OutlierStdDev { get => _outlierStdDev; set { _outlierStdDev = Math.Max(0.1m, value); RecalculateValues(); } }

        [Display(Name = "Min. |Delta| (0 = aus)", GroupName = "Erkennung", Order = 4,
            Description = "Absoluter Mindestbetrag des Delta, den ein Bar ZUSAETZLICH zur statistischen Schwelle erreichen muss. " +
                          "Filtert Rausch-Marker in sehr ruhigen Phasen, in denen die Streuung kollabiert und ein belangloser " +
                          "Bar 'statistisch' zum Ausreisser wuerde. 0 = aus. Auf Tick-Charts meist unnoetig.")]
        [Range(0, 1000000)]
        public decimal MinAbsDelta { get => _minAbsDelta; set { _minAbsDelta = Math.Max(0m, value); RecalculateValues(); } }

        [Display(Name = "Level-Anker", GroupName = "Erkennung", Order = 5,
            Description = "Auf welchem Preis des Outlier-Bars die Linie sitzt. vPOC = Preis mit dem meisten Volumen " +
                          "(wo die Aggression abgewickelt wurde, empfohlen). Close = Schlusskurs des Bars. " +
                          "Extrem = High bei Kauf-Outlier, Low bei Verkauf-Outlier.")]
        public LevelAnchor Anchor { get => _anchor; set { _anchor = value; RecalculateValues(); } }

        [Display(Name = "Max. Level (Anzahl)", GroupName = "Erkennung", Order = 6,
            Description = "Wie viele der juengsten Level gleichzeitig gehalten und gezeichnet werden. Aeltere fallen " +
                          "automatisch heraus. Niedriger = uebersichtlicher, hoeher = mehr historischer Kontext.")]
        [Range(1, 500)]
        public int MaxLevels { get => _maxLevels; set { _maxLevels = Math.Max(1, value); RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Darstellung
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Linienbreite", GroupName = "Darstellung", Order = 10,
            Description = "Dicke der Level-Linie in Pixeln.")]
        [Range(1, 8)]
        public int LineWidth { get => _lineWidth; set { _lineWidth = Math.Clamp(value, 1, 8); RedrawChart(); } }

        [Display(Name = "Labels anzeigen", GroupName = "Darstellung", Order = 11,
            Description = "Blendet den Delta-Wert (z.B. 'Delta +500') am Level ein oder aus. Aus = ruhigeres Chartbild.")]
        public bool ShowLabels { get => _showLabels; set { _showLabels = value; RedrawChart(); } }

        [Display(Name = "Linie durchgehend (aus = ab Signal-Bar)", GroupName = "Darstellung", Order = 12,
            Description = "Aus (Standard): Linie beginnt am Signal-Bar und laeuft nach rechts. " +
                          "An: durchgehende Linie ueber die volle Chart-Breite (auch links vom Signal sichtbar).")]
        public bool FullWidthLines { get => _fullWidthLines; set { _fullWidthLines = value; RedrawChart(); } }

        [Display(Name = "Schriftgroesse", GroupName = "Darstellung", Order = 13,
            Description = "Schriftgroesse der Delta-Labels an den Levels.")]
        [Range(8, 24)]
        public int FontSize { get => _fontSize; set { _fontSize = Math.Clamp(value, 8, 24); BuildFonts(); RedrawChart(); } }

        [Display(Name = "Farbe Kauf-Outlier", GroupName = "Farben", Order = 20,
            Description = "Linien- und Labelfarbe fuer Kauf-Outlier (positives Delta, aggressive Kaeufer).")]
        public Color ColorUp { get => _colorUp; set { _colorUp = value; RedrawChart(); } }

        [Display(Name = "Farbe Verkauf-Outlier", GroupName = "Farben", Order = 21,
            Description = "Linien- und Labelfarbe fuer Verkauf-Outlier (negatives Delta, aggressive Verkaeufer).")]
        public Color ColorDown { get => _colorDown; set { _colorDown = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  CTOR
        // ─────────────────────────────────────────────────────────────────
        public LevelMarker() : base(true)
        {
            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            DataSeries[0].IsHidden = true;

            // WICHTIG: Ohne dieses Abo zeichnet ATAS nur, wenn sich der letzte Bar
            // aendert (DrawingLayouts.LatestBar). Dann verschwindet alles, sobald
            // man vom aktuellsten Bar wegnavigiert (Drag in die Vergangenheit, Zoom,
            // ZoomXY). Historical = bei Chart-Bewegung/Zoom, Final = bei jedem
            // Neuzeichnen (z.B. Mausbewegung) -> Linien bleiben immer sichtbar.
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
            // Bei vollstaendiger Neuberechnung (Reload, Property-Aenderung) faengt
            // ATAS wieder bei bar == 0 an -> State zuruecksetzen.
            if (bar == 0)
                ResetState();

            // Jeden ABGESCHLOSSENEN Bar genau einmal auswerten. Der sich bildende
            // Bar (CurrentBar - 1) hat noch kein finales Delta und wird ausgelassen.
            int lastClosed = CurrentBar - 2;
            while (_lastProcessedBar < lastClosed)
            {
                _lastProcessedBar++;
                ProcessClosedBar(_lastProcessedBar);
            }

            // Nur bei tatsaechlicher Aenderung neu zeichnen.
            if (bar == CurrentBar - 1 && _levels.Count != _lastDrawnCount)
            {
                _lastDrawnCount = _levels.Count;
                RedrawChart();
            }
        }

        private void ResetState()
        {
            _levels.Clear();
            _absWindow.Clear();
            _lastProcessedBar = -1;
            _lastDrawnCount = -1;
        }

        // Wertet einen abgeschlossenen Bar aus: testet auf Delta-Outlier ggue. dem
        // rollierenden Fenster der vorhergehenden Bars, danach Fenster aktualisieren.
        private void ProcessClosedBar(int bar)
        {
            var c = GetCandle(bar);
            if (c == null)
                return;

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

                // Outlier nur bei echter Streuung und Ueberschreiten der Schwelle.
                if (std > 0m && absDelta >= threshold && absDelta >= _minAbsDelta)
                    AddLevel(bar, c);
            }

            // Aktuellen |Delta| ins Fenster aufnehmen, Fenstergroesse begrenzen.
            _absWindow.Enqueue(absDelta);
            while (_absWindow.Count > _lookbackBars)
                _absWindow.Dequeue();
        }

        private void AddLevel(int bar, IndicatorCandle c)
        {
            bool up = c.Delta >= 0;
            decimal price = _anchor switch
            {
                LevelAnchor.Close => c.Close,
                LevelAnchor.Extreme => up ? c.High : c.Low,
                _ => GetPoc(c)
            };

            _levels.Add(new Level(bar, price, c.Delta, up));
            while (_levels.Count > _maxLevels)
                _levels.RemoveAt(0);
        }

        // vPOC eines Bars = Preis-Level mit dem groessten Volumen im Footprint.
        // Fallback auf Close, falls keine Cluster-Daten vorliegen.
        private static decimal GetPoc(IndicatorCandle c)
        {
            decimal pocPrice = c.Close;
            decimal maxVol = -1m;
            foreach (var pv in c.GetAllPriceLevels())
            {
                if (pv.Volume > maxVol)
                {
                    maxVol = pv.Volume;
                    pocPrice = pv.Price;
                }
            }
            return pocPrice;
        }

        // ─────────────────────────────────────────────────────────────────
        //  RENDER
        // ─────────────────────────────────────────────────────────────────
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (_font == null || _levels.Count == 0)
                return;

            // Dokumentierte Koordinaten-API: ChartInfo.PriceChartContainer.
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;
            int lastBar = CurrentBar - 1;
            if (lastBar < 0)
                return;

            foreach (var lvl in _levels)
            {
                int y;
                try { y = cont.GetYByPrice(lvl.Price, false); }
                catch { continue; }

                var col = lvl.Up ? _colorUp : _colorDown;

                int x1 = region.Left;          // Default: durchgehend, immer sichtbar
                int x2 = region.Right;

                if (!_fullWidthLines)
                {
                    // Ray-Modus: Linie beginnt am Signal-Bar und laeuft nach rechts.
                    // Bar-Index wird hart auf den gueltigen Bereich geclampt, damit
                    // ein nach Historie-Nachladen veralteter Index GetXByBar nicht
                    // zum Werfen bringt (das wuerde sonst das Rendering abbrechen).
                    try
                    {
                        int b = Math.Min(Math.Max(lvl.Bar, 0), lastBar);
                        int xOrigin = cont.GetXByBar(b, false);
                        if (xOrigin > region.Right)
                            continue;                       // Ursprung rechts ausserhalb -> nichts sichtbar
                        x1 = Math.Max(xOrigin, region.Left);
                    }
                    catch { x1 = region.Left; }             // im Zweifel ueber volle Breite
                }

                context.DrawLine(new RenderPen(col, _lineWidth), x1, y, x2, y);

                if (_showLabels)
                {
                    // Label getrennt absichern: ein Label-Fehler darf die Linie nie kippen.
                    try
                    {
                        string txt = $"Δ {lvl.Delta:+0;-0}";
                        var sz = context.MeasureString(txt, _font);
                        int b = Math.Min(Math.Max(lvl.Bar, 0), lastBar);
                        int xOrigin = cont.GetXByBar(b, false);
                        int labelX = Math.Min(Math.Max(xOrigin + 3, region.Left + 3),
                                              region.Right - sz.Width - 3);
                        context.DrawString(txt, _font, col, labelX, y - sz.Height - 1);
                    }
                    catch { /* Label-Position nicht ermittelbar -> Label diesmal weglassen */ }
                }
            }
        }
    }
}
