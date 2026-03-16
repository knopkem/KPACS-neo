# Entwicklungsplan: Volume Rendering

> Basierend auf Analyse von Zhou/Tönnies 2002 (State of the Art) und Jönsson et al. 2012 (Eurographics STAR: Volumetric Illumination).
> Ziel: KPACS von achsenparallelem Compositing auf modernen CPU Ray Caster mit Beleuchtung bringen.

---

## Ist-Zustand

| Datei | Was da ist |
|---|---|
| `SeriesVolume.cs` | 3D `short[]` Volumen, trilineare Interpolation, Koordinatentransformation |
| `VolumeReslicer.cs` | Achsenparallele Slab-Projektionen: MPR, MIP, MinIP, Compositing (MpVrt) |
| `DicomPixelRenderer.cs` | 2D BGRA32 mit LUT-Windowing |
| `DicomColorLut.cs` | 6 LUT-Schemata |

**Problem:** Das MpVrt-Compositing iteriert nur entlang einer festen Achse, hat eine hardcoded Transfer Function (`Pow(normalized, 1.6) * 0.35`), kein Shading, keine Schatten. Das ist der Stand von ~2000.

---

## Phase 1: Ray Caster + Gradient Shading

> **Ziel:** 3D Volume Rendering aus beliebiger Blickrichtung mit Phong-Beleuchtung.
> **Dauer:** ~5 Arbeitstage

### Tag 1: Datenmodell + Gradient-Volume

- [ ] **`VolumeRenderState.cs`** — Kamera- und Renderparameter
  - `CameraPosition`, `CameraTarget`, `CameraUp` (Vector3)
  - `ProjectionMode` (Perspective / Orthographic)
  - `FieldOfView` / `OrthoSize`
  - `LightDirection` (normalisiert, relativ zur Kamera oder Welt)
  - Phong-Koeffizienten: `AmbientK`, `DiffuseK`, `SpecularK`, `Shininess`
  - `SamplingStepFactor` (1.0 = Voxel-Spacing, 0.5 = Oversampling)
  - `OutputWidth`, `OutputHeight`

- [ ] **`VolumeGradientVolume.cs`** — Vorberechnetes 3D Gradientenfeld
  - Zentrale Differenzen: `∇f(x) = (f(x+1)-f(x-1)) / (2·spacing)` pro Achse
  - Speicher: `float[]` mit 3 Kanälen (Gx, Gy, Gz) oder `Vector3[]`
  - Berechnung: `Parallel.For` über Slices
  - Zugriff: `GetGradient(ix, iy, iz)` → `Vector3`, `SampleGradientTrilinear(worldPos)` → `Vector3`
  - Gradient-Magnitude: `|∇f|` für spätere TF-Modulation
  - Lazy: erst bei erstem DVR-Aufruf berechnen, dann cachen bis Volume sich ändert
  - **Speicher-Risiko:** 512³×3×4 = 1.5 GB → Mitigation: `Half`-Precision (768 MB) oder On-the-fly mit Slice-Cache

### Tag 2–3: Ray Caster Core

- [ ] **`VolumeRayCaster.cs`** — Der Ray Marcher
  ```
  public byte[] Render(SeriesVolume volume, VolumeGradientVolume gradients,
                       VolumeTransferFunction tf, VolumeRenderState state)
  ```
  **Algorithmus pro Pixel:**
  1. Ray Origin + Direction aus Kamera berechnen (Perspective oder Ortho)
  2. Ray-AABB-Intersection mit Volume Bounding Box → `tNear`, `tFar`
  3. Front-to-back March in Schritten `Δt`:
     - `worldPos = origin + t × direction`
     - `value = volume.SampleTrilinear(worldPos)`
     - `(color, α) = tf.Lookup(value)` — zunächst simple 1D-Tabelle
     - Falls `α > ε`:
       - `gradient = gradients.SampleTrilinear(worldPos)`
       - `normal = normalize(gradient)`
       - Phong: `I = ambient + diffuse × max(N·L, 0) + specular × max(N·H, 0)^s`
       - `color *= I`
     - Compositing: `C_acc += (1 - α_acc) × color × α`, `α_acc += (1 - α_acc) × α`
     - **Early termination:** `if (α_acc > 0.99) break`
  4. Output: BGRA32 Pixel

  **Performance-Maßnahmen:**
  - `Parallel.For` über Bildzeilen
  - Empty-Space Skipping: Volume Bounding Box reicht zunächst, später Octree
  - Adaptive Step Size: grobe Schritte (`2×Δt`) wenn letzte N Samples `α ≈ 0`, fein bei Boundaries

### Tag 4: Parallelisierung + Progressive Rendering

- [ ] **Progressive Rendering Pipeline**
  - Bei Kamerabewegung: erst 1/4-Auflösung rendern (schnell), dann in Background volle Auflösung
  - Debounce: 100ms nach letztem Input → Start Full-Res Render
  - `CancellationToken` → bei neuem Input altes Rendering abbrechen
  - Rendering-Status anzeigen (Fortschrittsbalken oder "Rendering…" Label)

- [ ] **Basis-Transfer-Function** (temporär, wird in Phase 2 ersetzt)
  - Einfache lineare Rampe mit Window/Level als Fallback
  - Oder: direkt das bestehende Windowing als TF interpretieren

### Tag 5: UI-Integration

- [ ] **`DicomViewPanel.VolumeRender.cs`** — UI-Anbindung
  - Neuer `VolumeProjectionMode.Dvr` in der bestehenden Enum
  - Kamera-Orbit: Mausdrag → Rotation um Volume-Zentrum (Trackball)
  - Zoom: Mausrad → Kamera näher/weiter
  - Pan: Shift+Drag → Kamera verschieben
  - Licht folgt standardmäßig der Kamera (Head-Light), optional fixierbar
  - Toggle in der Toolbar: DVR neben MIP/MinIP/MPR

- [ ] **Erster Test mit Testdaten**
  - `Testfiles/ABDOM_*.dcm` als Testserie laden
  - Visueller Vergleich: MpVrt (alt) vs. Dvr (neu)
  - Performance-Messung: ms pro Frame loggen

> **✅ Meilenstein Phase 1:** Volume Rendering aus beliebiger Richtung mit Phong-Beleuchtung funktioniert.

---

## Phase 2: Transfer Function System

> **Ziel:** Konfigurierbare Zuordnung von Dichtewert → Farbe + Opazität mit medizinischen Presets.
> **Dauer:** ~3 Arbeitstage

### Tag 6: Transfer Function Modell

- [ ] **`VolumeTransferFunction.cs`** — Datenmodell
  - `ControlPoint`: `(float Value, byte R, byte G, byte B, float Alpha)`
  - `List<ControlPoint>` mit Spline-Interpolation (Catmull-Rom oder linear)
  - Vorberechnete Lookup-Tabelle: `uint[4096]` (12-bit Auflösung, RGBA gepackt)
  - `Rebuild()` → Lookup bei jeder Control-Point-Änderung neu berechnen
  - `Lookup(short value)` → `(R, G, B, α)` — inline-fähig, hot path
  - Optional: `GradientModulation(float gradientMagnitude)` → α-Multiplikator
    - Kindlmann-Prinzip: Grenzregionen (hoher Gradient) → hohe Opazität, homogene Regionen → transparent

- [ ] **CT-Presets** als statische Factory-Methoden
  - `CreateBone()` — HU 200–1500, weiß/elfenbein, hohe Opazität
  - `CreateSoftTissue()` — HU 40–400, beige/rot, mittlere Opazität
  - `CreateLung()` — HU -950 bis -500, blau, niedrige Opazität
  - `CreateAngio()` — HU 150–500, rot, hohe Opazität, Gradient-Gate aktiv
  - `CreateSkin()` — HU -200 bis 200, hautfarben, sehr niedrige Opazität
  - `CreateComposite()` — Multi-Material: Knochen + Weichgewebe + Haut gestaffelt

### Tag 7–8: Transfer Function Editor UI

- [ ] **`TransferFunctionEditor.axaml` / `.axaml.cs`** — Avalonia Control
  - Canvas mit:
    - **Histogramm** des aktuellen Volumens als grauer Hintergrund
    - **Opacity-Kurve** (Control Points, drag-bar, Spline-Interpolation)
    - **Farbband** unter der Kurve (Color Gradient)
    - **Preset-Dropdown** oben (Bone, Soft Tissue, Lung, Angio, …)
  - Interaktion:
    - Klick auf Kurve → neuen Control Point einfügen
    - Drag → Control Point verschieben
    - Rechtsklick → Control Point löschen oder Farbe ändern (ColorPicker)
    - Änderung → Event `TransferFunctionChanged` → Live-Re-Render
  - Histogramm:
    - `volume.ComputeHistogram()` → `int[4096]` Bins
    - Logarithmische Y-Achse (medizinische Daten haben extreme Spikes bei Luft/Knochen)

- [ ] **Integration in StudyViewerWindow**
  - TF-Editor als Panel im Viewer (ein-/ausklappbar)
  - Preset-Wechsel mit sofortigem Re-Render
  - TF-State in `VolumeRenderState` speichern/laden

> **✅ Meilenstein Phase 2:** Nutzer kann per Maus Transfer Functions editieren und zwischen CT-Presets wechseln.

---

## Phase 3: Directional Occlusion (Soft Shadows)

> **Ziel:** Globale Schatten für deutlich bessere Tiefenwahrnehmung.
> User-Studien (Lindemann & Ropinski 2011) belegen: Directional Occlusion hat die besten perzeptuellen Eigenschaften.
> **Dauer:** ~2.5 Arbeitstage

### Tag 9: Shadow Rays

- [ ] **Shadow Ray Integration in `VolumeRayCaster`**
  - Pro sichtbarem Sample (α > Schwelle):
    - Sekundären Strahl in Lichtrichtung marchen
    - Opazität entlang Lichtstrahl akkumulieren → Transmission `T ∈ [0,1]`
    - `diffuse *= T`, `specular *= T`
  - Shadow-Ray Step Size: gröber als View-Ray (2–3× Δt) für Performance
  - Max Shadow Distance: nicht unendlich, sondern begrenzt (z.B. 100 Voxel) um Performance zu halten

- [ ] **Cone Soft Shadows**
  - Statt eines Shadow-Rays: 3–5 jittered Rays in einem Cone
  - Cone-Apertur θ: konfigurierbar, Default 8°
  - Mittelwert der Transmissionen → weiche Schattenränder
  - Adaptive: bei hoher Transmission (wenig Verdeckung) genügt 1 Ray

### Tag 10: Occlusion Cache + Performance

- [ ] **Shadow-Volume Cache**
  - 3D `float[]` Array in Volume-Auflösung (oder halber Auflösung)
  - Vorberechnung in Background-Task:
    - Pro Voxel: Transmission entlang Lichtrichtung berechnen
    - Parallelisiert über Slices senkrecht zur Lichtrichtung
  - Ray Caster liest aus Cache statt Shadow-Rays zu marchen → massive Beschleunigung
  - Invalidierung: bei Lichtrichtungs- oder TF-Änderung → Neuberechnung
  - Progressive: erst grobe Schatten (jedes 4. Voxel), dann verfeinern

- [ ] **UI: Licht-Richtung**
  - Licht-Richtung per Drag-Gizmo einstellbar (kleiner Kreis in der Ecke)
  - Oder: Licht folgt Kamera mit konfigurierbarem Offset-Winkel
  - Shadow Intensity Slider (0 = aus, 1 = volle Schatten)

> **✅ Meilenstein Phase 3:** Weiche Schatten unter Knochen, zwischen Organen, in Gefäßbäumen. Deutlich bessere räumliche Wahrnehmung.

---

## Phase 4: Local Ambient Occlusion

> **Ziel:** Zusätzliche Tiefenhinweise durch umgebungsbasierte Verdeckung — auch ohne gerichtetes Licht.
> Basierend auf Dynamic Ambient Occlusion (Ropinski et al. 2008).
> **Dauer:** ~3.5 Arbeitstage

### Tag 11: Precomputed Ambient Occlusion

- [ ] **AO-Vorberechnung in `VolumeIllumination.cs`**
  - Pro Voxel: hemisphärische Sichtbarkeit in Radius R berechnen
  - Approximation: 32–64 Strahlen in stratifizierten Richtungen (quasi-random)
  - Opazität entlang jedes Strahls akkumulieren → Verdeckungsgrad
  - Mittelwert → Occlusion-Faktor `O(x) ∈ [0,1]`
  - Ergebnis: `float[]` 3D-Array, 1 Wert pro Voxel
  - Berechnung: Background-Task, geschätzt 10–60 Sek. für 512³

### Tag 12–13: Dynamic AO mit Histogrammen

- [ ] **Histogram-basierte AO (Transfer-Function-unabhängig vorberechnen)**
  - Pro Voxel: lokales Dichte-Histogramm der Nachbarschaft (Radius R) berechnen
  - Histogramm-Bins: 256 Bins genügen
  - Clustering: K-Means oder ähnlich → ~256 repräsentative Histogramme
  - Speicher: Cluster-ID Volume (1 Byte/Voxel) + Histogramm-Tabelle (256×256 Bytes)
  - **Vorteil:** Bei TF-Änderung → Histogramme × neue TF-Opazitäten = neuer AO-Wert, **ohne Neuberechnung der Basis**
  - `O(x) = Σ_j (α_TF(j) × Histogram_cluster(x)[j])` — eine Dot-Product-artige Operation, sehr schnell

- [ ] **Integration in Ray Caster**
  - `L_ambient(x) = (1 - O(x)) × k_ambient` statt konstantem Ambient
  - AO-Intensität als Slider (0 = aus, 1 = voll)
  - AO-Radius als Parameter (klein = feine Details, groß = großflächige Verdeckung)

### Tag 14: Polish + Kombination

- [ ] **Shadow + AO zusammen**
  - Shadow Rays für gerichtete Schatten + AO für umgebungsbasierte Verdeckung
  - Gewichtung: `illumination = diffuse×T + specular×T + ambient×(1-O)`
  - Visueller Vergleich: nur Phong vs. Phong+Shadows vs. Phong+Shadows+AO
  - Performance-Profiling: Gesamtzeit pro Frame, Breakdown nach Komponente

- [ ] **Finale Tests**
  - CT Abdomen (Testfiles)
  - CTA (falls Testdaten vorhanden) — Gefäßbaum mit Schatten
  - MRT-Kompatibilität testen (Gradient-Smoothing bei Bedarf aktivieren)
  - Edge Cases: sehr kleine Volumes, sehr große Volumes, anisotropes Spacing

> **✅ Meilenstein Phase 4:** Vollständiges Illuminationsmodell: Phong + Directional Shadows + Ambient Occlusion.

---

## Dateien-Übersicht (neu / geändert)

```
KPACS.Viewer.Avalonia/
  Rendering/
    VolumeRayCaster.cs            ← NEU Phase 1: Ray Marcher Engine
    VolumeGradientVolume.cs       ← NEU Phase 1: Gradientenfeld
    VolumeTransferFunction.cs     ← NEU Phase 2: TF Modell + Presets
    VolumeIllumination.cs         ← NEU Phase 3+4: Shadows + AO
    VolumeReslicer.cs             ← ÄNDERN: neuer Mode Dvr
    SeriesVolume.cs               ← MINIMAL: ggf. Hilfsmethoden
  Controls/
    DicomViewPanel.VolumeRender.cs ← NEU Phase 1: Kamera-UI, DVR-Integration
    TransferFunctionEditor.axaml   ← NEU Phase 2: TF-Editor UI
    TransferFunctionEditor.axaml.cs
  Models/
    VolumeRenderState.cs          ← NEU Phase 1: Kamera/Licht/TF State
```

---

## Risiken & Mitigierungen

| Risiko | Mitigation |
|---|---|
| CPU zu langsam für interaktive Raten | Progressive Rendering (1/4 Auflösung bei Interaktion), Caching bei Stillstand |
| Gradient-Volume Speicher (1.5 GB bei 512³) | Half-Precision oder On-the-fly mit Slice-Cache |
| Gradienten verrauscht bei MRT | Optionaler Gauss-Vorfilter (3×3×3 oder 5×5×5) |
| TF-Editor UX zu komplex | Presets als primärer Einstieg, freies Editieren optional |
| Shadow-Berechnung zu langsam | Shadow-Volume Cache, gröbere Auflösung, progressive Verfeinerung |

---

## Was bewusst draußen bleibt

- **GPU Ray Casting** — separates Folgeprojekt (Silk.NET / ComputeSharp)
- **Multiple Scattering** — marginaler Gewinn über Single Scattering
- **Spherical Harmonics** — Overkill für diagnostische Anwendungen
- **Shear-Warp / Splatting / Fourier** — veraltet
- **NPR-Effekte** — kein diagnostischer Mehrwert
- **Cinematic Rendering** (Monte Carlo) — braucht GPU
- **Multi-Volume Fusion** (PET/CT) — eigenes Feature, nicht Teil dieses Plans
