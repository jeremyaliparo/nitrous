<#
.SYNOPSIS
    Read-only probe for Acer's custom boot logo support.

.DESCRIPTION
    Answers two questions without writing anything:
      * does SMBIOS type 0xAC list gaming record 0x0D (custom boot logo)?
      * does AcerGamingFunction answer GetGamingMiscSetting(0x08), the BIOS setup's
        "Customize POST animation" switch?

    Either one is enough for the firmware to read \EFI\OEM\AcerLogo.gif|jpg at POST.
    Nothing here opens the EFI system partition, writes a setting or touches the EC.

.EXAMPLE
    pwsh -File tools\probe-boot-logo.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

Add-Type -Namespace Win32 -Name Firmware -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern uint GetSystemFirmwareTable(uint provider, uint tableId, System.IntPtr buffer, uint size);
'@

function Read-SmbiosTable {
    $provider = 0x52534D42 # 'RSMB'
    $size = [Win32.Firmware]::GetSystemFirmwareTable($provider, 0, [IntPtr]::Zero, 0)
    if ($size -eq 0) { throw "GetSystemFirmwareTable failed: $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)" }

    $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal([int]$size)
    try {
        $read = [Win32.Firmware]::GetSystemFirmwareTable($provider, 0, $buffer, $size)
        if ($read -eq 0) { throw 'GetSystemFirmwareTable returned nothing' }
        $bytes = New-Object byte[] $read
        [Runtime.InteropServices.Marshal]::Copy($buffer, $bytes, 0, [int]$read)

        # RawSMBIOSData: 4 bytes of version, then the table length, then the table itself.
        if ($bytes.Length -lt 8) { throw 'SMBIOS table is too short' }
        $length = [Math]::Min([BitConverter]::ToUInt32($bytes, 4), [uint32]($bytes.Length - 8))
        return $bytes[8..(7 + $length)]
    }
    finally {
        [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
    }
}

function Get-GamingRecords([byte[]]$table) {
    $major = $null
    $minor = $null
    $records = @()
    $offset = 0

    while ($offset + 4 -le $table.Length) {
        $type = $table[$offset]
        $length = $table[$offset + 1]
        if ($length -lt 4 -or $offset + $length -gt $table.Length) { break }

        if ($type -eq 0xAC -and $length -ge 6) {
            $major = $table[$offset + 4]
            $minor = $table[$offset + 5]
            for ($i = 6; $i + 3 -le $length; $i += 3) {
                $records += [pscustomobject]@{
                    Id    = $table[$offset + $i]
                    Value = [int]$table[$offset + $i + 1] -bor ([int]$table[$offset + $i + 2] -shl 8)
                }
            }
        }

        if ($type -eq 0x7F) { break }

        $next = $offset + $length
        while ($next + 1 -lt $table.Length -and -not ($table[$next] -eq 0 -and $table[$next + 1] -eq 0)) { $next++ }
        $offset = $next + 2
    }

    return [pscustomobject]@{ Major = $major; Minor = $minor; Records = $records }
}

function Invoke-Misc([uint32]$address) {
    try {
        $instance = Get-CimInstance -Namespace 'root\wmi' -ClassName 'AcerGamingFunction' -ErrorAction Stop |
            Select-Object -First 1
        if (-not $instance) { return $null }
        $out = Invoke-CimMethod -InputObject $instance -MethodName 'GetGamingMiscSetting' `
            -Arguments @{ gmInput = $address } -ErrorAction Stop
        return $out.gmOutput
    }
    catch {
        Write-Host "  AcerGamingFunction: $($_.Exception.Message)" -ForegroundColor DarkYellow
        return $null
    }
}

Write-Host 'SMBIOS gaming structure' -ForegroundColor Cyan
$gaming = $null
try {
    $table = Read-SmbiosTable
    $gaming = Get-GamingRecords $table
}
catch {
    Write-Host "  could not read SMBIOS: $($_.Exception.Message)" -ForegroundColor Red
}

if ($gaming -and $null -ne $gaming.Major) {
    $version = '{0}.{1:D2}' -f $gaming.Major, $gaming.Minor
    Write-Host "  gaming interface: $version, $($gaming.Records.Count) records"
    foreach ($r in $gaming.Records) {
        $name = switch ($r.Id) {
            0x08 { 'keyboard type' }
            0x0A { 'keyboard colour' }
            0x0D { 'CUSTOM BOOT LOGO' }
            0x0F { 'operating modes' }
            0x17 { 'light bars' }
            default { '' }
        }
        Write-Host ('    0x{0:X2} = 0x{1:X4} {2}' -f $r.Id, $r.Value, $name)
    }
    $logo = $gaming.Records | Where-Object { $_.Id -eq 0x0D } | Select-Object -First 1
    Write-Host ("  record 0x0D: " + $(if ($logo) { "present, value 0x{0:X4}" -f $logo.Value } else { 'absent' }))
}
else {
    Write-Host '  type 0xAC not found' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Acer WMI misc settings' -ForegroundColor Cyan
$misc8 = Invoke-Misc 0x08
if ($null -eq $misc8) {
    Write-Host '  misc 0x08: no answer' -ForegroundColor Yellow
}
else {
    $value = [int]$misc8 -band 0xFF
    Write-Host ("  misc 0x08 (Customize POST animation): raw 0x{0:X16}, low byte {1}" -f [uint64]$misc8, $value)
    Write-Host ('    -> ' + $(if ($value -in 0, 1) { 'firmware has the custom boot logo switch' } else { 'no switch (0xFF/other means the id is absent)' }))
}

$misc6 = Invoke-Misc 0x06
if ($null -ne $misc6) {
    Write-Host ("  misc 0x06 (boot animation): raw 0x{0:X16}, low byte {1}" -f [uint64]$misc6, ([int]$misc6 -band 0xFF))
}

Write-Host ''
Write-Host 'Result' -ForegroundColor Cyan
$logoRecord = $null
if ($gaming) { $logoRecord = $gaming.Records | Where-Object { $_.Id -eq 0x0D } | Select-Object -First 1 }
$smbiosYes = ($null -ne $logoRecord -and $logoRecord.Value -eq 1)
$wmiYes = ($null -ne $misc8 -and (([int]$misc8 -band 0xFF) -in 0, 1))
if ($smbiosYes -or $wmiYes) {
    Write-Host '  custom boot logo: SUPPORTED' -ForegroundColor Green
    Write-Host "  (SMBIOS 0x0D: $(if ($smbiosYes) { 'yes' } else { 'no' }), WMI misc 0x08: $(if ($wmiYes) { 'yes' } else { 'no' }))"
}
else {
    Write-Host '  custom boot logo: not advertised by this firmware' -ForegroundColor Yellow
}
