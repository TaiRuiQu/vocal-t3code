$ErrorActionPreference = 'Stop'

$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$destination = Join-Path $repository 'release\T3-Voice'
$project = Join-Path $PSScriptRoot 'T3.Voice\T3.Voice.csproj'

foreach ($name in @('T3.Voice.exe', 'T3.Voice.dll', 'T3.Voice.Core.dll')) {
    $existing = Join-Path $destination $name
    if (!(Test-Path -LiteralPath $existing)) { continue }
    try {
        $handle = [System.IO.File]::Open($existing, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $handle.Dispose()
    }
    catch {
        throw 'T3 Voice is running. Exit it from the tray, then run Publish.ps1 again.'
    }
}

& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $destination --source https://api.nuget.org/v3/index.json --nologo
if ($LASTEXITCODE -ne 0) { throw "T3 Voice publish failed ($LASTEXITCODE)." }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $destination 'README.md') -Force
Write-Output "T3 Voice updated: $(Join-Path $destination 'T3.Voice.exe')"
