DeltaBasedIndicators — Flag-Only Indicator

Overview
- Purpose: emits four discrete signals derived from Volume Delta (VD) relationships with price and volume.
- Non‑repainting: computes on the previous bar (HistoricalData[1]) to avoid mid‑bar flips.
- Robustness: guards against division by zero and warms up rolling windows before comparisons.

Inputs
- APAVD Settings:
  - Delta: Use Median: if true, price move uses Median; else |Close − Open|.
  - Delta: Threshold Multiplier: multiplier applied to the APAVD baseline for the CPVD comparison.
  - Delta: Lookback: window for average price move and average VD.
- VD Strength Settings:
  - Delta Strength: Lookback: window for average |VD|.
  - Delta Strength: Threshold Multiplier: multiplier applied to average |VD| for strength comparison.
- VD/Volume Settings:
  - VDtV Lookback: window for averages in VD/Volume.
  - VDtV Threshold: multiplier applied to the baseline ratio avg(|VD|)/avg(Vol).
- Advanced:
  - Force Volume Ready: if true, bypasses volume‑analysis readiness gate (useful during warm‑up/testing).

Output Lines (Flag‑Only)
- Line 0 — APAVD_Flag (−1/0/+1):
  - Definitions: priceMove = |Close − Open| (or Median), VD = |buy − sell|.
  - APAVD = avg(priceMove) / avg(VD) over Lookback.
  - CPVD = priceMove / VD on current bar.
  - +1 if CPVD > APAVD × Threshold and VD > 0; −1 if CPVD > APAVD × Threshold and VD < 0; else 0.
- Line 1 — VD_Strength_Flag (−2/0/+2):
  - +2 if |VD| > avg(|VD|) × Strength Threshold and VD > 0; −2 if VD < 0; else 0.
- Line 2 — VD_Price_Divergent_Flag (−3/0/+3):
  - Price direction = sign(Close − Open). Divergence = VD sign ≠ price direction.
  - +3 if price falling and VD > 0; −3 if price rising and VD < 0; else 0.
- Line 3 — VD_to_Volume_Flag (−4/0/+4):
  - Compare (|VD| / Volume) to baseline avg(|VD|)/avg(Volume) × VDtV Threshold.
  - +4 if above baseline and VD > 0; −4 if above baseline and VD < 0; else 0.

Behavior & Warm‑up
- Each rolling window is filled before averages are used; until then, flags remain neutral (0).
- Division safeguards ensure no NaN/Infinity results; neutral values are emitted when data is insufficient.



