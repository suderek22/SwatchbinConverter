# Durango BC7 Swatchbin Converter

This tool replaces the pixels in Durango Xbox One BC7 `.swatchbin` bundles with matching RGBA8 PNG images. It does not change texture dimensions, layout metadata, or bundle structure.

## Requirements

- Windows x64
- ForzaTech Studio installation containing `xg.dll`, `libs\BCnEncoder.dll`, and `libs\Microsoft.Toolkit.HighPerformance.dll`
- The .NET runtime matching ForzaTech Studio's runtime configuration
- Visual Studio's C# compiler (`csc.exe`)
- One stock Durango BC7 `.swatchbin` per replacement PNG

The supported inputs are single-mip, 2D Durango BC7 bundles with the 83-byte `burG`/`BCXT`/`HCXT` header used by FH2 HUD textures. The PNG must be non-interlaced RGBA8 and have the exact dimensions in its matching stock header. PNG files are paired by base filename: `Gauge.swatchbin` with `Gauge.png`.

The converter converts the PNG's straight alpha to premultiplied alpha, encodes BC7, determines whether the stock payload uses 1D-thin or 2D-thin tiling by requiring an exact detile/retile match, tiles the replacement with ForzaTech's `xg.dll`, then checks the generated pixels with a reverse round-trip. It aborts with an error if an input is invalid or its tile mode cannot be established.

## Usage

Prepare two directories containing corresponding files:

```text
stock\
  Gauge.swatchbin
png\
  Gauge.png
```

Run from PowerShell:

```powershell
.\build.ps1 `
  -ForzaTechDir "\ForzaTech" `
  -StockSwatchbinDir ".\stock" `
  -SourcePngDir ".\png" `
  -OutputDir ".\output"
```

The output folder contains game-format `.swatchbin` files with the original stock headers and metadata preserved. Validate replacements with ForzaTech and a safe game test before using them in a mod.
