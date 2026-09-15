$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '..\raspi\usb-hid-gadget.sh'
$servicePath = Join-Path $PSScriptRoot '..\raspi\usb-hid-gadget.service'

function Assert-Contains {
    param([string]$Text, [string]$Expected, [string]$Message)
    if (-not $Text.Contains($Expected)) { throw $Message }
}

function Assert-Matches {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -notmatch $Pattern) { throw $Message }
}

function Get-RequiredIndex {
    param([string]$Text, [string]$Needle, [string]$Message)
    $index = $Text.IndexOf($Needle, [System.StringComparison]::Ordinal)
    if ($index -lt 0) { throw $Message }
    return $index
}

function Assert-Descriptor {
    param([string]$Script, [string]$FunctionName, [string]$ExpectedHex)

    $pattern = "'(?<bytes>(?:\\x[0-9A-Fa-f]{2})+)'\s*>\s*`"?" +
        [regex]::Escape("`$GADGET/functions/hid.$FunctionName/report_desc") + '"?'
    $match = [regex]::Match($Script, $pattern)
    if (-not $match.Success) { throw "Missing literal report descriptor for $FunctionName" }

    $actualHex = ($match.Groups['bytes'].Value -replace '\\x', '').ToUpperInvariant()
    $expected = ($ExpectedHex -replace '\s', '').ToUpperInvariant()
    if ($actualHex -ne $expected) {
        throw "$FunctionName descriptor mismatch.`nExpected: $expected`nActual:   $actualHex"
    }
}

if (-not (Test-Path -LiteralPath $scriptPath)) { throw "Missing gadget script: $scriptPath" }
if (-not (Test-Path -LiteralPath $servicePath)) { throw "Missing systemd service: $servicePath" }

$script = (Get-Content -Raw -LiteralPath $scriptPath) -replace "`r`n", "`n"
$service = (Get-Content -Raw -LiteralPath $servicePath) -replace "`r`n", "`n"

Assert-Matches $script '^#!/(?:usr/)?bin/(?:env )?bash' 'Gadget script must use Bash'
Assert-Contains $script 'set -euo pipefail' 'Gadget script must retain strict Bash settings'

$keyboardHex = @'
05 01 09 06 A1 01 05 07 19 E0 29 E7 15 00 25 01 75 01 95 08 81 02
95 01 75 08 81 01 95 06 75 08 15 00 26 A4 00 05 07 19 00 2A A4 00
81 00 95 05 75 01 05 08 19 01 29 05 91 02 95 01 75 03 91 01 C0
'@
$mouseHex = @'
05 01 09 02 A1 01 09 01 A1 00 05 09 19 01 29 05 15 00 25 01 95 05
75 01 81 02 95 01 75 03 81 01 05 01 09 30 09 31 09 38 15 81 25 7F
75 08 95 03 81 06 05 0C 0A 38 02 15 81 25 7F 75 08 95 01 81 06 C0 C0
'@
$consumerHex = @'
05 0C 09 01 A1 01 15 00 26 FF 03 19 00 2A FF 03 75 10 95 01 81 00 C0
'@

Assert-Descriptor $script 'keyboard' $keyboardHex
Assert-Descriptor $script 'mouse' $mouseHex
Assert-Descriptor $script 'consumer' $consumerHex
Assert-Contains $script "printf '%s' '0x0101' > `"`$GADGET/bcdDevice`"" 'USB device revision must change when the composite descriptor layout changes'

foreach ($expected in @(
    "printf '%s' '1' > `"`$GADGET/functions/hid.keyboard/protocol`"",
    "printf '%s' '1' > `"`$GADGET/functions/hid.keyboard/subclass`"",
    "printf '%s' '8' > `"`$GADGET/functions/hid.keyboard/report_length`"",
    "printf '%s' '0' > `"`$GADGET/functions/hid.mouse/protocol`"",
    "printf '%s' '0' > `"`$GADGET/functions/hid.mouse/subclass`"",
    "printf '%s' '5' > `"`$GADGET/functions/hid.mouse/report_length`"",
    "printf '%s' '0' > `"`$GADGET/functions/hid.consumer/protocol`"",
    "printf '%s' '0' > `"`$GADGET/functions/hid.consumer/subclass`"",
    "printf '%s' '2' > `"`$GADGET/functions/hid.consumer/report_length`""
)) {
    Assert-Contains $script $expected "Missing HID attribute/configuration token: $expected"
}

Assert-Matches $script '(?s)mkdir -p "\$GADGET/functions/hid\.keyboard".*mkdir -p "\$GADGET/functions/hid\.mouse".*mkdir -p "\$GADGET/functions/hid\.consumer"' 'HID functions must be created in keyboard, mouse, consumer order'
Assert-Matches $script '(?s)ln -s "\$GADGET/functions/hid\.keyboard" "\$GADGET/configs/c\.1/01-keyboard".*ln -s "\$GADGET/functions/hid\.mouse" "\$GADGET/configs/c\.1/02-mouse".*ln -s "\$GADGET/functions/hid\.consumer" "\$GADGET/configs/c\.1/03-consumer"' 'Stable config links must be created in 01/02/03 order'

foreach ($name in @('01-keyboard', '02-mouse', '03-consumer', 'hid.keyboard', 'hid.mouse', 'hid.consumer')) {
    Assert-Contains $script $name "Cleanup/configuration must cover $name"
}
foreach ($link in @('01-keyboard', '02-mouse', '03-consumer')) {
    Assert-Contains $script "rm -f `"`$GADGET/configs/c.1/$link`"" "Cleanup must remove $link"
}
foreach ($function in @('keyboard', 'mouse', 'consumer')) {
    Assert-Contains $script "rmdir `"`$GADGET/functions/hid.$function`"" "Cleanup must remove hid.$function"
}
Assert-Matches $script '(?m)^\s*printf ''\\n'' > "\$GADGET/UDC"' 'Cleanup must write a newline to the UDC attribute to trigger ConfigFS unbind'
if ($script.Contains("printf '%s' '' > `"`$GADGET/UDC`"")) {
    throw 'Cleanup must not use a zero-byte printf write for ConfigFS unbind'
}

$trapInstallIndex = Get-RequiredIndex $script "trap 'remove_gadget' EXIT" 'A failed start must clean up the partially created or bound gadget'
$gadgetCreateIndex = Get-RequiredIndex $script 'mkdir -p "$GADGET"' 'Missing gadget directory creation'
$descriptorIndex = Get-RequiredIndex $script 'functions/hid.keyboard/report_desc' 'Missing HID descriptor write'
$bindIndex = Get-RequiredIndex $script 'printf ''%s'' "$UDC_NAME" > "$GADGET/UDC"' 'Missing UDC bind'
$pollIndex = Get-RequiredIndex $script 'for ((attempt = 1; attempt <= max_attempts; attempt++))' 'Missing HID node poll'
$chgrpIndex = Get-RequiredIndex $script 'chgrp hidbridge "$node"' 'Missing HID group assignment'
$chmodIndex = Get-RequiredIndex $script 'chmod 0660 "$node"' 'Missing HID mode assignment'
$trapClearIndex = Get-RequiredIndex $script 'trap - EXIT' 'A successful start must retain the bound gadget'

foreach ($operation in @(
    @{ Name = 'gadget creation'; Index = $gadgetCreateIndex },
    @{ Name = 'descriptor write'; Index = $descriptorIndex },
    @{ Name = 'UDC bind'; Index = $bindIndex },
    @{ Name = 'HID node poll'; Index = $pollIndex }
)) {
    if ($trapInstallIndex -ge $operation.Index) {
        throw "EXIT cleanup trap must be installed before $($operation.Name)"
    }
}
if ($trapClearIndex -le $chgrpIndex -or $trapClearIndex -le $chmodIndex) {
    throw 'EXIT cleanup trap must remain installed until all HID ownership and mode operations succeed'
}
Assert-Matches $script '(?s)for node in "\$\{expected_nodes\[@\]\}"; do\s+\[\[ -c "\$node" \]\] \|\| \{.*?\}\s+chgrp hidbridge "\$node"\s+chmod 0660 "\$node"\s+done\s+trap - EXIT' 'EXIT cleanup trap may only be cleared after the complete permission loop succeeds'
Assert-Contains $script '/sys/class/udc' 'Script must discover UDCs from sysfs'
Assert-Matches $script '(?s)mapfile -t UDC.*sort' 'UDC selection must be deterministic'
Assert-Matches $script '(?s)(exactly one|More than one|multiple).*UDC' 'Multiple UDC behavior must be explicit'

foreach ($node in @('/dev/hidg0', '/dev/hidg1', '/dev/hidg2')) {
    Assert-Contains $script $node "Post-bind validation must explicitly expect $node"
}
Assert-Contains $script '/dev/hidg3' 'Post-bind validation must reject an unexpected fourth HID node'
Assert-Matches $script '(?s)(timeout|deadline|attempt|max_attempt)' 'Post-bind HID-node polling must have a bounded timeout'
Assert-Matches $script '\[\[\s+-c\s+"\$node"\s+\]\]' 'Each expected HID node must be verified as a character device'
Assert-Matches $script '(?m)^\s*getent group hidbridge' 'The hidbridge group must be checked, not created'
if ($script -match '(?m)^\s*(groupadd|addgroup)\b') { throw 'Gadget script must not create the hidbridge group' }
Assert-Matches $script '(?m)^\s*chgrp hidbridge "\$node"' 'Only expected HID nodes must be assigned to hidbridge'
Assert-Matches $script '(?m)^\s*chmod 0660 "\$node"' 'Only expected HID nodes must receive mode 0660'
if ($script -match 'chmod\s+[^\n]*(?:/dev/\*|/dev/hidg\*)') { throw 'Gadget script must not broaden wildcard device permissions' }
Assert-Contains $script 'keyboard + mouse + consumer' 'Gadget configuration string must describe all three interfaces'

Assert-Contains $service 'Description=USB HID gadget (keyboard + mouse + consumer)' 'Service description must name all interfaces'
Assert-Matches $service '(?m)^After=local-fs\.target systemd-modules-load\.service$' 'Service must start after local filesystems and modules'
Assert-Matches $service '(?m)^Before=network-hid-bridge\.service$' 'Receiver must start after gadget creation'
Assert-Matches $service '(?m)^Type=oneshot$' 'Service must be oneshot'
Assert-Matches $service '(?m)^RemainAfterExit=yes$' 'Service must remain active after start'
Assert-Matches $service '(?m)^ExecStart=/usr/local/sbin/usb-hid-gadget start$' 'Service ExecStart must be exact'
Assert-Matches $service '(?m)^ExecStop=/usr/local/sbin/usb-hid-gadget stop$' 'Service ExecStop must be exact'
Assert-Matches $service '(?m)^WantedBy=multi-user\.target$' 'Service must be enabled by multi-user.target'

Write-Host 'USB HID gadget configuration checks passed.'
