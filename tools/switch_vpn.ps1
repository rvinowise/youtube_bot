# Disconnect every AmneziaWG tunnel and install exactly one, in a single
# detached process. Meant to be started with Start-Process so it survives the
# loss of the remote session (which rides on the very tunnel being replaced).
param(
    [Parameter(Mandatory = $true)][string]$Conf
)

$exe = 'C:\Program Files\AmneziaWG\AmneziaWG.exe'
$awg = 'C:\Program Files\AmneziaWG\awg.exe'
$log = 'C:\prj\browsing\work\switch-vpn.log'

function Log([string]$m) { "$(Get-Date -Format o) $m" | Add-Content -Path $log }

function Up { (& $awg show interfaces 2>$null) -split '\s+' | Where-Object { $_ } }

$target = [IO.Path]::GetFileNameWithoutExtension($Conf)
Log "=== switch to $target (was: $((Up) -join ','))"

foreach ($n in (Up)) {
    Log "uninstall $n"
    & $exe "/uninstalltunnelservice" $n | Out-Null
}

# wait for the routes to disappear, do not install on top of them
for ($i = 0; $i -lt 40; $i++) {
    if (-not (Up)) { break }
    Start-Sleep -Milliseconds 250
}

if (Up) {
    Log "still up after uninstall: $((Up) -join ','); forcing again"
    foreach ($n in (Up)) { & $exe "/uninstalltunnelservice" $n | Out-Null; Start-Sleep -Milliseconds 500 }
}

Log "install $Conf"
& $exe "/installtunnelservice" $Conf | Out-Null

for ($i = 0; $i -lt 80; $i++) {
    if ((Up) -contains $target) {
        Log "OK up: $((Up) -join ',')"
        exit 0
    }
    Start-Sleep -Milliseconds 250
}

Log "FAILED, up: $((Up) -join ',')"
exit 1
