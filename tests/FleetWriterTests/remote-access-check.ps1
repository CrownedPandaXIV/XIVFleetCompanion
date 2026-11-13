# Checks Setup/elevated.ps1's edits of PostgreSQL's files against the app's (setup/remote-access.js) for the same
# files (remote-access-fixture.json: each input with the app's answers). Prints one line per failure, then "failures: N".
param([string]$Cases, [string]$Script)
. $Script
$c = Get-Content -Raw $Cases | ConvertFrom-Json
$fail = 0
foreach ($x in $c.conf) {
    $a = Set-ListenAddresses $x[0]; if ($a -cne $x[1]) { $fail++; "listen_addresses: [$($x[0])] gave [$a]" }
    $p = Get-ConfPort $x[0]; if ($p -ne $x[2]) { $fail++; "port: [$($x[0])] gave $p" }
}
$ranges = @('100.64.0.0/10', '192.168.1.0/24')
foreach ($x in $c.hba) {
    $m = Get-PasswordMethod $x[0]; if ($m -cne $x[1]) { $fail++; "password method: gave $m" }
    $a = Add-AccessLines $x[0] $ranges; if ($a -cne $x[2]) { $fail++; "access lines: [$($x[0])] gave [$a]" }
    $r = Remove-AccessLines $a; if ($r -cne $x[3]) { $fail++; "removing them: gave [$r]" }
}
"failures: $fail"
