# Level Marker (Delta-Outlier) — ATAS Indikator

Eigenentwickelter ATAS-Orderflow-Indikator (C#) für Futures-Trading (NQ/MNQ, ES/MES).
Markiert **Levels of Interest** auf dem Tick-/Range-Chart und projiziert sie nach rechts.
**Rein informativ — kein Entry-Signal.**

> Teil eines mehrstufigen Projekts (Modul 3 — Pro-Bar-Orderflow): Bias-Dashboard →
> **Level-Marker** → Confluence-Score.

## Zwei zuschaltbare Detektoren

### 1. Delta-Outlier (Default an)

Bei jedem abgeschlossenen Bar: *„Ist die Aggression ungewöhnlich groß im Vergleich zu
dem, was gerade normal ist?"* — statistischer Ausreißer des `|Delta|`:

```
Outlier, wenn:  |Delta| ≥ Mittelwert + k · σ   UND   |Delta| ≥ Min.|Delta|
```

- 🟢 **Grün** = Kauf-Outlier (positives Delta) · 🔴 **Rot** = Verkauf-Outlier (negatives Delta)
- Die Schwelle ist **relativ** zur Streuung der letzten Bars → passt sich automatisch an
  ruhige bzw. wilde Phasen an.

### 2. Zero Prints (Default aus)

Footprint-Level, auf denen eine Seite **gar nicht** gehandelt hat (`Bid = 0` oder `Ask = 0`).
Solche „unfairen" Preise sind oft **magnetische Zielzonen**.

- Farbe Amber, Label `ZP`.
- **Verschwinden bei Berührung:** sobald der Preis zurückkommt, ist der Zero Print „gefüllt"
  und wird entfernt. Stehen bleiben nur die *unberührten* (naked) Zero Prints.

> **Hinweis:** Volumenprofil-Zonen wie **LVN/HVN/vPOC/Naked POC** gehören nicht hierher,
> sondern in den separaten Indikator **[ProfileLevels](https://github.com/giucino/ProfileLevels)**
> (Zeitchart M5/M15). LVN wurde bewusst aus diesem Indikator entfernt.

## Empfohlener Chart

Tick-Charts (z.B. NQ 900-Tick) sind ideal — gleich viele Trades pro Bar machen das Delta
vergleichbar. Range-Bars gehen ebenfalls gut; Zeit-Charts sind schwächer.

## Einstellungen (Kurzüberblick)

| Gruppe | Einstellung |
|---|---|
| Delta-Outlier | aktiv, Lookback, Min. Bars, **Outlier-Schwelle (k)**, Min.\|Delta\|, Level-Anker |
| Zero Prints | aktiv, Min. Volumen (gehandelte Seite) |
| Darstellung | Max. Zonen pro Typ, Linienbreite, Labels, Linie durchgehend, Schriftgröße |
| Farben | Kauf-/Verkauf-Outlier, Zero Print |

Ausführliche Erklärung & Interpretation: siehe [`LevelMarker_Doku.html`](LevelMarker_Doku.html)
([online](https://giucino.github.io/LevelMarker/LevelMarker_Doku.html)).

## Build & Installation

- TargetFramework `net10.0-windows`, ATAS-DLLs per HintPath referenziert.
- `dotnet build -c Release`, dann `LevelMarker.dll` nach `%APPDATA%\ATAS\Indicators\` kopieren.
- ATAS neu starten bzw. Indikatorliste aktualisieren.

## Lizenz / Hinweis

Private Eigenentwicklung auf Basis allgemein verfügbarer AMT/Orderflow-Konzepte.
Kein Nachbau kommerzieller Fremdprodukte. Nutzung auf eigenes Risiko —
**kein Handelssignal, keine Anlageberatung.**
