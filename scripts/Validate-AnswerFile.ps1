[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$InstallationMedia,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$WindowsImage,

    [Parameter(Mandatory)]
    [ValidateRange(1, 4294967295)]
    [uint32]$ImageIndex,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$AnswerFile,

    [ValidateSet('NotRun', 'Passed', 'Failed')]
    [string]$WindowsSimResult = 'NotRun',

    [string]$WindowsSimVersion,

    [string]$WindowsSimMessage,

    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'Easyaller\Validation')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedPath([string]$Path) {
    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Get-DismImageMetadata([string]$ImagePath, [uint32]$Index) {
    $arguments = @('/English', '/Get-ImageInfo', "/ImageFile:$ImagePath", "/Index:$Index")
    $output = & dism.exe @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "DISM could not inspect image index $Index. $($output -join [Environment]::NewLine)"
    }

    $metadata = [ordered]@{}
    foreach ($line in $output) {
        if ($line -match '^\s*(?<key>[^:]+?)\s*:\s*(?<value>.*)\s*$') {
            $metadata[$Matches.key.Trim()] = $Matches.value.Trim()
        }
    }

    return [ordered]@{
        RawOutput = @($output)
        Fields = $metadata
    }
}

function Test-AnswerFile([string]$Path) {
    [xml]$answerFileXml = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ($answerFileXml.DocumentElement.LocalName -ne 'unattend' -or
        $answerFileXml.DocumentElement.NamespaceURI -ne 'urn:schemas-microsoft-com:unattend') {
        throw 'Answer file root must be unattend in the Windows unattended-setup namespace.'
    }

    $forbiddenElements = @(
        'DiskConfiguration',
        'ProductKey',
        'DomainAccounts',
        'JoinDomain',
        'AutoLogon',
        'FirstLogonCommands',
        'RunSynchronous'
    )
    $found = @(
        foreach ($element in $forbiddenElements) {
            if ($answerFileXml.SelectNodes("//*[local-name()='$element']").Count -gt 0) {
                $element
            }
        }
    )
    if ($found.Count -gt 0) {
        throw "Answer file contains prohibited element(s): $($found -join ', ')."
    }
}

$normalizedMedia = Get-NormalizedPath $InstallationMedia
$normalizedImage = Get-NormalizedPath $WindowsImage
$normalizedAnswerFile = Get-NormalizedPath $AnswerFile
$imageExtension = [System.IO.Path]::GetExtension($normalizedImage)
if ($imageExtension -notin '.wim', '.esd') {
    throw 'WindowsImage must be an install.wim or install.esd file.'
}

if ([System.IO.Path]::GetExtension($normalizedMedia) -ne '.iso') {
    throw 'InstallationMedia must be the supplied official Windows ISO file.'
}

if ($WindowsSimResult -ne 'NotRun' -and [string]::IsNullOrWhiteSpace($WindowsSimVersion)) {
    throw 'WindowsSimVersion is required when WindowsSimResult is Passed or Failed.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$normalizedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($normalizedOutputDirectory.StartsWith($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must stay outside the Easyaller repository.'
}

Test-AnswerFile $normalizedAnswerFile
$imageMetadata = Get-DismImageMetadata $normalizedImage $ImageIndex

New-Item -ItemType Directory -Path $normalizedOutputDirectory -Force | Out-Null
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssZ', [System.Globalization.CultureInfo]::InvariantCulture)
$evidencePath = Join-Path $normalizedOutputDirectory "answer-file-validation-$timestamp.json"
$evidence = [ordered]@{
    SchemaVersion = 1
    CreatedUtc = [DateTime]::UtcNow.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
    Automation = [ordered]@{
        XmlPreflight = 'Passed'
        DismImageInspection = 'Passed'
        IsDestructive = $false
    }
    WindowsSim = [ordered]@{
        Result = $WindowsSimResult
        Version = $WindowsSimVersion
        Message = $WindowsSimMessage
        RequiresManualConfirmation = $WindowsSimResult -eq 'NotRun'
    }
    InstallationMedia = [ordered]@{
        FileName = [System.IO.Path]::GetFileName($normalizedMedia)
        Sha256 = (Get-FileHash -LiteralPath $normalizedMedia -Algorithm SHA256).Hash
    }
    WindowsImage = [ordered]@{
        FileName = [System.IO.Path]::GetFileName($normalizedImage)
        Sha256 = (Get-FileHash -LiteralPath $normalizedImage -Algorithm SHA256).Hash
        ImageIndex = $ImageIndex
        Metadata = $imageMetadata
    }
    AnswerFile = [ordered]@{
        FileName = [System.IO.Path]::GetFileName($normalizedAnswerFile)
        Sha256 = (Get-FileHash -LiteralPath $normalizedAnswerFile -Algorithm SHA256).Hash
    }
}

$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
Write-Host "Evidence written to $evidencePath"
if ($WindowsSimResult -eq 'NotRun') {
    Write-Warning 'Windows SIM validation is still required. Do not claim this image is schema validated until the result is recorded as Passed.'
}
