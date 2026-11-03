# Inspect pixels for the known 100 x 80 mm integration drawing.
param([Parameter(Mandatory = $true)] [string] $Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap($Path)
try {
    $margin = $bitmap.GetPixel(5, $bitmap.Height - 20)
    $rectangle = $bitmap.GetPixel(120, 330)
    if ($margin.R -lt 245 -or $margin.G -lt 245 -or $margin.B -lt 245) { throw 'Preview lost the white page margin; it is cropped to content.' }
    if ($rectangle.B -le 100 -or $rectangle.B -le $rectangle.R) { throw 'Preview does not contain the expected blue rectangle at page coordinates.' }
}
finally { $bitmap.Dispose() }
