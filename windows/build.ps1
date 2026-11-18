# Builds dist\tether-setup.exe: service and helper published self-contained, signed with a local
# certificate, then embedded in the setup exe. Run from the windows\ folder.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# The certificate is created once and reused. Import dist\tether-signing.cer on the child's laptop
# (as an administrator) so the signature is trusted:
#   Import-Certificate -FilePath tether-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
#   Import-Certificate -FilePath tether-signing.cer -CertStoreLocation Cert:\LocalMachine\Root
param(
    [string]$Configuration = 'Release',
    [switch]$NoSign
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$dist = Join-Path $PSScriptRoot 'dist'
$payload = Join-Path $dist 'payload'
Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $payload | Out-Null

function Publish($project, $out, $extra = @()) {
    dotnet publish "src\$project" -c $Configuration -r win-x64 --self-contained true -o $out `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none @extra
    if ($LASTEXITCODE -ne 0) { throw "publish $project failed" }
}

$cert = $null
if (-not $NoSign) {
    $subject = 'CN=Tether Local Signing'
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
    }
    Export-Certificate -Cert $cert -FilePath (Join-Path $dist 'tether-signing.cer') | Out-Null
}

function Sign($file) {
    if ($cert) {
        $r = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256
        if ($r.Status -notin 'Valid', 'UnknownError') { throw "signing $file failed: $($r.StatusMessage)" }
    }
}

Publish 'Tether.Service' (Join-Path $dist 'svc')
Publish 'Tether.Helper' (Join-Path $dist 'helper')
Copy-Item (Join-Path $dist 'svc\Tether.Service.exe') $payload
Copy-Item (Join-Path $dist 'helper\Tether.Helper.exe') $payload
Sign (Join-Path $payload 'Tether.Service.exe')
Sign (Join-Path $payload 'Tether.Helper.exe')

Publish 'Tether.Setup' (Join-Path $dist 'setup') @("-p:PayloadDir=$payload")
Copy-Item (Join-Path $dist 'setup\Tether.Setup.exe') (Join-Path $dist 'tether-setup.exe')
Sign (Join-Path $dist 'tether-setup.exe')

Remove-Item -Recurse -Force (Join-Path $dist 'svc'), (Join-Path $dist 'helper'), (Join-Path $dist 'setup'), $payload
Get-ChildItem $dist | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
