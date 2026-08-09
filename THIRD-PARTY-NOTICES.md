# Third-party notices

This program bundles and depends on work by other people. Everything below was read from the
artefacts themselves — the version resources compiled into each DLL, and the licence metadata in
each NuGet package — rather than from memory, and each entry says which.

---

## Native libraries redistributed in this repository

These five files sit in `src/AccessiblePdfEditor/` and are copied next to the executable at build
time. They are what let the program speak through a screen reader and play its earcons; without them
it is silent.

### Tolk — screen reader abstraction

| | |
|---|---|
| Files | `Tolk.dll`, `TolkDotNet.dll` |
| Version | 1.0.0.0 |
| Copyright | © 2014–2019, Davy Kager *(from the DLL version resource)* |
| Licence | **LGPL-3.0** *(reported by the GitHub API for `dkager/tolk`)* |
| Source | https://github.com/dkager/tolk |

Tolk is used unmodified, as a dynamically linked library. The LGPL requires that recipients be able
to replace it: to do so, build Tolk from the source above and overwrite `Tolk.dll` and
`TolkDotNet.dll` in the output folder. Nothing in this program depends on the version shipped here
beyond its published API.

### NVDA Controller Client

| | |
|---|---|
| File | `nvdaControllerClient64.dll` |
| Licence | **LGPL-2.1** *(from `LICENSE-NVDA.txt` in the Tolk distribution)* |
| Source | https://github.com/nvaccess/nvda — redistributed as part of Tolk |

The DLL carries no version resource of its own. It is obtained through the Tolk distribution, which
ships its licence separately as `LICENSE-NVDA.txt`.

### Dolphin ScreenReader API

| | |
|---|---|
| File | `SAAPI64.dll` |
| Origin | Dolphin Computer Access, redistributed as part of the Tolk distribution |

This DLL carries no version resource and no licence statement of its own. It reaches this repository
by way of Tolk, which bundles it so that Dolphin's SuperNova and ScreenReader can be driven through
the same interface as NVDA and JAWS. **Its terms have not been independently verified here.** Anyone
redistributing this repository commercially should confirm them with Dolphin.

### OpenAL Soft

| | |
|---|---|
| File | `OpenAL32.dll` |
| Version | 1.25.1 |
| Licence | **GNU LGPL v2, June 1991** *(from the DLL version resource)* |
| Source | https://github.com/kcat/openal-soft |

Used unmodified, dynamically linked, and replaceable the same way as Tolk.

---

## NuGet packages

Restored at build time and not redistributed in this repository. Licence identifiers are taken from
each package's `.nuspec`.

| Package | Licence | Project |
|---|---|---|
| PdfPig | **Apache-2.0** | https://github.com/UglyToad/PdfPig |
| PdfPig.Rendering.Skia | **Apache-2.0** | https://github.com/BobLd/PdfPig.Rendering.Skia |
| PDFsharp | **MIT** | https://docs.pdfsharp.net/ |
| OpenTK | **MIT** | https://github.com/opentk/opentk |
| Microsoft.Extensions.DependencyInjection | **MIT** | https://dot.net/ |
| Microsoft.Web.WebView2 | Microsoft software licence terms (`LICENSE.txt` in the package) | https://aka.ms/webview |

The **Microsoft Edge WebView2 Runtime** itself is not distributed here. It ships with Windows 11 and
with Microsoft Edge, and the program falls back to its text view when it is absent.

---

## Research sources

The browse-view design rests on reading NVDA's own source to find out what it actually does, rather
than on documentation or assumption. The files consulted, all from https://github.com/nvaccess/nvda:

- `source/NVDAObjects/IAccessible/ia2Web.py` — browse mode is created for any read-only document,
  with no check on the host application
- `source/virtualBuffers/__init__.py` — a browse-mode buffer is what implements table navigation
- `source/cursorManager.py` — NVDA binds Page Up and Page Down to its own cursor
- `source/browseMode.py` — which gestures browse mode claims, and which pass through

NVDA is copyright NV Access Limited and contributors, and is licensed GPL-2.0. No NVDA code is
copied into this repository; it was read to learn what interfaces to expose.
