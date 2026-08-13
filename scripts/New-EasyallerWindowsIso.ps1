param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DestinationIso,

    [string]$VolumeLabel = 'EASYALLER_WIN11_25H2',

    [switch]$DataOnly
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$destination = [IO.Path]::GetFullPath($DestinationIso)
if (Test-Path -LiteralPath $destination) {
    throw "Destination ISO already exists: $destination"
}

$biosBoot = Join-Path $source 'boot\etfsboot.com'
$uefiBoot = Join-Path $source 'efi\microsoft\boot\efisys.bin'
if (-not $DataOnly -and
    (-not (Test-Path -LiteralPath $biosBoot -PathType Leaf) -or
     -not (Test-Path -LiteralPath $uefiBoot -PathType Leaf))) {
    throw 'Windows BIOS or UEFI boot image is missing.'
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class EasyallerIsoStreamWriter
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateStreamOnFileEx(
        string fileName,
        uint mode,
        uint attributes,
        bool create,
        IStream template,
        out IStream stream);

    public static void Write(string path, object imageStream, int blockSize, int totalBlocks)
    {
        IStream source = (IStream)imageStream;
        IStream destination;
        int result = SHCreateStreamOnFileEx(path, 0x1001, 0x80, true, null, out destination);
        if (result != 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        try
        {
            const int blocksPerChunk = 1024;
            int chunks = totalBlocks / blocksPerChunk;
            int remainder = totalBlocks % blocksPerChunk;
            if (remainder > 0)
            {
                source.CopyTo(destination, (long)remainder * blockSize, IntPtr.Zero, IntPtr.Zero);
            }

            for (int index = 0; index < chunks; index++)
            {
                source.CopyTo(destination, (long)blocksPerChunk * blockSize, IntPtr.Zero, IntPtr.Zero);
            }

            destination.Commit(0);
        }
        finally
        {
            Marshal.ReleaseComObject(destination);
        }
    }
}
'@

$streams = @()
$bootOptions = @()
$fileSystemImage = $null
$resultImage = $null
try {
    if (-not $DataOnly) {
        foreach ($configuration in @(
            @($biosBoot, 0),
            @($uefiBoot, 0xEF)
        )) {
            $stream = New-Object -ComObject ADODB.Stream
            $stream.Type = 1
            $stream.Open()
            $stream.LoadFromFile($configuration[0])
            $streams += $stream

            $option = New-Object -ComObject IMAPI2FS.BootOptions
            $option.AssignBootImage($stream.psobject.BaseObject)
            $option.PlatformId = $configuration[1]
            $option.Emulation = 0
            $option.Manufacturer = 'Microsoft'
            $bootOptions += $option.psobject.BaseObject
        }
    }

    $fileSystemImage = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $fileSystemImage.FileSystemsToCreate = 4
    $fileSystemImage.UDFRevision = 0x102
    $fileSystemImage.FreeMediaBlocks = 0
    $fileSystemImage.VolumeName = $VolumeLabel
    if (-not $DataOnly) {
        $fileSystemImage.BootImageOptionsArray = $bootOptions
    }
    $fileSystemImage.Root.AddTree($source, $false)

    $resultImage = $fileSystemImage.CreateResultImage()
    [EasyallerIsoStreamWriter]::Write(
        $destination,
        $resultImage.ImageStream,
        $resultImage.BlockSize,
        $resultImage.TotalBlocks)

    Get-Item -LiteralPath $destination
}
finally {
    foreach ($stream in $streams) {
        try { $stream.Close() } catch { }
    }

    if ($resultImage) {
        [Runtime.InteropServices.Marshal]::ReleaseComObject($resultImage) | Out-Null
    }
    if ($fileSystemImage) {
        [Runtime.InteropServices.Marshal]::ReleaseComObject($fileSystemImage) | Out-Null
    }
}
