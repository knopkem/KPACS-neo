# K-PACS — C# Port

A modern C# port of key components from **K-PACS**, the free DICOM workstation I originally wrote about 20 years ago. K-PACS was one of the first freely available DICOM viewers and became widely used in radiology departments, veterinary clinics, and research labs around the world.

This project aims to bring the core DICOM infrastructure into the .NET ecosystem, built on top of [fo-dicom](https://github.com/fo-dicom/fo-dicom) and targeting **.NET 10**.

## Project Structure

```
KPACS.DCMClasses/           — Class library (.NET 10, fo-dicom 5.1.3)
├── DicomTypes.cs            — Enumerations (content types, VR types, bit depths, …)
├── DicomTagConstants.cs     — UID constants, private tag definitions, UID registry
├── DicomTagValue.cs         — Tag value wrapper (group, element, VR, value, name)
├── DicomBaseObject.cs       — Base class with notification support
├── DicomHeader.cs           — Core DICOM dataset wrapper (read/write/navigate tags)
├── DicomFunctions.cs        — Utility functions (date/time, UID generation, name parsing, …)
├── DicomImage.cs            — DICOM image file handling and pixel data access
├── DicomDirectory.cs        — DICOMDIR creation and reading
├── DicomPdf.cs              — Encapsulated PDF DICOM objects
├── DicomSecondaryCapture.cs — Secondary Capture creation from raw pixel data
├── DicomStructuredReport.cs — Structured Report (SR) creation and content management
├── DicomPresentationState.cs— Grayscale Softcopy Presentation State (GSPS)
├── DicomNetworkClient.cs    — DICOM networking (C-ECHO, C-FIND, C-MOVE, C-STORE, Worklist)
├── DicomNetworkThread.cs    — Multi-study async network operations with progress
└── Models/
    ├── StudyInfo.cs          — Study / Series / Image info classes
    ├── PrintConfig.cs        — DICOM print configuration
    └── WorklistItem.cs       — Modality worklist item
```

## Current Status

### ✅ Completed: DCMClasses Core Library

The entire **DCMClasses** package has been ported — this is the foundational DICOM data layer that everything else builds on. It covers:

- **DICOM parsing & serialization** — reading/writing DICOM files, tag manipulation, sequence navigation, character set support
- **DICOM networking** — full async C-ECHO, C-FIND (Patient/Study/Series/Image level), C-MOVE, C-STORE SCU, and Modality Worklist queries
- **Image objects** — pixel data access, frame extraction, transfer syntax handling, compression/decompression
- **Specialized SOP classes** — Secondary Capture, Encapsulated PDF, Structured Reports, Presentation State (GSPS with annotations, windowing, spatial transforms)
- **DICOMDIR** — creation and reading of media directory structures
- **Utility functions** — UID generation, date/time conversions, patient name parsing, age calculation

The library builds cleanly with zero errors and zero warnings.

### 🔲 Still To Do

The following major components from the original K-PACS have **not yet been ported**:

| Component | Description |
|---|---|
| **Study Browser** | Patient/study database, local storage management, query/retrieve UI |
| **DICOM Viewer** | Image display, windowing, zoom/pan, measurements, annotations, hanging protocols, lightbox, cine, magnifier |
| **Import Engine** | File import, DICOMDIR import, drag & drop, non-DICOM-to-DICOM conversion |
| **DICOM Server** | SCP services (C-STORE receiver, Query/Retrieve provider) |
| **Print** | DICOM Print SCU, print preview, film layout |
| **Report Writer** | Structured report authoring and display |
| **CD Burner** | DICOM media creation with auto-run viewer |
| **Settings & Configuration** | DICOM network settings, local storage, server management |
| **RIS Interface** | Worklist-driven workflow integration |
| **UI Framework** | Main application shell, toolbar, menus, observer/event system |

## Technology

| | |
|---|---|
| **Language** | C# 13 |
| **Runtime** | .NET 10 |
| **DICOM library** | [fo-dicom 5.1.3](https://github.com/fo-dicom/fo-dicom) |
| **Original** | Written in Delphi (Object Pascal), ~150k lines of application code |

## Building

```bash
dotnet build KPACS.DCMClasses/KPACS.DCMClasses.csproj
```

## Background

K-PACS was created around 2004 as a free DICOM workstation. It provided a full-featured PACS client including DICOM storage, query/retrieve, viewing, printing, CD burning, and modality worklist — capabilities that were typically only available in expensive commercial products at the time. It gained significant adoption worldwide, particularly in smaller imaging facilities and educational settings.

This port preserves the architectural concepts of the original while modernizing the codebase for the .NET platform.

## License

*License to be determined.*
