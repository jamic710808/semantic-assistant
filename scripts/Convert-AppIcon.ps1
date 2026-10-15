param(
    [string]$PngPath,
    [string]$IcoPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $PngPath) { $PngPath = Join-Path $taskRoot 'src\SemanticAssistant.Desktop\Assets\SemanticAssistant.png' }
if (-not $IcoPath) { $IcoPath = Join-Path $taskRoot 'src\SemanticAssistant.Desktop\Assets\SemanticAssistant.ico' }
$taskSource = [Drawing.Bitmap]::new([IO.Path]::GetFullPath($PngPath))
$taskSizes = @(16,20,24,32,40,48,64,96,128,256)
$taskFrames = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($taskSize in $taskSizes) {
        $taskBitmap = [Drawing.Bitmap]::new($taskSize,$taskSize,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
        $taskStream = [IO.MemoryStream]::new()
        try {
            $taskGraphics.Clear([Drawing.Color]::Transparent)
            $taskGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $taskGraphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskGraphics.DrawImage($taskSource,[Drawing.Rectangle]::new(0,0,$taskSize,$taskSize),0,0,$taskSource.Width,$taskSource.Height,[Drawing.GraphicsUnit]::Pixel)
            $taskBitmap.Save($taskStream,[Drawing.Imaging.ImageFormat]::Png)
            $taskFrames.Add($taskStream.ToArray())
        } finally { $taskStream.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose() }
    }
} finally { $taskSource.Dispose() }
$taskOutput = [IO.File]::Create([IO.Path]::GetFullPath($IcoPath))
$taskWriter = [IO.BinaryWriter]::new($taskOutput)
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskFrames.Count)
    $taskOffset = 6 + 16 * $taskFrames.Count
    for ($taskIndex=0; $taskIndex -lt $taskFrames.Count; $taskIndex++) {
        $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
        $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length); $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskFrames[$taskIndex].Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write($taskFrame) }
} finally { $taskWriter.Dispose() }
Write-Output ('ICO frames: ' + ($taskSizes -join ', ') + '; output: ' + $IcoPath)
