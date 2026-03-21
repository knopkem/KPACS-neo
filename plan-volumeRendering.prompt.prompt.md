# Workspace: Volume Rendering — Analyse & Implementierungsplan

## 1. Literatur-Zusammenfassung

### Paper A: Zhou & Tönnies (2002) — "State of The Art for Volume Rendering"

Klassischer Überblick über die Volume-Rendering-Landschaft um 2000. Die Kerntechniken:

| Algorithmus | Qualität | Performance | Kerneigenschaft |
|---|---|---|---|
| **Ray Casting** | ★★★ | ★ | Goldstandard-Qualität, trilineare Interpolation, early ray termination, post-classified |
| **Splatting** | ★★★ | ★ | Object-order, Gauss-Kernel, nur relevante Voxel |
| **Shear-Warp** | ★★ | ★★★ | Schnellster Software-Renderer, aber Bilinear-Artefakte, 3× Speicherkopien |
| **3D Texture Mapping** | ★★ | ★★★ | GPU-Hardware, aber nur 8–12 bit Framebuffer, Color-Bleeding |
| **MIP** | ★ (speziell) | ★★★ | Gefäßdarstellung, kein Shading, keine Tiefe |
| **Fourier Domain** | ★ | ★★ | Nur X-ray-artige Bilder, hoher Speicher, praktisch nutzlos |

**Aktive Forschungsthemen 2000:**
- Transfer Functions (1D value→color/opacity, multi-dimensional mit Gradient)
- NPR-Enhanced Volume Rendering (Silhouetten, Boundaries, Hatching)
- Hybrid Rendering (verschiedene Methoden für verschiedene Strukturen)
- Segmentation + Volume Rendering Kombination
- Hardware-Beschleunigung (damals: NV Register Combiners, Pixel Shader v1.0)

**Kernaussage:** Ray Casting liefert die beste Qualität, DVR mit fuzzy Transfer Function schlägt Surface Rendering für medizinische Daten. Transfer Function Design ist ein Top-10-Problem.

---

### Paper B: Jönsson et al. (2012, Eurographics) — "Interactive Volume Rendering with Volumetric Illumination"

Moderner STAR-Report über fortgeschrittene Beleuchtungsmodelle. Klassifiziert Techniken in 5 Gruppen:

| Gruppe | Technik | GPU-gebunden? | Lichtquellen | Schatten | Scattering | Perf. |
|---|---|---|---|---|---|---|
| **Local Region** | Gradient-based Shading (Blinn-Phong) | Nein | N×D,P | Keine | Nein | ★★★ |
| | Local Ambient Occlusion | Nein | AMB | Lokal/Soft | Nein | ★★ |
| | Dynamic Ambient Occlusion | Nein | D,P,AMB | Lokal/Soft | Basic | ★★ |
| **Slice Based** | Half Angle Slicing | Ja (Slicing) | 1×D,P | Global/Hard | Multiple | ★★ |
| | **Directional Occlusion Shading** | Ja (Slicing) | 1×P (Kamera) | Global/Soft | Single | ★★ |
| **Light Space** | Deep Shadow Mapping | Nein | 1×D,P | Global/Hard | Nein | ★★ |
| | Image Plane Sweep | Ja (Ray) | 1×D,P | Global | Multiple | ★★ |
| **Lattice Based** | Shadow Volume Propagation | Nein | 1×D,P,A | Global | Multiple | ★ |
| **Basis Function** | Spherical Harmonics | Nein | N×D,P,A,T,AMB | Global | Multiple | ★ |

**Perceptual Key Finding:** User-Studien zeigen, dass **Directional Occlusion Shading** die besten perzeptuellen Fähigkeiten hat (Tiefenwahrnehmung, Größeneinschätzung).

**Kernaussage:** Der Sprung von lokaler Emission+Absorption zu globalem Shadowing liefert den größten perzeptuellen Gewinn. Schatten = Tiefenwahrnehmung. Scattering = Materialrealismus. Gradient-basiertes Shading allein ist der Baseline-Sprung über reines Compositing.

---

## 2. Ist-Analyse: Unser Codebase

```
SeriesVolume.cs          — 3D short[] Volumen, trilineare Interpolation ✅
VolumeReslicer.cs        — Achsenparallele Slab-Projektionen:
                            Mpr (Average), MipPr (MIP), MinPr (MinIP),
                            MpVrt (Alpha Compositing) ✅
DicomPixelRenderer.cs    — 2D BGRA32 Rendering mit LUT-Windowing ✅
DicomColorLut.cs         — 6 LUT-Schemata (Grayscale, Hot Iron, etc.) ✅
```

### Was fehlt (in der Reihenfolge des Impacts):

| # | Feature | Impact | Aufwand |
|---|---|---|---|
| 1 | **Ray Casting von beliebigen Blickrichtungen** | ★★★ | Hoch |
| 2 | **Gradient-basiertes Shading (Blinn-Phong)** | ★★★ | Mittel |
| 3 | **Konfigurierbare Transfer Functions** | ★★★ | Mittel |
| 4 | **Directional Occlusion / Soft Shadows** | ★★ | Hoch |
| 5 | **Pre-integrated Volume Rendering** | ★★ | Mittel |
| 6 | GPU-Beschleunigung | ★★ | Sehr hoch |
| 7 | Multiple Scattering | ★ | Sehr hoch |
| 8 | Spherical Harmonics Lighting | ★ | Sehr hoch |

---

## 3. Entscheidungsmatrix: Build vs. Skip

### ✅ EINBAUEN

#### Phase 1: CPU Ray Caster mit Gradient Shading (Fundament)
**Warum:** Unser bestehendes MpVrt-Compositing ist achsenparallel — es iteriert nur entlang einer festen Achse. Das ist das "20 Jahre alte" Modell, dem viele Programme nachhängen. Ein echter Ray Caster mit beliebiger Blickrichtung ist der größte Qualitätssprung.

- **Arbitrary View Direction Ray Casting**: Strahlen von einer virtuellen Kamera durch das Volumen, trilineare Interpolation (haben wir schon in SeriesVolume), front-to-back Compositing
- **Gradient-basiertes Phong Shading**: Gradienten via zentrale Differenzen, Diffuse + Specular + Ambient als Beleuchtungsmodell → der am meisten zitierte Qualitätssprung über reines Emission+Absorption
- **Early Ray Termination**: Abbruch bei α ≈ 1.0 → signifikante Beschleunigung ohne Qualitätsverlust
- **Adaptive Sampling**: Gröber in homogenen Regionen → weitere Beschleunigung

#### Phase 2: Transfer Function System
**Warum:** Beide Paper identifizieren Transfer Functions als kritischste Komponente. Unser hard-coded `Pow(normalized, 1.6) * 0.35` ist extrem limitiert.

- **1D Transfer Function**: Lookup-Tabelle `scalar value → (R, G, B, α)`, editierbar über Spline-Widget
- **Medizinische Presets**: CT-Standardpresets (Knochen, Weichgewebe, Lunge, Gefäße/CTA) als vordefinierte Kurven
- **Gradient-modulated Opacity**: `α = f(value) × g(|∇|)` — Grenzen zwischen Materialien werden betont, homogene Regionen transparent → das Kindlmann/Kniss-Prinzip aus beiden Papers

#### Phase 3: Directional Occlusion (Soft Shadows)
**Warum:** User-Studien (Jönsson 2012, Lindemann & Ropinski 2011) zeigen: Directional Occlusion hat die beste perzeptuelle Wirkung aller getesteten Illuminationsmodelle.

- **Front-to-back Occlusion Accumulation**: Während des Ray Marching wird die Opazität entlang der Lichtrichtung akkumuliert
- **CPU-Approximation**: Statt GPU-Slicing einen Shadow-Ray pro Sample Richtung Lichtquelle casten (vereinfacht, da wir ohnehin CPU-basiert sind)
- **Cone-based Soft Shadows**: Nicht nur ein einzelner Shadow-Ray, sondern ein kleiner Cone für weiche Schatten (approximiert durch wenige zusätzliche Samples)

#### Phase 4: Ambient Occlusion (Local)
**Warum:** Ambient Occlusion liefert starke Tiefenhinweise auch ohne gerichtetes Licht. Das Dynamic Ambient Occlusion Modell (Ropinski 2008) mit vorberechneten Histogrammen ist für CPU-Implementierung ideal, da die Histogramme einmalig berechnet werden.

- **Precomputed Local Histograms**: Pro Voxel die Nachbarschafts-Dichtverteilung vorberechnen (wie Ropinski, aber mit gröberem Radius da CPU)
- **Transfer-Function-modulated Occlusion**: Histogramme × aktuelle TF → Occlusion-Wert
- **Glow/Emission für markierte Strukturen**: Segmentierte ROIs können als Emitter fungieren

### ⚠️ SPÄTER / OPTIONAL

| Feature | Grund für Verschiebung |
|---|---|
| GPU Ray Casting | Erfordert OpenGL/Vulkan-Integration in Avalonia; lohnt sich erst, wenn CPU-Pipeline steht und funktional bewiesen ist |
| Multiple Scattering | Perzeptueller Gewinn über Single Scattering gering, Aufwand enorm |
| Half Angle Slicing | Gebunden an Slice-Rendering-Paradigma, das wir nicht nutzen |
| Spherical Harmonics | Overkill für medizinische Standardanwendungen |
| Pre-integrated VR | Qualitätsverbesserung marginal bei ausreichend hoher Samplingrate |

### ❌ NICHT EINBAUEN

| Feature | Begründung |
|---|---|
| Shear-Warp | Veraltete Technik mit bekannten Artefakten, nur für Software-Speed relevant (2000er Ära) |
| Splatting | Object-order Paradigma, nicht kompatibel mit unserem image-order Ansatz |
| Fourier Domain Rendering | Praktisch nutzlos, nur X-ray-artige Bilder, hoher Speicher |
| NPR (Hatching, Stippling) | Künstlerische Effekte, kein diagnostischer Mehrwert |
| Shadow Volume Propagation | Hoher Speicher (3D Illumination Volume), langsame Updates |

---

## 4. Architektur-Plan

### Prinzipien
1. **Separation of Concerns**: Ray Marcher, Transfer Function, Illumination Model und Renderer sind unabhängige Komponenten
2. **Progressive Enhancement**: Jede Phase produziert ein lauffähiges, testbares Ergebnis
3. **CPU-first, GPU-ready**: Algorithmen so strukturieren, dass spätere GPU-Portierung möglich ist (keine CPU-spezifischen Hacks)
4. **Integration mit bestehendem Code**: Neuer `VolumeProjectionMode.Dvr` neben MIP/MinIP/MPR

### Dateistruktur (neue/geänderte Dateien)

```
KPACS.Viewer.Avalonia/
  Rendering/
    VolumeReslicer.cs              ← erweitern: neuer Mode VolumeProjectionMode.Dvr
    VolumeRayCaster.cs             ← NEU: Phase 1 — Ray Marcher Engine
    VolumeTransferFunction.cs      ← NEU: Phase 2 — TF Model + Presets
    VolumeGradientVolume.cs        ← NEU: Phase 1 — Vorberechnetes 3D Gradient-Feld
    VolumeIllumination.cs          ← NEU: Phase 3+4 — Shadow + AO Berechnung
    SeriesVolume.cs                ← minimal erweitern: Gradient-Zugriff
  Controls/
    DicomViewPanel.VolumeRender.cs ← NEU: UI-Integration, Kamera-Steuerung
    TransferFunctionEditor.axaml   ← NEU: Phase 2 — Spline-basierter TF-Editor
    TransferFunctionEditor.axaml.cs
  Models/
    VolumeRenderState.cs           ← NEU: Kamera, Licht, TF, Render-Parameter
```

### Detailplan pro Phase

---

### Phase 1: Arbitrary-View Ray Caster + Gradient Shading

**Ziel:** 3D-Volumen aus beliebiger Blickrichtung mit Phong-Beleuchtung rendern.

**Schritt 1.1: `VolumeRenderState`** — Kamera- und Rendering-Zustand
```
- CameraPosition, CameraTarget, CameraUp (Vector3)
- FieldOfView (perspective) oder OrthoSize (parallel)
- LightDirection (Vector3, normalisiert)
- AmbientIntensity, DiffuseIntensity, SpecularIntensity, Shininess
- SamplingStepSize (als Vielfaches des Voxel-Spacing)
- OutputWidth, OutputHeight
```

**Schritt 1.2: `VolumeGradientVolume`** — Vorberechnete Gradienten
```
- Berechnet ∇f für jedes Voxel via zentrale Differenzen: (f(x+1)-f(x-1))/2
- Speichert als Vector3[] (3× float pro Voxel)
- Normalisiert: |∇f| als separater Kanal für TF-Modulation
- Lazy Computation: erst bei erstem DVR-Aufruf, dann gecacht
- Optional: Gauss-Smoothing vor Gradient für verrauschte Modalitäten (MRT)
```

**Schritt 1.3: `VolumeRayCaster`** — Der eigentliche Ray Marcher
```
Algorithmus (pro Pixel):
1. Compute ray origin + direction from camera
2. Intersect ray with volume bounding box (AABB)
3. March front-to-back in steps of Δt:
   a. Sample value via SeriesVolume.SampleTrilinear(worldPos)
   b. Lookup (color, opacity) from Transfer Function
   c. If opacity > 0:
      - Sample gradient via VolumeGradientVolume
      - Compute Phong shading: ambient + diffuse(N·L) + specular(N·H)^s
      - Modulate color by shading
   d. Front-to-back composite: C_out = C_out + (1-α_out) × C_sample × α_sample
   e. Early termination if α_out > 0.99
4. Output BGRA32 pixel
```

**Schritt 1.4: Parallelisierung**
```
- Parallel.For über Bildzeilen (wie bestehende Rendering-Loops)
- Optional: SIMD via Vector<float> für Interpolation
- Adaptive Step Size: große Schritte in Regionen mit α ≈ 0, feine Schritte nahe Boundaries
```

**Schritt 1.5: Integration in `VolumeReslicer` / UI**
```
- Neuer VolumeProjectionMode.Dvr
- Kamera-Orbit per Mausdrag (wie bestehender SlabThickness-Drag)
- Rendering in Background-Task mit Debounce (da CPU-basiert ~100-500ms pro Frame)
```

**Liefert:** Interaktives 3D Volume Rendering mit Beleuchtung. Bereits dramatisch besser als das jetzige achsenparallele Compositing.

---

### Phase 2: Transfer Function System

**Ziel:** Konfigurierbare Zuordnung von Dichtewert → Farbe + Opazität.

**Schritt 2.1: `VolumeTransferFunction`** — Datenmodell
```
- ControlPoints: List<(value, R, G, B, α)> mit Spline-Interpolation
- LookupTable: vorberechnetes ushort[4096] für schnellen Zugriff (12-bit Auflösung)
- GradientModulation: optionale Funktion g(|∇f|) → Opacity-Multiplikator
- Presets: Statische Factory-Methoden für CT-Standard-TFs
```

**Schritt 2.2: CT-Presets** (klinisch bewährt)
```
- Bone:      HU 200–1500, weiß, hohe Opazität
- SoftTissue: HU 40–400, beige/rot, mittlere Opazität
- Lung:      HU -950 – -500, blau/transparent
- Angio/CTA: HU 150–500, rot, hohe Opazität + Gradient-Gate
- Skin:      HU -200 – 200, hautfarben, sehr niedrige Opazität
- Composite: Knochen + Weichgewebe + Haut überlagert
```

**Schritt 2.3: `TransferFunctionEditor`** — Avalonia UI Widget
```
- Horizontale Achse: HU-Werte (oder normalisierte Dichte)
- Vertikale Achse: Opazität
- Farbband darunter: RGB-Farbverlauf
- Control Points: drag-bare Punkte auf der Kurve
- Histogramm-Overlay: Werteverteilung des aktuellen Volumens als Hintergrund
- Live-Preview: bei Änderung → neues Lookup-Table → Re-Render
```

**Liefert:** Der Nutzer kann gezielt anatomische Strukturen ein-/ausblenden und einfärben. Dies löst das "Top-10-Problem" der Volume Visualization.

---

### Phase 3: Directional Occlusion / Shadow Rays

**Ziel:** Globale Schatten für Tiefenwahrnehmung (der größte perzeptuelle Sprung laut User-Studien).

**Schritt 3.1: Single Shadow Ray**
```
- Pro Ray-Sample, das sichtbar ist (α > Schwelle):
  - Sekundären Strahl Richtung Lichtquelle marchen
  - Opazität entlang des Lichtstrahls akkumulieren
  - Ergebnis: Licht-Transmission T(x_l, x) ∈ [0,1]
  - Diffuse/Specular × T modulieren
```

**Schritt 3.2: Cone Soft Shadows (Jitter)**
```
- Statt eines Shadow-Rays: 3–5 leicht gestreute Rays in einem Cone
- Mittelwert der Transmissionen → weiche Schatten
- Cone-Apertur θ ≈ 5–15° (konfigurierbar)
- Adaptive: wenige Rays in offensichtlich unverdeckten Regionen
```

**Schritt 3.3: Occlusion Cache (Performance)**
```
- Schatten ändern sich nicht bei jedem Frame (nur bei TF/Licht-Änderung)
- Shadow Volume als 3D float[]-Array vorberechnen (Hintergrund-Task)
- Ray Caster liest aus Cache statt Shadow Rays zu marchen
- Invalidierung bei Lichtrichtungs- oder TF-Änderung
```

**Liefert:** Dramatisch bessere Tiefenwahrnehmung. Schatten unter Knochen, zwischen Organen, in Gefäßverzweigungen. Der Schlüssel-Unterschied zwischen "flachem" und "räumlichem" Volume Rendering.

---

### Phase 4: Local Ambient Occlusion

**Ziel:** Zusätzliche Tiefenhinweise durch umgebungsbasierte Verdeckung.

**Schritt 4.1: Precomputed Neighborhood Occlusion**
```
- Pro Voxel: hemisphärische Sichtbarkeit in einem Radius R berechnen
- Approximation: N Strahlen (32–64) in zufälligen Richtungen, Opazität akkumulieren
- Ergebnis: Occlusion-Faktor O(x) ∈ [0,1] pro Voxel
- Einmalige Vorberechnung (kann 10–60 Sek. dauern), dann gecacht
```

**Schritt 4.2: Dynamic AO mit Histogrammen (Ropinski-Ansatz)**
```
- Statt roher Opazität: lokale Dichte-Histogramme vorberechnen
- Histogramme × Transfer Function → Occlusion (in Echtzeit bei TF-Änderung)
- Clustering der Histogramme (Vector Quantization) → Speicher-Effizienz
- Vorteil: TF-Änderung erfordert KEIN Neuberechnen der Basis-Daten
```

**Schritt 4.3: Integration**
```
- L_amb(x) = (1 - O(x)) × k_a statt konstantem Ambient
- Konfigurierbar: AO-Radius, Intensität, Ein/Aus
```

**Liefert:** Selbst ohne gerichtetes Licht vermitteln enge Spalten, Kavitäten und eingebettete Strukturen räumliche Tiefe.

---

## 5. Reihenfolge & Meilensteine

```
Phase 1 ──────────────────────────────────────────────
  1.1 VolumeRenderState               │  0.5 Tage
  1.2 VolumeGradientVolume             │  0.5 Tage
  1.3 VolumeRayCaster (Core)           │  2.0 Tage
  1.4 Parallelisierung + Adaptive      │  1.0 Tag
  1.5 UI Integration + Kamera          │  1.0 Tag
                                        ├──── Meilenstein: "DVR works"
Phase 2 ──────────────────────────────────────────────
  2.1 TransferFunction Model           │  0.5 Tage
  2.2 CT Presets                       │  0.5 Tage
  2.3 TransferFunctionEditor UI        │  2.0 Tage
                                        ├──── Meilenstein: "TF Editor"
Phase 3 ──────────────────────────────────────────────
  3.1 Single Shadow Ray                │  1.0 Tag
  3.2 Cone Soft Shadows                │  0.5 Tage
  3.3 Occlusion Cache                  │  1.0 Tag
                                        ├──── Meilenstein: "Shadows"
Phase 4 ──────────────────────────────────────────────
  4.1 Precomputed AO                   │  1.0 Tag
  4.2 Dynamic AO (Histogramme)         │  2.0 Tage
  4.3 Integration                      │  0.5 Tage
                                        ├──── Meilenstein: "Full Illumination"
                          Gesamt: ~14 Arbeitstage
```

---

## 6. Technische Risiken & Mitigierungen

| Risiko | Impact | Mitigation |
|---|---|---|
| **CPU zu langsam für interaktive Raten** | Hoch | Progressive Rendering: erst niedrige Auflösung (1/4), dann hochskalieren. Debounce bei Kamerabewegung. Caching bei statischer Ansicht. |
| **Speicher für Gradient-Volume** | Mittel | 512³ × 3 × 4 Byte = 1.5 GB. → Half-precision (float16) oder on-the-fly Berechnung mit Caching der letzten N Slices. |
| **Gradienten verrauscht bei MRT/US** | Mittel | Optionaler Gauss-Filter vor Gradientenberechnung (Paper warnt explizit: "gradient-based shading is not recommended for low SNR modalities"). |
| **Transfer Function UX Komplexität** | Mittel | Presets als Einstieg, Editor nur für Power-User. Histogramm-Overlay als Orientierung. |
| **Avalonia hat kein GPU-Compute** | Niedrig (in Phase 1-4) | CPU-first Design. GPU-Phase wäre separates Projekt mit Silk.NET oder ComputeSharp. |

---

## 7. Abgrenzung zu anderen DICOM-Viewern

Was dieser Plan **nicht** anstrebt:
- Wir bauen keinen generischen VTK/ParaView-Klon
- Wir brauchen kein Echtzeit-60fps — 2–5 fps bei Interaktion, volle Qualität bei Stillstand reicht für Diagnostik
- Kein Multi-Volume Fusion (z.B. PET/CT Overlay) in Phase 1-4
- Kein Cinematic Rendering (Monte Carlo Path Tracing) — das wäre ein GPU-Projekt

Was wir **erreichen**:
- Qualitätslevel vergleichbar mit Siemens syngo.via / GE AW Server für Standard-CT/CTA
- Transfer Functions auf dem Niveau kommerzieller PACS-Workstations
- Schatten und AO als Differenzierungsmerkmal gegenüber Open-Source-Viewern (die meist nur lokales Phong haben)
