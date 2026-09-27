# Generates a small Format 1 MIDI file used to smoke-test the parser, the piano
# roll and the playback engine: a C-major melody, a closing triad and a bass line.
#
#   .\make-test-midi.ps1 D:\tmp\sample.mid
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'

# PowerShell unwraps single-element arrays, which breaks List<byte>.AddRange.
function Add-Bytes($list, [byte[]]$bytes) { foreach ($b in $bytes) { [void]$list.Add($b) } }

function VarLen([int]$v) {
    $b = New-Object System.Collections.Generic.List[byte]
    [void]$b.Add([byte]($v -band 0x7F)); $v = $v -shr 7
    while ($v -gt 0) { $b.Insert(0, [byte](($v -band 0x7F) -bor 0x80)); $v = $v -shr 7 }
    return , $b.ToArray()
}

function Str([string]$s) { return , [System.Text.Encoding]::ASCII.GetBytes($s) }

function Track([byte[]]$body) {
    $n = $body.Length
    $o = New-Object System.Collections.Generic.List[byte]
    Add-Bytes $o (Str 'MTrk')
    Add-Bytes $o ([byte[]]@(
        [byte](($n -shr 24) -band 0xFF), [byte](($n -shr 16) -band 0xFF),
        [byte](($n -shr 8) -band 0xFF), [byte]($n -band 0xFF)))
    Add-Bytes $o $body
    return , $o.ToArray()
}

function Meta([int]$type, [byte[]]$data) {
    $o = New-Object System.Collections.Generic.List[byte]
    Add-Bytes $o (VarLen 0)
    Add-Bytes $o ([byte[]]@(0xFF, [byte]$type))
    Add-Bytes $o (VarLen $data.Length)
    Add-Bytes $o $data
    return , $o.ToArray()
}

# Conductor: 120 bpm, 4/4
$t0 = New-Object System.Collections.Generic.List[byte]
Add-Bytes $t0 (Meta 0x03 (Str 'Conductor'))
Add-Bytes $t0 (Meta 0x51 ([byte[]]@(0x07, 0xA1, 0x20)))   # 500000 us per quarter
Add-Bytes $t0 (Meta 0x58 ([byte[]]@(4, 2, 24, 8)))
Add-Bytes $t0 (Meta 0x2F ([byte[]]@()))
$track0 = Track $t0.ToArray()

# Melody: ascending scale, then a two-beat triad
$t1 = New-Object System.Collections.Generic.List[byte]
Add-Bytes $t1 (Meta 0x03 (Str 'Melody'))
foreach ($p in @(60, 62, 64, 65, 67, 69, 71, 72)) {
    Add-Bytes $t1 (VarLen 0);   Add-Bytes $t1 ([byte[]]@(0x90, [byte]$p, 100))
    Add-Bytes $t1 (VarLen 480); Add-Bytes $t1 ([byte[]]@(0x80, [byte]$p, 64))
}
# Each delta must be followed by a status byte, so the first release carries the
# 960-tick delta and the rest carry zero.
foreach ($p in @(60, 64, 67)) { Add-Bytes $t1 (VarLen 0); Add-Bytes $t1 ([byte[]]@(0x90, [byte]$p, 90)) }
Add-Bytes $t1 (VarLen 960); Add-Bytes $t1 ([byte[]]@(0x80, 60, 64))
Add-Bytes $t1 (VarLen 0);   Add-Bytes $t1 ([byte[]]@(0x80, 64, 64))
Add-Bytes $t1 (VarLen 0);   Add-Bytes $t1 ([byte[]]@(0x80, 67, 64))
Add-Bytes $t1 (Meta 0x2F ([byte[]]@()))
$track1 = Track $t1.ToArray()

# Bass: C3 then G3, two beats each
$t2 = New-Object System.Collections.Generic.List[byte]
Add-Bytes $t2 (Meta 0x03 (Str 'Bass'))
Add-Bytes $t2 (VarLen 0);    Add-Bytes $t2 ([byte[]]@(0x90, 48, 80))
Add-Bytes $t2 (VarLen 1920); Add-Bytes $t2 ([byte[]]@(0x80, 48, 64))
Add-Bytes $t2 (VarLen 0);    Add-Bytes $t2 ([byte[]]@(0x90, 55, 80))
Add-Bytes $t2 (VarLen 1920); Add-Bytes $t2 ([byte[]]@(0x80, 55, 64))
Add-Bytes $t2 (Meta 0x2F ([byte[]]@()))
$track2 = Track $t2.ToArray()

$out = New-Object System.Collections.Generic.List[byte]
Add-Bytes $out (Str 'MThd')
Add-Bytes $out ([byte[]]@(0, 0, 0, 6))
Add-Bytes $out ([byte[]]@(0, 1))          # format 1
Add-Bytes $out ([byte[]]@(0, 3))          # three tracks
Add-Bytes $out ([byte[]]@(0x01, 0xE0))    # 480 ticks per quarter
Add-Bytes $out $track0
Add-Bytes $out $track1
Add-Bytes $out $track2

$dir = Split-Path -Parent $Path
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($Path, $out.ToArray())
"wrote $Path ($($out.Count) bytes)"
