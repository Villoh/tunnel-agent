# Build & Publish Guide

## Development

Normal day-to-day run:

```pwsh
dotnet run
```

Build without running (catches compile errors):

```pwsh
dotnet build
```

---

## Publishing Options

### Option 1 — Framework-dependent, single file (recommended for releases)

Requires **.NET 10 runtime** installed on the user's machine.  
Produces **one `.exe`** (~106MB) — all managed DLLs and native libs bundled inside.

```pwsh
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

| | |
|---|---|
| Output | `bin/Release/net10.0-windows/win-x64/publish/TunnelAgent.exe` |
| Size | ~106 MB |
| Files | 1 (+ `.pdb` debug symbols, can be dropped) |
| Requires | .NET 10 Runtime on target machine |

---

### Option 2 — Self-contained, single file (no runtime required)

Bundles the entire .NET 10 runtime inside the exe. No prerequisites for the user.  
Uses trimming to remove unused framework code — watch for runtime crashes if reflection-heavy code paths are hit.

```pwsh
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=true `
  -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

| | |
|---|---|
| Output | `bin/Release/net10.0-windows/win-x64/publish/TunnelAgent.exe` |
| Size | ~60-70 MB (trimmed) |
| Files | 1 (+ `.pdb`) |
| Requires | Nothing |

> ⚠️ Trimming may break Avalonia reflection bindings. Always test the trimmed build before shipping.

---

### Option 3 — Self-contained, multiple files (safest)

No trimming, no single file. All DLLs explicit. Largest but most reliable.

```pwsh
dotnet publish -c Release -r win-x64 --self-contained true
```

| | |
|---|---|
| Output | `bin/Release/net10.0-windows/win-x64/publish/` folder |
| Size | ~135 MB total |
| Files | ~30 files |
| Requires | Nothing |

---

## Why are there always 3 native DLLs?

Without `IncludeNativeLibrariesForSelfExtract=true`, these always appear alongside the exe:

| File | Purpose | Size |
|---|---|---|
| `libSkiaSharp.dll` | 2D graphics (Avalonia renderer) | ~9 MB |
| `av_libglesv2.dll` | OpenGL ES (GPU acceleration) | ~4 MB |
| `libHarfBuzzSharp.dll` | Text shaping | ~1.5 MB |

These are **unmanaged C++ libraries** — they cannot be packed into a managed `.exe` by `PublishSingleFile` unless you use `IncludeNativeLibrariesForSelfExtract=true`, which bundles them and extracts them to a temp folder at startup.

---

## Why is the exe so large?

The main contributors:

| Component | Size |
|---|---|
| .NET 10 runtime (self-contained only) | ~80 MB |
| `libSkiaSharp.dll` | ~9 MB |
| `IconPacks.Avalonia.SimpleIcons.dll` | ~5 MB |
| `IconPacks.Avalonia.Lucide.dll` | ~4.5 MB |
| `av_libglesv2.dll` | ~4 MB |
| App code (`TunnelAgent.dll`) | ~3 MB |

The icon packs embed **all** icons even if only a few are used. If size becomes critical, switching to SVG files or a subset icon pack would help.

---

## Runtime identifiers

| RID | Target |
|---|---|
| `win-x64` | Windows 64-bit (most common) |
| `win-arm64` | Windows on ARM (Surface Pro X, Snapdragon) |
| `osx-arm64` | macOS Apple Silicon (M1/M2/M3/M4) |
| `osx-x64` | macOS Intel |
| `linux-x64` | Linux 64-bit |

---

## Dropping debug symbols

The `.pdb` file is only needed for crash stack traces. Safe to exclude from distribution:

```pwsh
dotnet publish ... -p:DebugType=None -p:DebugSymbols=false
```

---

## Current release command

```pwsh
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

Produces a single `TunnelAgent.exe` (~106MB, no debug symbols) requiring .NET 10 on the target machine.
