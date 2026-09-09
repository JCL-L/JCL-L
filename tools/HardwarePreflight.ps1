$ErrorActionPreference = 'Continue'

$pcIp = '192.168.4.100'
$plcIp = '192.168.4.101'
$plcPort = 102
$loadIp = '192.168.4.102'
$loadPort = 7000
$scopeIp = '192.168.4.128'
$scopePort = 5025
Write-Host 'NinOne 现场只读预检'
Write-Host ('时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

Write-Host "`n运行环境"
$netFx = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if ($null -eq $netFx -or $netFx.Release -lt 528040) {
    Write-Host '.NET Framework 4.8: 缺失'
} else {
    Write-Host ('.NET Framework 4.8+: 已安装，Release=' + $netFx.Release)
}
$vcRuntimeFiles = @(
    (Join-Path $env:WINDIR 'SysWOW64\msvcr120.dll')
    (Join-Path $env:WINDIR 'SysWOW64\msvcp120.dll')
)
foreach ($runtimeFile in $vcRuntimeFiles) {
    Write-Host ((Split-Path $runtimeFile -Leaf) + ': ' + $(if (Test-Path -LiteralPath $runtimeFile) { '存在' } else { '缺失，请安装 VC++ 2013 x86' }))
}

Write-Host "`nIPv4 地址"
Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue |
    Where-Object { $_.IPEnabled } |
    ForEach-Object { $_.IPAddress } |
    Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object -Unique
$ipv4 = Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue |
    Where-Object { $_.IPEnabled } |
    ForEach-Object { $_.IPAddress } |
    Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' }
Write-Host ($pcIp + ': ' + $(if ($ipv4 -contains $pcIp) { '存在' } else { '缺失' }))

Write-Host "`n串口"
$portEvidence = @()
foreach ($name in @([System.IO.Ports.SerialPort]::GetPortNames())) {
    if ($name -match '^(?i:COM[1-9][0-9]*)$') {
        $portEvidence += [pscustomobject]@{ Name = $name.ToUpperInvariant(); Source = '.NET x64' }
    }
}

$powershellX86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
if (Test-Path -LiteralPath $powershellX86) {
    foreach ($name in @(& $powershellX86 -NoProfile -NonInteractive -Command '[System.IO.Ports.SerialPort]::GetPortNames()' 2>$null)) {
        if ($name -match '^(?i:COM[1-9][0-9]*)$') {
            $portEvidence += [pscustomobject]@{ Name = $name.ToUpperInvariant(); Source = '.NET x86' }
        }
    }
}

$serialMap = Get-ItemProperty 'HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM' -ErrorAction SilentlyContinue
if ($null -ne $serialMap) {
    foreach ($property in $serialMap.PSObject.Properties) {
        $name = [string]$property.Value
        if ($property.Name -notmatch '^PS' -and $name -match '^(?i:COM[1-9][0-9]*)$') {
            $portEvidence += [pscustomobject]@{ Name = $name.ToUpperInvariant(); Source = '注册表' }
        }
    }
}

foreach ($device in @(Get-CimInstance Win32_SerialPort -ErrorAction SilentlyContinue)) {
    $name = [string]$device.DeviceID
    if ($name -match '^(?i:COM[1-9][0-9]*)$') {
        $portEvidence += [pscustomobject]@{ Name = $name.ToUpperInvariant(); Source = 'Win32_SerialPort' }
    }
}

foreach ($device in @(Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue)) {
    $match = [regex]::Match([string]$device.Name, '(?i)\((COM[1-9][0-9]*)\)')
    if ($match.Success) {
        $portEvidence += [pscustomobject]@{ Name = $match.Groups[1].Value.ToUpperInvariant(); Source = '设备管理器' }
    }
}

$ports = @($portEvidence | Select-Object -ExpandProperty Name -Unique | Sort-Object)
if ($ports.Count -eq 0) { Write-Host '所有枚举来源均未发现串口' } else { Write-Host ('发现: ' + ($ports -join ', ')) }
if ($portEvidence.Count -gt 0) {
    $portEvidence | Sort-Object Name, Source -Unique | Format-Table -AutoSize
}
Write-Host 'GPD2303S、GDM9061、PA333H 的串口和波特率在各自设备页面设置；预检不再按 INI 中的固定 COM 号判定缺失。'

Write-Host "`n网络"
Write-Host '以下目标是程序界面默认值；若现场已在界面改址，请按实际地址另行测试。'
$results = @()
$targets = @(
    @{ Device = 'PLC S7'; Host = $plcIp; Port = $plcPort },
    @{ Device = 'N69206'; Host = $loadIp; Port = $loadPort },
    @{ Device = 'ZDS2024C'; Host = $scopeIp; Port = $scopePort }
)
foreach ($target in $targets) {
    $pingClient = New-Object System.Net.NetworkInformation.Ping
    try {
        $ping = $pingClient.Send($target.Host, 1000).Status -eq [System.Net.NetworkInformation.IPStatus]::Success
    } catch {
        $ping = $false
    } finally {
        $pingClient.Dispose()
    }
    $tcp = $null
    if ($target.Port -gt 0) {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $pending = $client.BeginConnect($target.Host, $target.Port, $null, $null)
            $tcp = $pending.AsyncWaitHandle.WaitOne(1000)
            if ($tcp) { $client.EndConnect($pending) }
        } catch {
            $tcp = $false
        } finally {
            $client.Dispose()
        }
    }
    $results += [pscustomobject]@{ Device = $target.Device; IP = $target.Host; Ping = $ping; Port = $target.Port; Tcp = $tcp }
}
$results | Format-Table -AutoSize

Write-Host 'PLC 使用内置 S7 客户端直连 TCP 102，不需要 NI OPC Servers。'

Write-Host "`nZLG/CANFD 即插即用设备"
$zlg = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'ZLG|CANFD|USBCAN|CAN-?FD' -or $_.PNPDeviceID -match 'ZLG|USBCAN' } |
    Select-Object Status, Name, PNPDeviceID
if ($null -eq $zlg) { Write-Host '未枚举到' } else { $zlg | Format-Table -AutoSize }
