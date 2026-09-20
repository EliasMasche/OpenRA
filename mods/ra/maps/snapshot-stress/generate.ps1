# Regenerates this map: one of every placeable ra actor, alternating sides, a self-playing bot, ore
# beside each refinery, and a water bay with the naval actors.
#
#   pwsh mods/ra/maps/snapshot-stress/generate.ps1
#   bin/OpenRA.Utility.exe ra --map refresh snapshot-stress   # regenerates the preview and map.yaml
#
# The terrain is flat SNOW clear ground (tile 255) with a water bay (tile 1) and ore (resource 1).
$ErrorActionPreference = 'Stop'
$outDir = $PSScriptRoot
$repo = Split-Path (Split-Path (Split-Path (Split-Path $outDir -Parent) -Parent) -Parent) -Parent
$W = 140
$H = 140
$Bounds = "1,1,$($W - 2),$($H - 2)"

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$rulesDir = Join-Path $repo 'mods\ra\rules'
function Get-ActorKeys($file) {
	$names = @()
	foreach ($line in Get-Content (Join-Path $rulesDir $file)) {
		if ($line -match '^([A-Za-z0-9_]+):\s*$') { $names += $Matches[1] }
	}
	return $names
}

# kind -> actors, in mod.yaml Rules order. System actors and editor markers are handled separately.
$plan = [ordered]@{
	'vehicles.yaml'   = 'vehicle'
	'structures.yaml' = 'building'
	'infantry.yaml'   = 'infantry'
	'civilian.yaml'   = 'civilian'
	'decoration.yaml' = 'decoration'
	'aircraft.yaml'   = 'aircraft'
	'ships.yaml'      = 'ship'
	'fakes.yaml'      = 'building'
	'misc.yaml'       = 'misc'
}
$skip = @('mpspawn', 'waypoint', 'world', 'player')

# Bridge spans and huts are terrain-construction actors: the map editor builds them as a matched chain
# over a crossing, and a span placed on its own has no footprint to update when it is damaged.
$skip += @('br1', 'br2', 'br3', 'bridge1', 'bridge2', 'bridge3', 'bridge4',
	'sbridge1', 'sbridge2', 'sbridge3', 'sbridge4', 'bridgehut')
$actors = [ordered]@{}
foreach ($kv in $plan.GetEnumerator()) {
	foreach ($name in (Get-ActorKeys $kv.Key)) {
		if ($name.StartsWith('^') -or $skip -contains $name) { continue }
		if (-not $actors.Contains($name)) { $actors[$name] = $kv.Value }
	}
}

# --- terrain -----------------------------------------------------------------
$tiles = New-Object 'byte[]' ($W * $H * 3)
$res = New-Object 'byte[]' ($W * $H * 2)
function Set-Tile([int]$x, [int]$y, [int]$tile, [int]$variant) {
	$i = ($x * $H + $y) * 3
	$t = [System.BitConverter]::GetBytes([uint16]$tile)
	$tiles[$i] = $t[0]; $tiles[$i + 1] = $t[1]; $tiles[$i + 2] = [byte]$variant
}
function Set-Ore([int]$x, [int]$y, [int]$type, [int]$density) {
	$i = ($x * $H + $y) * 2
	$res[$i] = [byte]$type; $res[$i + 1] = [byte]$density
}
for ($x = 0; $x -lt $W; $x++) {
	for ($y = 0; $y -lt $H; $y++) {
		if ($y -ge 126) { Set-Tile $x $y 1 0 } else { Set-Tile $x $y 255 (($x * 7 + $y * 3) % 20) }
	}
}
# Ore fields, one per side, directly below that side's base so the harvesters find them.
foreach ($ox in @(6, 104)) {
	for ($x = $ox; $x -lt $ox + 30; $x++) {
		for ($y = 114; $y -lt 125; $y++) {
			if (($x + $y) % 7 -eq 3) { continue }
			Set-Ore $x $y 1 12
		}
	}

	# A few gem cells, so the gem resource path is exercised too.
	for ($x = $ox + 4; $x -lt $ox + 9; $x++) { for ($y = 120; $y -lt 124; $y++) { Set-Ore $x $y 2 3 } }
}

$bin = New-Object 'System.Collections.Generic.List[byte]'
$bin.Add([byte]1)
$bin.AddRange([System.BitConverter]::GetBytes([uint16]$W))
$bin.AddRange([System.BitConverter]::GetBytes([uint16]$H))
$bin.AddRange($tiles)
$bin.AddRange($res)
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'map.bin'), $bin.ToArray())

# --- actor placement ---------------------------------------------------------
$lines = New-Object 'System.Collections.Generic.List[string]'
$n = 0
function Add-Actor([string]$name, [string]$owner, [int]$x, [int]$y, [string]$extra = '') {
	$script:n++
	$lines.Add("	Actor$($script:n): $($name.ToLowerInvariant())")
	$lines.Add("		Owner: $owner")
	$lines.Add("		Location: $x,$y")
	if ($extra) { $lines.Add("		$extra") }
}

$occasion = 0
function Next-Location([string]$kind) {
	# Coarse grids, one per kind, chosen so nothing overlaps the starter bases or the water.
	$script:occasion++
	switch ($kind) {
		'building' {
			$i = $script:occasion % 120
			$col = $i % 20; $row = [int]($i / 20)
			return @((6 + $col * 6), (40 + $row * 6))
		}
		'vehicle' {
			$i = $script:occasion % 200
			$col = $i % 26; $row = [int]($i / 26)
			$x = if ($row % 2 -eq 0) { 6 + $col * 2 } else { 56 - $col * 2 }
			return @($x, (20 + $row * 2))
		}
		'infantry' {
			$i = $script:occasion % 200
			$col = $i % 26; $row = [int]($i / 26)
			$x = if ($row % 2 -eq 0) { 74 + $col * 2 } else { 124 - $col * 2 }
			return @($x, (20 + $row * 2))
		}
		'aircraft' {
			$i = $script:occasion % 40
			$x = if ($i % 2 -eq 0) { 10 + $i * 2 } else { 130 - $i * 2 }
			return @($x, 92)
		}
		'ship' {
			$i = $script:occasion % 40
			return @((8 + $i * 3), 131)
		}
		'civilian' {
			$i = $script:occasion % 60
			$col = $i % 30; $row = [int]($i / 30)
			return @((8 + $col * 4), (8 + $row * 4))
		}
		'decoration' {
			$i = $script:occasion % 120
			$col = $i % 40; $row = [int]($i / 40)
			return @((6 + $col * 3), (66 + $row * 3))
		}
		'misc' {
			$i = $script:occasion % 60
			$col = $i % 20; $row = [int]($i / 20)
			return @((10 + $col * 6), (96 + $row * 4))
		}
	}
	throw "no placement rule for $kind"
}

# Starter bases, placed first so the bulk grid cannot land on them.
$bases = @(
	@{ Owner = 'Multi0'; X = 6;   Y = 94 },
	@{ Owner = 'Multi1'; X = 104; Y = 94 }
)
foreach ($b in $bases) {
	$o = $b.Owner
	Add-Actor 'fact' $o $b.X $b.Y
	Add-Actor 'powr' $o ($b.X + 8) $b.Y
	Add-Actor 'powr' $o ($b.X + 12) $b.Y
	Add-Actor 'proc' $o ($b.X + 4) ($b.Y + 4)
	Add-Actor 'tent' $o ($b.X + 12) ($b.Y + 4)
	Add-Actor 'weap' $o ($b.X + 16) ($b.Y + 4)
	Add-Actor 'fix' $o ($b.X + 20) ($b.Y + 4)
	Add-Actor 'afld' $o ($b.X + 4) ($b.Y + 10)
	Add-Actor 'hpad' $o ($b.X + 10) ($b.Y + 10)
	Add-Actor 'syrd' $o ($b.X + 14) ($b.Y + 16)
	Add-Actor 'atek' $o ($b.X + 4) ($b.Y + 14)
	Add-Actor 'gun' $o ($b.X + 20) ($b.Y + 10)
	Add-Actor 'pbox' $o ($b.X + 18) ($b.Y + 12)
	Add-Actor 'tsla' $o ($b.X + 22) ($b.Y + 12)
	Add-Actor 'harv' $o ($b.X + 16) ($b.Y + 22)
	Add-Actor 'harv' $o ($b.X + 18) ($b.Y + 24)
	Add-Actor 'harv' $o ($b.X + 20) ($b.Y + 22)
	Add-Actor 'mnly' $o ($b.X + 2) ($b.Y + 18)
	Add-Actor 'mcv' $o ($b.X + 6) ($b.Y + 18)
	Add-Actor 'mine' 'Neutral' ($b.X + 26) ($b.Y + 22)
}

# One of every actor in the mod's rule files, alternating sides so both players own each type.
foreach ($name in $actors.Keys) {
	$kind = $actors[$name]
	$loc = Next-Location $kind
	$owner = switch ($kind) {
		'vehicle' { if ($script:occasion % 2 -eq 0) { 'Multi0' } else { 'Multi1' } }
		'infantry' { if ($script:occasion % 2 -eq 0) { 'Multi0' } else { 'Multi1' } }
		'building' { if ($script:occasion % 2 -eq 0) { 'Multi0' } else { 'Multi1' } }
		'aircraft' { if ($script:occasion % 2 -eq 0) { 'Multi0' } else { 'Multi1' } }
		'ship' { if ($script:occasion % 2 -eq 0) { 'Multi0' } else { 'Multi1' } }
		'civilian' { 'Neutral' }
		'decoration' { 'Neutral' }
		default { 'Neutral' }
	}

	$extra = ''
	# A few damaged units, so repair and damage-state paths have state to keep.
	if ($kind -in @('vehicle', 'infantry') -and $script:occasion % 5 -eq 0) { $extra = 'Health: 45' }
	Add-Actor $name $owner $loc[0] $loc[1] $extra
}

# Extra combat units, so the two sides actually fight.
$combat = @('e1', 'e2', 'e3', 'e4', 'shok', 'dog', '1tnk', '2tnk', '3tnk', '4tnk', 'arty', 'jeep', 'apc', 'ftrk', 'v2rl', 'mgg', 'ttnk', 'mrj')
foreach ($name in $combat) {
	$kind = if ($actors.Contains($name)) { $actors[$name] } else { 'vehicle' }
	for ($i = 0; $i -lt 3; $i++) {
		$loc = Next-Location $kind
		$owner = if ($script:occasion % 2 -eq 0) { 'Multi0' } else { 'Multi1' }
		$extra = if ($script:occasion % 4 -eq 0) { 'Health: 60' } else { '' }
		Add-Actor $name $owner $loc[0] $loc[1] $extra
	}
}

# Extra harvesters and refineries, so docking and unloading run continuously on both sides.
foreach ($b in $bases) {
	for ($i = 0; $i -lt 3; $i++) {
		Add-Actor 'harv' $b.Owner ($b.X + 22 + 2 * $i) ($b.Y + 20)
	}

	Add-Actor 'proc' $b.Owner ($b.X + 24) ($b.Y + 16)
}

# Spawn points: one per playable player.
Add-Actor 'mpspawn' 'Neutral' 20 110
Add-Actor 'mpspawn' 'Neutral' 108 110

# --- map.yaml ----------------------------------------------------------------
$yaml = New-Object 'System.Collections.Generic.List[string]'
$yaml.Add('MapFormat: 12')
$yaml.Add('')
$yaml.Add('RequiresMod: ra')
$yaml.Add('')
$yaml.Add('Title: Snapshot Stress')
$yaml.Add('Author: OpenRA snapshot test harness')
$yaml.Add('Tileset: SNOW')
$yaml.Add('MapSize: ' + "$W,$H")
$yaml.Add('Bounds: ' + $Bounds)
$yaml.Add('')
$yaml.Add('Visibility: Lobby')
$yaml.Add('Categories: Conquest')
$yaml.Add('')
$yaml.Add('Players:')
$yaml.Add('	PlayerReference@Neutral:')
$yaml.Add('		Name: Neutral')
$yaml.Add('		OwnsWorld: True')
$yaml.Add('		NonCombatant: True')
$yaml.Add('		Faction: england')
$yaml.Add('	PlayerReference@Creeps:')
$yaml.Add('		Name: Creeps')
$yaml.Add('		NonCombatant: True')
$yaml.Add('		Faction: england')
$yaml.Add('		Enemies: Multi0, Multi1')
$yaml.Add('	PlayerReference@Multi0:')
$yaml.Add('		Name: Multi0')
$yaml.Add('		Playable: True')
$yaml.Add('		Faction: england')
$yaml.Add('		Enemies: Creeps')
$yaml.Add('	PlayerReference@Multi1:')
$yaml.Add('		Name: Multi1')
$yaml.Add('		Playable: True')
$yaml.Add('		Faction: russia')
$yaml.Add('		Bot: normal')
$yaml.Add('		Enemies: Creeps')
$yaml.Add('')
$yaml.Add('Actors:')
$yaml.AddRange($lines)
[System.IO.File]::WriteAllLines((Join-Path $outDir 'map.yaml'), $yaml)

"actors: $n"
"map.bin bytes: $((Get-Item (Join-Path $outDir 'map.bin')).Length)"
"kinds: " + (($actors.Values | Group-Object | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ')
