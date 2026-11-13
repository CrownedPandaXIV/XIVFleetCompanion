# XIV Fleet Companion: the setup steps that need administrator rights, run by the plugin through Windows' permission
# prompt (one prompt for all the steps), as the app runs them (its setup/elevated.js and setup/remote-access.js):
# installing PostgreSQL and Tailscale, "Let other PCs connect", and removing them again. The steps come in a small
# JSON file; how each went is written to the progress and result files the plugin reads. Written for Windows'
# own PowerShell (5.1). Started with -RequestFile; without it, it only defines its functions (for the tests).
param([string]$RequestFile)

$Marker = '# Added by XIV Fleet Companion (Let other PCs connect):'
$FirewallRule = 'XIV Fleet Companion PostgreSQL'
$TailscaleRange = '100.64.0.0/10'

function Get-Eol([string]$Text) { if ($Text.Contains("`r`n")) { return "`r`n" } return "`n" }
function Split-Lines([string]$Text) { return [regex]::Split($Text, "`r?`n") }

# postgresql.conf with listen_addresses = '*' (pg_hba.conf decides who gets in).
function Set-ListenAddresses([string]$Text) {
    $lines = [System.Collections.Generic.List[string]](Split-Lines $Text)
    $eol = Get-Eol $Text
    $wanted = "listen_addresses = '*'"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^\s*listen_addresses\s*=') {
            if ($lines[$i] -cmatch "^\s*listen_addresses\s*=\s*'\*'") { return $Text }
            $lines[$i] = "$wanted`t`t# was: $($lines[$i].Trim())"
            return [string]::Join($eol, $lines)
        }
    }
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^\s*#\s*listen_addresses\s*=') {
            $lines.Insert($i + 1, $wanted)
            return [string]::Join($eol, $lines)
        }
    }
    $sep = ''
    if (-not $Text.EndsWith("`n")) { $sep = $eol }
    return "$Text$sep$wanted$eol"
}

# The password check pg_hba.conf already uses for this PC's own connections (scram-sha-256 when there is none).
function Get-PasswordMethod([string]$Text) {
    foreach ($line in (Split-Lines $Text)) {
        $f = $line.Trim() -split '\s+'
        if ($f.Count -gt 4 -and $f[0] -ceq 'host' -and $f[3] -ceq '127.0.0.1/32' -and $f[1] -cne 'replication' -and $f[2] -cne 'replication') {
            if ($f[4] -ceq 'md5' -or $f[4] -ceq 'scram-sha-256') { return $f[4] }
        }
    }
    return 'scram-sha-256'
}

# pg_hba.conf with a line letting each address range in (with the password); ranges already in are left alone.
function Add-AccessLines([string]$Text, [string[]]$Ranges) {
    $eol = Get-Eol $Text
    $present = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($line in (Split-Lines $Text)) {
        $f = $line.Trim() -split '\s+'
        if ($f.Count -gt 3 -and $f[0] -ceq 'host' -and $f[1] -ceq 'all' -and $f[2] -ceq 'all') { $present.Add($f[3]) | Out-Null }
    }
    $missing = @($Ranges | Where-Object { -not $present.Contains($_) })
    if ($missing.Count -eq 0) { return $Text }
    $method = Get-PasswordMethod $Text
    $added = @($missing | ForEach-Object { 'host    all             all             ' + $_.PadRight(24) + $method })
    $sep = ''
    if (-not $Text.EndsWith("`n")) { $sep = $eol }
    return "$Text$sep$eol$Marker$eol$([string]::Join($eol, $added))$eol"
}

# pg_hba.conf without the lines "Let other PCs connect" added.
function Remove-AccessLines([string]$Text) {
    $eol = Get-Eol $Text
    $lines = Split-Lines $Text
    $out = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -ne $Marker) { $out.Add($lines[$i]); continue }
        if ($out.Count -gt 0 -and $out[$out.Count - 1].Trim() -eq '') { $out.RemoveAt($out.Count - 1) }
        while ($i + 1 -lt $lines.Count -and $lines[$i + 1].Trim() -cmatch '^host\s') { $i++ }
    }
    return [string]::Join($eol, $out)
}

# The port a postgresql.conf sets (5432 when it doesn't).
function Get-ConfPort([string]$Text) {
    $m = [regex]::Match($Text, '(?m)^\s*port\s*=\s*(\d+)')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return 5432
}

# PostgreSQL's Windows services from "Name|PathName" lines: service, data folder and install folder.
function ConvertFrom-ServiceLines([string[]]$Lines) {
    $found = @()
    foreach ($line in $Lines) {
        $parts = $line -split '\|', 2
        if ($parts.Count -lt 2 -or -not $parts[0] -or -not $parts[1]) { continue }
        $pathName = $parts[1]
        $d = [regex]::Match($pathName, '\s-D\s+(?:"([^"]+)"|(\S+))')
        if (-not $d.Success) { continue }
        $e = [regex]::Match($pathName, '^\s*(?:"([^"]+)"|(\S+))')
        $exe = ''
        if ($e.Success) { $exe = $e.Groups[1].Value; if (-not $exe) { $exe = $e.Groups[2].Value } }
        $install = $null
        if ($exe -match '\\bin\\pg_ctl\.exe$') { $install = Split-Path (Split-Path $exe -Parent) -Parent }
        $data = $d.Groups[1].Value; if (-not $data) { $data = $d.Groups[2].Value }
        $found += [pscustomobject]@{ Service = $parts[0].Trim(); DataDir = $data.Trim(); InstallDir = $install }
    }
    return $found
}

function Get-PostgresServices {
    $lines = @(Get-CimInstance Win32_Service -Filter "Name LIKE 'postgresql%'" | ForEach-Object { $_.Name + '|' + $_.PathName })
    return ConvertFrom-ServiceLines $lines
}

function Find-Instance($Services, [int]$Port) {
    foreach ($s in $Services) {
        $conf = Join-Path $s.DataDir 'postgresql.conf'
        if ((Test-Path -LiteralPath $conf) -and (Get-ConfPort ([IO.File]::ReadAllText($conf))) -eq $Port) { return $s }
    }
    return $null
}

function Get-Stamp { return (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') }

# Runs a program and waits: its exit code.
function Invoke-Waited([string]$File, [string[]]$Arguments) {
    $p = Start-Process -FilePath $File -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}

function Test-Installed([int]$Code) { return $Code -eq 0 -or $Code -eq 3010 }

function Step-Result($Kind, [bool]$Ok, [string]$Message, $Changed) {
    $r = [ordered]@{ kind = $Kind; ok = $Ok; message = $Message }
    if ($null -ne $Changed) { $r.changed = @($Changed) }
    return [pscustomobject]$r
}

function Edit-Conf($Instance, [string]$Name, [scriptblock]$Change, [string]$Label, $Changed) {
    $full = Join-Path $Instance.DataDir $Name
    if (-not (Test-Path -LiteralPath $full)) { return }
    $before = [IO.File]::ReadAllText($full)
    $after = & $Change $before
    if ($after -ceq $before) { return }
    Copy-Item -LiteralPath $full -Destination "$full.before-xiv-fleet-$(Get-Stamp)"
    [IO.File]::WriteAllText($full, $after)
    $Changed.Add($Label) | Out-Null
}

function Restart-Postgres($Instance) {
    Restart-Service -Name $Instance.Service -Force
}

function Open-Connections($Step) {
    $changed = New-Object System.Collections.ArrayList
    $port = [int]$Step.port
    $instance = Find-Instance (Get-PostgresServices) $port
    if (-not $instance) { return Step-Result 'openConnections' $false "Couldn't find the PostgreSQL on this PC that uses port $port. Is PostgreSQL installed here as a Windows service?" $changed }
    $ranges = [string[]]@($Step.ranges)
    Edit-Conf $instance 'postgresql.conf' { param($t) Set-ListenAddresses $t } 'PostgreSQL now listens for other PCs' $changed
    Edit-Conf $instance 'postgresql.auto.conf' { param($t) if ($t -match '(?m)^\s*listen_addresses\s*=') { Set-ListenAddresses $t } else { $t } } 'PostgreSQL now listens for other PCs (ALTER SYSTEM setting)' $changed
    Edit-Conf $instance 'pg_hba.conf' { param($t) Add-AccessLines $t $ranges } "Let in: $([string]::Join(', ', $ranges))" $changed
    & netsh.exe advfirewall firewall show rule "name=$FirewallRule" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $out = & netsh.exe advfirewall firewall add rule "name=$FirewallRule" dir=in action=allow protocol=TCP "localport=$port" "remoteip=$TailscaleRange,LocalSubnet" 2>&1
        if ($LASTEXITCODE -ne 0) { return Step-Result 'openConnections' $false "Windows Firewall wasn't changed: $(($out | Out-String).Trim())" $changed }
        $changed.Add("Windows Firewall allows port $port from Tailscale and this PC's network") | Out-Null
    }
    if (@($changed | Where-Object { $_.StartsWith('PostgreSQL') -or $_.StartsWith('Let in') }).Count -gt 0) {
        try { Restart-Postgres $instance }
        catch { return Step-Result 'openConnections' $false "The settings were changed, but PostgreSQL didn't restart: $($_.Exception.Message) Restart the `"$($instance.Service)`" service in Services." $changed }
    }
    return Step-Result 'openConnections' $true 'Other PCs can connect.' $changed
}

function Close-Connections($Step) {
    $changed = New-Object System.Collections.ArrayList
    & netsh.exe advfirewall firewall show rule "name=$FirewallRule" | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $out = & netsh.exe advfirewall firewall delete rule "name=$FirewallRule" 2>&1
        if ($LASTEXITCODE -ne 0) { return Step-Result 'undoOpenConnections' $false "Windows Firewall wasn't changed: $(($out | Out-String).Trim())" $changed }
        $changed.Add('The Windows Firewall rule is removed') | Out-Null
    }
    $instance = Find-Instance (Get-PostgresServices) ([int]$Step.port)
    if ($instance) {
        $hba = Join-Path $instance.DataDir 'pg_hba.conf'
        if (Test-Path -LiteralPath $hba) {
            $before = [IO.File]::ReadAllText($hba)
            $after = Remove-AccessLines $before
            if ($after -cne $before) {
                Copy-Item -LiteralPath $hba -Destination "$hba.before-xiv-fleet-$(Get-Stamp)"
                [IO.File]::WriteAllText($hba, $after)
                $changed.Add('Other PCs are no longer let in') | Out-Null
                try { Restart-Postgres $instance }
                catch { return Step-Result 'undoOpenConnections' $false "pg_hba.conf was changed, but PostgreSQL didn't restart: $($_.Exception.Message)" $changed }
            }
        }
    }
    $message = "Other PCs weren't let in, so nothing to undo."
    if ($changed.Count -gt 0) { $message = 'Other PCs are no longer let in.' }
    return Step-Result 'undoOpenConnections' $true $message $changed
}

# Removes the PostgreSQL that uses the port with its own uninstaller, then its data folder (which the uninstaller
# leaves) and the firewall rule. Only folders that are plainly PostgreSQL's are deleted.
function Remove-Postgres($Step) {
    $instance = Find-Instance (Get-PostgresServices) ([int]$Step.port)
    if (-not $instance) { return Step-Result 'removePostgres' $true 'PostgreSQL was already gone.' $null }
    $uninstaller = $null
    if ($instance.InstallDir) { $uninstaller = Join-Path $instance.InstallDir 'uninstall-postgresql.exe' }
    if (-not $uninstaller -or -not (Test-Path -LiteralPath $uninstaller)) {
        return Step-Result 'removePostgres' $false "Couldn't find PostgreSQL's uninstaller, so it wasn't removed. Remove `"PostgreSQL`" in Windows' Installed apps instead." $null
    }
    & sc.exe stop $instance.Service | Out-Null
    $code = Invoke-Waited $uninstaller @('--mode', 'unattended')
    if (-not (Test-Installed $code)) { return Step-Result 'removePostgres' $false "PostgreSQL's uninstaller stopped with code $code." $null }
    for ($i = 0; $i -lt 90 -and (Test-Path -LiteralPath $uninstaller); $i++) { Start-Sleep -Seconds 2 }
    if (Test-Path -LiteralPath (Join-Path $instance.DataDir 'PG_VERSION')) { Remove-Item -LiteralPath $instance.DataDir -Recurse -Force }
    $parent = Split-Path $instance.InstallDir -Parent
    if ((Split-Path $parent -Leaf) -match '^postgresql$') {
        if (Test-Path -LiteralPath $instance.InstallDir) { Remove-Item -LiteralPath $instance.InstallDir -Recurse -Force }
        if ((Test-Path -LiteralPath $parent) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent -Force }
    }
    & netsh.exe advfirewall firewall delete rule "name=$FirewallRule" | Out-Null
    return Step-Result 'removePostgres' $true 'PostgreSQL and its databases are removed.' $null
}

function Remove-Tailscale {
    $code = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        Get-ItemProperty | Where-Object { $_.DisplayName -eq 'Tailscale' -and $_.WindowsInstaller -eq 1 } | ForEach-Object { $_.PSChildName } | Select-Object -First 1
    if (-not $code) { return Step-Result 'removeTailscale' $true 'Tailscale was already gone.' $null }
    $exit = Invoke-Waited 'msiexec.exe' @('/x', $code, '/quiet', '/norestart')
    if (Test-Installed $exit) { return Step-Result 'removeTailscale' $true 'Tailscale is removed.' $null }
    return Step-Result 'removeTailscale' $false "Tailscale's uninstaller stopped with code $exit." $null
}

function Invoke-Step($Step) {
    switch ($Step.kind) {
        'installPostgres' {
            $code = Invoke-Waited $Step.installer @('--mode', 'unattended', '--unattendedmodeui', 'none', '--optionfile', "`"$($Step.optionFile)`"")
            if (Test-Installed $code) { return Step-Result $Step.kind $true 'PostgreSQL is installed.' $null }
            return Step-Result $Step.kind $false "PostgreSQL's installer stopped with code $code." $null
        }
        'installTailscale' {
            $code = Invoke-Waited 'msiexec.exe' @('/i', "`"$($Step.msi)`"", '/quiet', '/norestart')
            if (Test-Installed $code) { return Step-Result $Step.kind $true 'Tailscale is installed.' $null }
            return Step-Result $Step.kind $false "Tailscale's installer stopped with code $code." $null
        }
        'openConnections' { return Open-Connections $Step }
        'undoOpenConnections' { return Close-Connections $Step }
        'removePostgres' { return Remove-Postgres $Step }
        'removeTailscale' { return Remove-Tailscale }
        default { return Step-Result $Step.kind $false "Unknown step `"$($Step.kind)`"." $null }
    }
}

if ($RequestFile) {
    $request = $null
    $results = New-Object System.Collections.ArrayList
    try {
        $request = Get-Content -Raw -LiteralPath $RequestFile | ConvertFrom-Json
        foreach ($step in $request.steps) {
            try { $r = Invoke-Step $step }
            catch { $r = Step-Result $step.kind $false $_.Exception.Message $null }
            $results.Add($r) | Out-Null
            try { ConvertTo-Json -InputObject @($results) -Depth 5 | Set-Content -LiteralPath $request.progressFile -Encoding UTF8 } catch { }
            if (-not $r.ok -and -not $request.keepGoing) { break }
        }
        $ok = ($results.Count -eq @($request.steps).Count) -and (@($results | Where-Object { -not $_.ok }).Count -eq 0)
        $result = [pscustomobject]@{ ok = $ok; results = @($results) }
    }
    catch {
        $result = [pscustomobject]@{ ok = $false; error = $_.Exception.Message; results = @($results) }
    }
    if ($request -and $request.resultFile) { ConvertTo-Json -InputObject $result -Depth 6 | Set-Content -LiteralPath $request.resultFile -Encoding UTF8 }
}
