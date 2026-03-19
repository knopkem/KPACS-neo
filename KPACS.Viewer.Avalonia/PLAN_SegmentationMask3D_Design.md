# SegmentationMask3D Technical Design

## Purpose

This document defines the technical design for Issue 1:

> Add `SegmentationMask3D` domain model and persistence hooks.

It is intentionally scoped to the first production-safe step:

- introduce a true 3D mask representation
- preserve spatial correctness
- integrate with current `VolumeRoi` workflows without breaking them
- provide a clean path toward centerline, CPR, and EVAR planning

Constraint:

- stay on .NET 10
- stay on OpenCL 2.1 for compute acceleration
- do not introduce external binary segmentation formats in this step

## Why this is needed

Today, the viewer already stores volumetric ROI intent through `VolumeRoiContour[]` on `StudyMeasurement` in `Models/MeasurementModels.cs`.

That is useful for:

- preview
- contour projection
- rough volume estimation
- local contour-based editing

But it is not enough for vascular planning because the downstream modules need a voxel-accurate, reusable, and editable 3D object.

Those modules include:

- centerline extraction
- cross-sectional lumen analysis
- landing-zone metrics
- future thrombus / calcification / branch-aware workflows
- persistent planning sessions

## Design goals

1. **Spatial correctness first**
   - mask must stay aligned to the source volume with deterministic transforms

2. **Minimal first version**
   - binary mask only
   - no multi-label segmentation yet

3. **Non-breaking introduction**
   - existing `VolumeRoiContour[]` workflows continue to work during migration

4. **Fast enough for interactive planning**
   - memory-efficient representation
   - predictable indexing
   - easy future GPU upload path

5. **Safe persistence**
   - robust JSON metadata
   - separate encoded payload option for large masks
   - graceful failure on partial or missing state

## Proposed model set

### 1. Core mask object

```csharp
public sealed record SegmentationMask3D(
    Guid Id,
    string Name,
    string SourceSeriesInstanceUid,
    string SourceFrameOfReferenceUid,
    string SourceStudyInstanceUid,
    VolumeGridGeometry Geometry,
    SegmentationMaskStorage Storage,
    SegmentationMaskMetadata Metadata);
```

### 2. Volume geometry

```csharp
public sealed record VolumeGridGeometry(
    int SizeX,
    int SizeY,
    int SizeZ,
    double SpacingX,
    double SpacingY,
    double SpacingZ,
    Vector3D Origin,
    Vector3D RowDirection,
    Vector3D ColumnDirection,
    Vector3D Normal,
    string FrameOfReferenceUid);
```

This must represent the exact voxel grid used by the loaded 3D volume.

### 3. Storage payload

```csharp
public sealed record SegmentationMaskStorage(
    SegmentationMaskStorageKind Kind,
    int ForegroundVoxelCount,
    string Encoding,
    byte[] Data);
```

```csharp
public enum SegmentationMaskStorageKind
{
    PackedBits,
}
```

### 4. Metadata

```csharp
public sealed record SegmentationMaskMetadata(
    SegmentationMaskSourceKind SourceKind,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    string? SourceMeasurementId,
    string? Notes,
    int Revision,
    SegmentationMaskStatistics? Statistics = null);
```

```csharp
public enum SegmentationMaskSourceKind
{
    AutoRoi,
    ManualEdit,
    Imported,
    Derived,
}
```

### 5. Statistics snapshot

```csharp
public sealed record SegmentationMaskStatistics(
    double VolumeCubicMillimeters,
    (int MinX, int MinY, int MinZ) BoundsMin,
    (int MaxX, int MaxY, int MaxZ) BoundsMax);
```

## Why a packed-bit mask for v1

A binary vascular mask does not need one byte per voxel in the first version.

Packed bits give a good baseline:

- compact in memory
- compact in JSON payload after Base64 serialization
- easy deterministic indexing
- easy conversion to byte-per-voxel or GPU buffers later

### Memory example

For a volume of $512 \times 512 \times 800$:

- voxels: $209{,}715{,}200$
- bit-packed payload: about $25$ MB
- byte mask payload: about $200$ MB

That difference matters immediately for planning sessions and undo/redo.

## Indexing strategy

Use a flat linear index with x-fastest layout:

$$
index = x + (y \cdot SizeX) + (z \cdot SizeX \cdot SizeY)
$$

Why:

- consistent with typical CPU cache-friendly slice traversal
- straightforward for GPU upload
- simple conversion to and from slice masks

## Access API design

Do not expose raw bit manipulation throughout the UI.

Create a focused helper type:

```csharp
internal sealed class SegmentationMaskBuffer
{
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }

    public bool Get(int x, int y, int z);
    public void Set(int x, int y, int z, bool value);
    public int CountForeground();
    public SegmentationMaskStorage ToStorage();
    public static SegmentationMaskBuffer FromStorage(VolumeGridGeometry geometry, SegmentationMaskStorage storage);
}
```

This gives:

- safe bounds checking in one place
- no repeated bit-twiddling logic in UI code
- clean place for future optimized block operations

## Relationship to current `StudyMeasurement`

Current `StudyMeasurement` already owns:

- `VolumeRoiContour[]? VolumeContours`

This should not be removed in Issue 1.

### Transitional approach

Keep `StudyMeasurement.VolumeContours` unchanged.

Add a new optional planning attachment concept instead of forcing mask data into the measurement record immediately.

### Option A - preferred

Introduce a dedicated planning state object later, but for Issue 1 attach mask references through metadata and a repository/service layer.

Example concept:

```csharp
public sealed record StudyPlanningState(
    List<SegmentationMask3D> Masks,
    List<Guid> SelectedMaskIds);
```

This keeps segmentation from overloading `StudyMeasurement`.

### Option B - acceptable stopgap

Add an optional `Guid? SegmentationMaskId` to `StudyMeasurement` for `MeasurementKind.VolumeRoi`.

Preferred approach is still to keep measurement geometry and planning masks loosely coupled.

## Conversion from current `VolumeRoiContour[]`

Issue 2 will implement the real conversion, but Issue 1 should already make the path obvious.

### Proposed conversion flow

1. Project each `VolumeRoiContour` into the corresponding volume slice
2. Rasterize closed contour(s) to a 2D slice mask
3. Write slice mask into `SegmentationMaskBuffer`
4. Optionally fill interpolated slices using existing contour interpolation helpers
5. Compute mask statistics and persist

### Important rule

The mask geometry must always be derived from the loaded 3D volume grid, not reconstructed from contour bounds alone.

That avoids coordinate drift between:

- source volume
- contour display
- future centerline and reslice planes

## Persistence design

## Storage shape

For the first implementation, use JSON-serializable records and Base64 for packed bytes.

```csharp
public sealed record StoredSegmentationMask3D(
    Guid Id,
    string Name,
    string SourceSeriesInstanceUid,
    string SourceFrameOfReferenceUid,
    string SourceStudyInstanceUid,
    VolumeGridGeometry Geometry,
    SegmentationMaskMetadata Metadata,
    string Encoding,
    string PayloadBase64,
    int ForegroundVoxelCount);
```

This is sufficient for:

- viewer settings style persistence
- study session persistence
- debugging and migration

## Persistence service

Add a narrow service abstraction:

```csharp
public interface ISegmentationMaskPersistenceService
{
    StoredSegmentationMask3D ToStored(SegmentationMask3D mask);
    SegmentationMask3D FromStored(StoredSegmentationMask3D stored);
}
```

Why:

- isolates encoding details
- simplifies migration later to file-backed payload storage
- keeps UI state classes smaller

## Failure handling

On load failure:

- log only non-PHI technical context
- skip invalid mask safely
- keep study opening functional
- surface a neutral UI warning such as: "A saved planning mask could not be restored."

Do not include patient-identifying data in log messages or exception text.

## Recommended repository layout

### New files

- `KPACS.Viewer.Avalonia/Models/SegmentationMask3D.cs`
- `KPACS.Viewer.Avalonia/Models/VolumeGridGeometry.cs`
- `KPACS.Viewer.Avalonia/Models/SegmentationMaskStorage.cs`
- `KPACS.Viewer.Avalonia/Models/SegmentationMaskMetadata.cs`
- `KPACS.Viewer.Avalonia/Models/SegmentationMaskStatistics.cs`
- `KPACS.Viewer.Avalonia/Services/SegmentationMaskPersistenceService.cs`
- `KPACS.Viewer.Avalonia/Services/SegmentationMaskBuffer.cs`

### Existing files likely to touch later

- `KPACS.Viewer.Avalonia/Models/MeasurementModels.cs`
- `KPACS.Viewer.Avalonia/Controls/DicomViewPanel.AutoOutline.cs`
- `KPACS.Viewer.Avalonia/Controls/DicomViewPanel.RoiBallCorrection.cs`
- `KPACS.Viewer.Avalonia/StudyViewerWindow.axaml.cs`
- future planning-state files

## Geometry invariants

These invariants should be enforced immediately:

1. `SizeX`, `SizeY`, `SizeZ` must all be `> 0`
2. `SpacingX`, `SpacingY`, `SpacingZ` must all be `> 0`
3. `RowDirection`, `ColumnDirection`, `Normal` must be normalized within tolerance
4. `FrameOfReferenceUid` of the mask must match the source volume
5. Payload bit count must equal `SizeX * SizeY * SizeZ`
6. `ForegroundVoxelCount` must never exceed total voxel count

If any invariant fails during load, reject the mask.

## API decisions

### Decision 1: record vs class

Use `record` for immutable persisted model types and `sealed class` for mutable working buffers.

Reason:

- persisted objects should be value-like
- edit-time buffers should avoid accidental copy-heavy behavior

### Decision 2: binary only in v1

Do not add labels, probabilities, or confidence maps yet.

Reason:

- EVAR MVP only needs foreground vessel mask
- multi-label design would slow down Issue 1 without immediate clinical value

### Decision 3: keep contours during migration

Do not replace `VolumeContours` immediately.

Reason:

- current preview/report features already depend on them
- conversion and backward compatibility are easier if both representations coexist temporarily

### Decision 4: bit-packed storage, not sparse voxels

Do not start with hash sets or sparse dictionaries.

Reason:

- vascular masks are often spatially compact, but contiguous volume traversal and GPU upload matter more
- sparse structures complicate cross-section sampling and morphology operations

## Performance considerations

### CPU

- editing helpers should operate on reusable buffers where possible
- avoid re-counting all voxels after tiny edits if a delta can be tracked later
- but for Issue 1, correctness beats incremental optimization

### GPU

Issue 1 does not need GPU-resident masks yet, but the chosen format should make upload simple.

Recommended future upload path:

- unpack packed bits into a reusable byte buffer when dispatching OpenCL kernels
- later consider keeping a dedicated device mask buffer for interactive workflows

## Undo/redo implications

Issue 1 does not need full undo/redo, but the model should not block it.

Recommended future undo strategy:

- immutable `SegmentationMask3D` snapshots at commit points
- optional region-delta actions later if memory becomes a problem

That means:

- persisted model immutable
- working buffer mutable
- promotion from buffer to immutable snapshot on commit

## Serialization options

Use one serializer option set consistently for mask persistence.

Recommended approach:

```csharp
private static readonly JsonSerializerOptions SerializerOptions = new()
{
    WriteIndented = true
};
```

If custom converters for `Vector3D` are already needed elsewhere, reuse one common implementation rather than special-casing mask files.

## Migration plan

### Step 1
- add model types and persistence service
- no user-visible behavior change yet

### Step 2
- convert auto-ROI result into `SegmentationMask3D`
- keep `VolumeContours` for preview and compatibility

### Step 3
- add mask edit commands
- save updated mask revisions

### Step 4
- let centerline and cross-section modules consume masks directly

### Step 5
- gradually demote contour representation from primary to compatibility/view model

## Test checklist for Issue 1

### Unit-level
- bit-packed round-trip preserves exact voxel states
- invalid geometry is rejected
- `ForegroundVoxelCount` matches actual set bits
- serialization/deserialization is stable

### Integration-level
- mask created from a loaded study reopens aligned to the same volume
- mismatched frame-of-reference mask is rejected cleanly
- corrupt payload fails without breaking viewer startup

### Manual checks
- save a study with one mask, reopen it, inspect same region visually
- verify bounds and reported volume remain stable across reopen

## Recommended first implementation slice

If the team wants the smallest credible implementation for the first PR:

1. add `VolumeGridGeometry`
2. add `SegmentationMaskStorage`
3. add `SegmentationMaskMetadata`
4. add `SegmentationMask3D`
5. add `SegmentationMaskBuffer`
6. add `SegmentationMaskPersistenceService`
7. add one focused unit/integration validation path if test harness exists

Keep Issue 1 strictly about the data model and persistence hook.

Do not pull in:

- centerline code
- mask editing UI
- DICOM SEG
- GPU kernels

## Recommendation

Use a **bit-packed immutable persisted mask + mutable working buffer** design.

That gives the best balance for the current codebase:

- small enough for session persistence
- strict enough for spatial correctness
- simple enough for a first implementation
- extensible enough for centerline, CPR, and EVAR planning

This should become the canonical 3D planning substrate, while `VolumeRoiContour[]` remains a transitional compatibility and preview format.
