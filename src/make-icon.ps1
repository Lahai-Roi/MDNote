$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('MDNoteIconBounds' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System.Drawing;
public static class MDNoteIconBounds {
    public static Rectangle Find(Bitmap image) {
        int left=image.Width,top=image.Height,right=-1,bottom=-1;
        for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)
            if(image.GetPixel(x,y).A>12){left=System.Math.Min(left,x);top=System.Math.Min(top,y);right=System.Math.Max(right,x);bottom=System.Math.Max(bottom,y);}
        return right<left?new Rectangle(0,0,image.Width,image.Height):Rectangle.FromLTRB(left,top,right+1,bottom+1);
    }
}
'@
}
$assetFolder = $PSScriptRoot
$inputImage = [System.Drawing.Image]::FromFile((Join-Path $assetFolder 'icon.png'))
$bounds = [MDNoteIconBounds]::Find($inputImage)
$frames = [System.Collections.Generic.List[byte[]]]::new()
$dimensions = @(16,24,32,48,64,128,256)
try {
    foreach ($size in $dimensions) {
        $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $inset = 0
        $available = $size - 2 * $inset
        $scale = $available / [Math]::Max($bounds.Width,$bounds.Height)
        $scaledWidth = $bounds.Width * $scale
        $scaledHeight = $bounds.Height * $scale
        $destination = [System.Drawing.RectangleF]::new(($size-$scaledWidth)/2,($size-$scaledHeight)/2,$scaledWidth,$scaledHeight)
        $graphics.DrawImage($inputImage,$destination,$bounds,[System.Drawing.GraphicsUnit]::Pixel)
        $memory = [System.IO.MemoryStream]::new()
        $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($memory.ToArray())
        $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
    $output = [System.IO.File]::Create((Join-Path $assetFolder 'MDNote.ico'))
    $writer = [System.IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$dimensions.Length)
        $offset = 6 + 16 * $dimensions.Length
        for ($i = 0; $i -lt $dimensions.Length; $i++) {
            $dimension = if ($dimensions[$i] -eq 256) {0} else {$dimensions[$i]}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
            $writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) {$writer.Write($frame)}
    } finally {$writer.Dispose();$output.Dispose()}
} finally {$inputImage.Dispose()}
