# Level Marker (Delta-Outlier) — ATAS Indikator

Eigenentwickelter ATAS-Orderflow-Indikator (C#) für Futures-Trading (NQ/MNQ, ES/MES).
Er markiert Bars mit **außergewöhnlich hohem Delta** und projiziert den zugehörigen
Level. **Rein informativ — kein Entry-Signal.**

> Teil eines mehrstufigen Projekts: Bias-Dashboard → **Level-Marker** → Confluence-Score.

## Was er macht

Bei jedem abgeschlossenen Bar wird gefragt: *„Ist die Aggression in diesem Bar
ungewöhnlich groß im Vergleich zu dem, was gerade normal ist?"* — gemessen als
statistischer Ausreißer des `|Delta|` gegenüber einem rollierenden Fenster:

```
Outlier, wenn:  |Delta| ≥ Mittelwert + k · σ   UND   |Delta| ≥ Min.|Delta|
```

- 🟢 **Grün** = Kauf-Outlier (positives Delta)
- 🔴 **Rot** = Verkauf-Outlier (negatives Delta)

Die Schwelle ist **relativ** zur Streuung der letzten Bars und passt sich damit
automatisch an ruhige bzw. wilde Marktphasen an.

## Empfohlener Chart

Tick-Charts (z.B. NQ 900-Tick) sind ideal — gleich viele Trades pro Bar machen
das Delta vergleichbar. Range-Bars gehen ebenfalls gut; Zeit-Charts sind schwächer.

## Einstellungen (Kurzüberblick)

| Gruppe | Einstellung | Zweck |
|---|---|---|
| Erkennung | Lookback, Min. Bars, **Outlier-Schwelle (k)**, Min.\|Delta\|, Level-Anker, Max. Level | Erkennungslogik |
| Darstellung | Linienbreite, Labels, Linie durchgehend, Schriftgröße | Optik |
| Farben | Kauf-/Verkauf-Outlier | Farben je Delta-Vorzeichen |

Ausführliche Erklärung & Interpretation: siehe [`LevelMarker_Doku.html`](LevelMarker_Doku.html).

## Build & Installation

- TargetFramework `net10.0-windows`, ATAS-DLLs per HintPath referenziert.
- `dotnet build -c Release`, dann `LevelMarker.dll` nach `%APPDATA%\ATAS\Indicators\` kopieren.
- ATAS neu starten bzw. Indikatorliste aktualisieren.

## Lizenz / Hinweis

Private Eigenentwicklung auf Basis allgemein verfügbarer AMT/Orderflow-Konzepte.
Kein Nachbau kommerzieller Fremdprodukte. Nutzung auf eigenes Risiko —
**kein Handelssignal, keine Anlageberatung.**
