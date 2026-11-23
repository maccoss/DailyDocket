# Builds DailyDocket's icon and logos from the mark (mark.svg), with VectorCraft's command-line tool:
#   icon.svg            the mark on a dark rounded square (the app icon)
#   logo-light.svg      mark, name and line for a light page (README on GitHub's light theme)
#   logo-dark.svg       the same for a dark page
#   icon-256.png, logo-*.png and src/DailyDocket.App/Assets/app.ico
# The logos' text is turned into outlines, so they look the same without the font installed.
# Run from anywhere: pwsh assets/brand/build.ps1

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$cli = 'C:\Program Files\ArtCraft\VectorCraft\vectorcraft-cli.exe'
if (-not (Test-Path $cli)) { throw "VectorCraft is not installed at $cli." }

$mark = Get-Content (Join-Path $here 'mark.svg') -Raw
$defs = [regex]::Match($mark, '(?s)<defs>.*?</defs>').Value
$art = [regex]::Match($mark, '(?s)(<g transform="translate\(-8,-12\)">.*</g>)\s*</svg>').Groups[1].Value

function Write-Svg([string]$name, [string]$body, [int]$width, [int]$height) {
    $svg = "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 $width $height"" width=""$width"" height=""$height"">`n$body`n</svg>`n"
    Set-Content -Path (Join-Path $here $name) -Value $svg -NoNewline -Encoding utf8
}

# The icon: a 256 square, the mark at its middle.
$iconBody = @"
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#2A3757"/>
      <stop offset="1" stop-color="#121A2E"/>
    </linearGradient>
  </defs>
  $defs
  <rect x="4" y="4" width="248" height="248" rx="54" fill="url(#tile)" stroke="#3E4C70" stroke-width="2"/>
  <g transform="translate(28,30)">
  $art
  </g>
"@
Write-Svg 'icon.svg' $iconBody 256 256

# The logos: the mark, then "Daily Docket" and its line.
foreach ($theme in @(@{ Name = 'light'; Ink = '#111827'; Line = '#4B5563' }, @{ Name = 'dark'; Ink = '#FFFFFF'; Line = '#A3AEC2' })) {
    $logoBody = @"
  $defs
  <g transform="translate(0,10) scale(0.9)">
  $art
  </g>
  <text x="200" y="112" font-family="Segoe UI" font-weight="700" font-size="76" fill="$($theme.Ink)">Daily <tspan fill="#14B8B0">Docket</tspan></text>
  <text x="203" y="158" font-family="Segoe UI" font-size="28" fill="$($theme.Line)">Your personal dashboard</text>
"@
    $source = Join-Path $env:TEMP "dailydocket-logo-$($theme.Name).svg"
    Set-Content -Path $source -Value "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 680 200"" width=""680"" height=""200"">`n$logoBody`n</svg>`n" -NoNewline -Encoding utf8
    & $cli convert $source (Join-Path $here "logo-$($theme.Name).svg") --outline-text | Out-Null
    & $cli convert $source (Join-Path $here "logo-$($theme.Name).png") --ppi 144 | Out-Null
    Remove-Item $source
}

# PNGs of the icon at each size the .ico holds, then the .ico (PNG entries, Vista and later).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $out = Join-Path $env:TEMP "dailydocket-icon-$size.png"
    & $cli convert (Join-Path $here 'icon.svg') $out --ppi (72 * $size / 256) | Out-Null
    [pscustomobject]@{ Size = $size; Bytes = [IO.File]::ReadAllBytes($out); Path = $out }
}
Copy-Item ($pngs | Where-Object Size -EQ 256).Path (Join-Path $here 'icon-256.png') -Force

$ico = Join-Path $here '..\..\src\DailyDocket.App\Assets\app.ico'
$stream = [IO.File]::Create($ico)
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$p.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $writer.Write($p.Bytes); Remove-Item $p.Path }
$writer.Dispose()
Write-Host "Wrote icon.svg, logo-light.svg, logo-dark.svg, their PNGs, icon-256.png and $((Resolve-Path $ico).Path)"
