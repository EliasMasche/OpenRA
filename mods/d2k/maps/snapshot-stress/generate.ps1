# Regenerates this map: one of every placeable d2k actor, alternating sides, a self-playing bot and
# spice beside each refinery.
#
#   pwsh mods/d2k/maps/snapshot-stress/generate.ps1
#   bin/OpenRA.Utility.exe d2k --map refresh snapshot-stress   # regenerates the preview and map.yaml
#
# The terrain is flat ARRAKIS sand (template 60) with spice (resource index 1) on the SpiceSand
# template (0) the resource layer allows. d2k has no 1x1 water template and no naval actors, so
# there is no water bay. This is the d2k copy of mods/ra/maps/snapshot-stress/generate.ps1; the only
# differences are the parameter block below.
$ErrorActionPreference = 'Stop'
$outDir = $PSScriptRoot
$repo = Split-Path (Split-Path (Split-Path (Split-Path $outDir -Parent) -Parent) -Parent) -Parent

# --- per-mod parameters ------------------------------------------------------------------------
$mod = 'd2k'
$tilesetFile = 'arrakis'    # mods/<mod>/tilesets/<file>.yaml
$tilesetName = 'ARRAKIS'    # what map.yaml declares
$mapW = 140
$mapH = 140

# Terrain templates. Every one is checked against the tileset below: it must be a 1x1 template whose
# frames are *all* the named terrain type. That check is also what catches the `Size: 1, 1`
# whitespace difference the ts tilesets use, which a literal '1,1' comparison silently misses.
$groundTile = 60; $groundTerrain = 'Sand'; $groundVariants = 1
$waterTile = 0; $waterTerrain = 'Water'
# The resource layer's AllowedTerrainTypes decides which tile spice may sit on. On d2k that is
# SpiceSand, which the plain sand ground is not, so the spice cells are re-tiled.
$oreGroundTile = 0; $oreGroundTerrain = 'SpiceSand'; $oreGroundVariants = 13
$oreResource = 1      # Spice
$oreDensity = 12
$gemResource = 0      # d2k has only one resource type
$gemDensity = 0
$hasWater = $false

$factions = @('atreides', 'harkonnen')
$botType = 'omnius'

# kind -> actors, in mod.yaml Rules order. System actors, editor markers and bridges are handled
# separately. Files not listed here (ai, player, world, palettes, defaults, husks, starport,
# map-generators) hold no placeable actors.
$plan = [ordered]@{
	'infantry.yaml'         = 'infantry'
	'vehicles.yaml'         = 'vehicle'
	'aircraft.yaml'         = 'aircraft'
	'structures.yaml'       = 'building'
	'arrakis.yaml'          = 'decoration'
	'misc.yaml'             = 'misc'
}

# Single actors whose file name gives the wrong kind: the sandworm is a critter that moves, the
# sietch is a capturable structure, and the spice bloom is a resource-producing neutral actor.
$kindOverrides = @{
	'sandworm' = 'infantry'
	'sietch' = 'building'
	'spicebloom' = 'misc'
}

$skip = @('mpspawn', 'waypoint', 'world', 'player', 'editorplayer', 'editorworld')

# Bridge spans and huts are terrain-construction actors: the map editor builds them as a matched
# chain over a crossing, and a span placed on its own has no footprint to update when it is damaged.
$skip += @('bridge1', 'bridge2', 'bridge3', 'bridge4', 'bridgehut')

# Starter base, as (actor, dx, dy) from the construction yard. Validated against the actor set below.
$basePlan = @(
	@('construction_yard', 0, 0), @('wind_trap', 6, 0), @('wind_trap', 10, 0), @('refinery', 4, 4),
	@('barracks', 10, 4), @('light_factory', 14, 4), @('heavy_factory', 18, 4), @('outpost', 22, 4),
	@('repair_pad', 4, 10), @('starport', 10, 10), @('medium_gun_turret', 20, 10),
	@('large_gun_turret', 24, 10), @('wall', 22, 12), @('harvester', 16, 22),
	@('harvester', 18, 24), @('harvester', 20, 22), @('mcv', 6, 18)
)
# A neutral actor placed beside each base, if the mod has one (ra's `mine`).
$baseNeutral = ''

# Combat units, so the two sides actually fight rather than standing still.
$combat = @('light_inf', 'trooper', 'fremen', 'grenadier', 'sardaukar', 'trike', 'quad',
	'siege_tank', 'missile_tank', 'sonic_tank', 'devastator', 'raider', 'stealth_raider',
	'deviator', 'combat_tank_a', 'harvester')

# --- tileset ----------------------------------------------------------------------------------
function Read-Tileset([string]$path) {
	$tmpls = @{}
	$cur = $null; $size = $null; $tiles = @{}
	foreach ($line in Get-Content $path) {
		if ($line -match '^\tTemplate@(\d+):') {
			if ($null -ne $cur) { $tmpls[[int]$cur] = @{ Size = $size; Tiles = $tiles } }
			$cur = $Matches[1]; $size = $null; $tiles = @{}
			continue
		}

		if ($null -ne $cur) {
			if ($line -match '^\t\tSize:\s*(.+?)\s*$') { $size = $Matches[1] }
			elseif ($line -match '^\t+(\d+):\s*(\w+)\s*$') { $tiles[[int]$Matches[1]] = $Matches[2] }
		}
	}

	if ($null -ne $cur) { $tmpls[[int]$cur] = @{ Size = $size; Tiles = $tiles } }
	return $tmpls
}

function Assert-Tile($tileset, [int]$id, [string]$terrain, [int]$minFrames, [string]$what) {
	if (-not $tileset.ContainsKey($id)) { throw "$what template $id is not in the tileset" }
	$t = $tileset[$id]
	# Compare with whitespace stripped: ra/cnc/d2k write `Size: 1,1`, ts writes `Size: 1, 1`.
	if (($t.Size -replace '\s', '') -ne '1,1') { throw "$what template $id is not 1x1 (Size: $($t.Size))" }
	$types = @($t.Tiles.Values | Select-Object -Unique)
	if ($types.Count -ne 1) { throw "$what template $id is not uniform: $($types -join '/')" }
	if ($types[0] -ne $terrain) { throw "$what template $id is $($types[0]), expected $terrain" }
	if ($t.Tiles.Count -lt $minFrames) { throw "$what template $id has $($t.Tiles.Count) frames, needs $minFrames" }
}

$tileset = Read-Tileset (Join-Path $repo "mods\$mod\tilesets\$tilesetFile.yaml")
Assert-Tile $tileset $groundTile $groundTerrain $groundVariants 'ground'
Assert-Tile $tileset $oreGroundTile $oreGroundTerrain $oreGroundVariants 'ore ground'
if ($hasWater) { Assert-Tile $tileset $waterTile $waterTerrain 1 'water' }

# --- actors -----------------------------------------------------------------------------------
function Get-ActorKeys($file) {
	$names = @()
	foreach ($line in Get-Content (Join-Path $repo "mods\$mod\rules\$file")) {
		if ($line -match '^([A-Za-z0-9_]+):\s*$') { $names += $Matches[1] }
	}

	return $names
}

# Some actors declare `RequiresSpecificOwners: ValidOwnerNames: ...`, which may sit on the actor
# itself or on any ^template it inherits. Resolve the chain so the map never gives an actor an owner
# the `--check-yaml` owner lint rejects.
function Read-RuleDatabase() {
	$db = @{}
	foreach ($file in Get-ChildItem (Join-Path $repo "mods\$mod\rules\*.yaml")) {
		$key = $null
		foreach ($line in Get-Content $file.FullName) {
			if ($line -match '^([A-Za-z0-9_^]+):\s*$') {
				$key = $Matches[1]
				if (-not $db.ContainsKey($key)) { $db[$key] = @{ Inherits = @(); Owners = $null } }
				continue
			}

			if ($null -eq $key) { continue }
			if ($line -match '^\t+Inherits:\s*(.+?)\s*$') {
				$db[$key].Inherits += @($Matches[1] -split ',' | ForEach-Object { $_.Trim().TrimStart('-') } | Where-Object { $_ })
			}
			elseif ($line -match '^\t+ValidOwnerNames:\s*(.+?)\s*$') {
				$db[$key].Owners = @($Matches[1] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
			}
		}
	}

	return $db
}

$ruleDb = Read-RuleDatabase
$ownerCache = @{}
function Get-ValidOwners([string]$key) {
	if ($ownerCache.ContainsKey($key)) { return $ownerCache[$key] }
	$ownerCache[$key] = @()
	if (-not $ruleDb.ContainsKey($key)) { return @() }
	$rule = $ruleDb[$key]
	if ($null -ne $rule.Owners) { $ownerCache[$key] = $rule.Owners; return $rule.Owners }
	$found = @()
	foreach ($parent in $rule.Inherits) {
		$owners = @(Get-ValidOwners $parent)
		if ($owners.Count -gt 0) { $found = $owners }
	}

	$ownerCache[$key] = $found
	return $found
}

$actors = [ordered]@{}
$ruleKey = @{}
foreach ($kv in $plan.GetEnumerator()) {
	foreach ($name in (Get-ActorKeys $kv.Key)) {
		if ($name.StartsWith('^')) { continue }
		$lower = $name.ToLowerInvariant()
		if ($skip -contains $lower) { continue }
		if (-not $actors.Contains($lower)) { $actors[$lower] = $kv.Value; $ruleKey[$lower] = $name }
	}
}

foreach ($name in $kindOverrides.Keys) {
	if ($actors.Contains($name)) { $actors[$name] = $kindOverrides[$name] }
}

foreach ($entry in $basePlan) {
	if (-not $actors.Contains($entry[0])) { throw "starter base actor '$($entry[0])' is not a placeable $mod actor" }
}

foreach ($name in $combat) {
	if (-not $actors.Contains($name)) { throw "combat actor '$name' is not a placeable $mod actor" }
}

# --- terrain ----------------------------------------------------------------------------------
$W = $mapW
$H = $mapH
$sx = $W / 140.0
$sy = $H / 140.0
function SX([double]$v) { return [int][Math]::Round($v * $script:sx) }
function SY([double]$v) { return [int][Math]::Round($v * $script:sy) }

$waterTop = SY 126
$oreTop = SY 114
$oreBottom = $waterTop - 1

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
		if ($hasWater -and $y -ge $waterTop) { Set-Tile $x $y $waterTile 0 }
		else { Set-Tile $x $y $groundTile (($x * 7 + $y * 3) % $groundVariants) }
	}
}

# Resource fields, one per side, directly below that side's base so the harvesters find them.
$oreWidth = SX 30
foreach ($b in @(@{ X = SX 6; Y = SY 94 }, @{ X = SX 104; Y = SY 94 })) {
	$ox = [Math]::Max(1, $b.X)
	for ($x = $ox; $x -lt [Math]::Min($ox + $oreWidth, $W - 1); $x++) {
		for ($y = $oreTop; $y -le $oreBottom; $y++) {
			if (($x + $y) % 7 -eq 3) { continue }
			if ($oreGroundTile -ne $groundTile) { Set-Tile $x $y $oreGroundTile (($x + $y) % $oreGroundVariants) }
			Set-Ore $x $y $oreResource $oreDensity
		}
	}

	# A few cells of the second resource, so its path is exercised too.
	if ($gemResource -ne 0) {
		for ($x = $ox + (SX 4); $x -lt [Math]::Min($ox + (SX 9), $W - 1); $x++) {
			for ($y = $oreTop + (SY 6); $y -lt $oreBottom; $y++) {
				if ($oreGroundTile -ne $groundTile) { Set-Tile $x $y $oreGroundTile 0 }
				Set-Ore $x $y $gemResource $gemDensity
			}
		}
	}
}

$bin = New-Object 'System.Collections.Generic.List[byte]'
$bin.Add([byte]1)
$bin.AddRange([System.BitConverter]::GetBytes([uint16]$W))
$bin.AddRange([System.BitConverter]::GetBytes([uint16]$H))
$bin.AddRange($tiles)
$bin.AddRange($res)
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'map.bin'), $bin.ToArray())

# --- actor placement --------------------------------------------------------------------------
$lines = New-Object 'System.Collections.Generic.List[string]'
$n = 0
function Add-Actor([string]$name, [string]$owner, [int]$x, [int]$y, [string]$extra = '') {
	$script:n++
	$lines.Add("`tActor$($script:n): $($name.ToLowerInvariant())")
	$lines.Add("`t`tOwner: $owner")
	$lines.Add("`t`tLocation: $x,$y")
	if ($extra) { $lines.Add("`t`t$extra") }
}

$occasion = 0
function Next-Location([string]$kind, [int]$index) {
	# Coarse grids, one per kind, chosen so nothing overlaps the starter bases or the water. The
	# constants are ra's 140x140 layout scaled to this map.
	$script:occasion++
	$cap = $script:capacity[$kind]
	$i = $index % $cap
	switch ($kind) {
		'building' {
			$col = $i % 20; $row = [int]($i / 20)
			return @(((SX 6) + $col * 6), ((SY 40) + $row * 6))
		}
		'vehicle' {
			$col = $i % 26; $row = [int]($i / 26)
			$x = if ($row % 2 -eq 0) { (SX 6) + $col * 2 } else { (SX 56) - $col * 2 }
			return @($x, ((SY 20) + $row * 2))
		}
		'infantry' {
			$col = $i % 26; $row = [int]($i / 26)
			$x = if ($row % 2 -eq 0) { (SX 74) + $col * 2 } else { (SX 124) - $col * 2 }
			return @($x, ((SY 20) + $row * 2))
		}
		'aircraft' {
			$x = if ($i % 2 -eq 0) { (SX 10) + $i * 2 } else { (SX 130) - $i * 2 }
			return @($x, (SY 92))
		}
		'ship' {
			return @(((SX 8) + $i * 3), $script:shipY)
		}
		'civilian' {
			$col = $i % 30; $row = [int]($i / 30)
			return @(((SX 8) + $col * 4), ((SY 8) + $row * 4))
		}
		'decoration' {
			$col = $i % 40; $row = [int]($i / 40)
			return @(((SX 6) + $col * 3), ((SY 66) + $row * 3))
		}
		'misc' {
			$col = $i % 20; $row = [int]($i / 20)
			return @(((SX 10) + $col * 6), ((SY 96) + $row * 4))
		}
	}

	throw "no placement rule for $kind"
}

# Grid capacity per kind, so a mod with more of one kind than ra still gives every actor its own slot.
$capacity = @{}
foreach ($kind in @('building', 'vehicle', 'infantry', 'aircraft', 'ship', 'civilian', 'decoration', 'misc')) {
	$capacity[$kind] = 0
}

foreach ($kind in $actors.Values) { $capacity[$kind]++ }
$capacity['building'] = [Math]::Max($capacity['building'], 120)
$capacity['vehicle'] = [Math]::Max($capacity['vehicle'], 200)
$capacity['infantry'] = [Math]::Max($capacity['infantry'], 200)
$capacity['aircraft'] = [Math]::Max($capacity['aircraft'], 40)
$capacity['ship'] = [Math]::Max($capacity['ship'], 40)
$capacity['civilian'] = [Math]::Max($capacity['civilian'], 60)
$capacity['decoration'] = [Math]::Max($capacity['decoration'], 120)
$capacity['misc'] = [Math]::Max($capacity['misc'], 60)

$shipY = $waterTop + 5
$kindIndex = @{}
foreach ($kind in $capacity.Keys) { $kindIndex[$kind] = 0 }

# Starter bases, placed first so the bulk grid cannot land on them.
$bases = @(
	@{ Owner = 'Multi0'; X = SX 6; Y = SY 94 },
	@{ Owner = 'Multi1'; X = SX 104; Y = SY 94 }
)
foreach ($b in $bases) {
	foreach ($entry in $basePlan) {
		Add-Actor $entry[0] $b.Owner ($b.X + $entry[1]) ($b.Y + $entry[2])
	}

	if ($baseNeutral) { Add-Actor $baseNeutral 'Neutral' ($b.X + 26) ($b.Y + 22) }
}

# One of every actor in the mod's rule files, alternating sides so both players own each type.
foreach ($name in $actors.Keys) {
	$kind = $actors[$name]
	$loc = Next-Location $kind $kindIndex[$kind]
	$kindIndex[$kind]++
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

	# An actor with RequiresSpecificOwners may only be owned by one of the names it lists.
	$validOwners = @(Get-ValidOwners $ruleKey[$name])
	if ($validOwners.Count -gt 0 -and $validOwners -notcontains $owner) { $owner = $validOwners[0] }

	$extra = ''
	# A few damaged units, so repair and damage-state paths have state to keep.
	if ($kind -in @('vehicle', 'infantry') -and $script:occasion % 5 -eq 0) { $extra = 'Health: 45' }
	Add-Actor $name $owner $loc[0] $loc[1] $extra
}

# Extra combat units, so the two sides actually fight.
foreach ($name in $combat) {
	$kind = if ($actors.Contains($name)) { $actors[$name] } else { 'vehicle' }
	for ($i = 0; $i -lt 3; $i++) {
		$loc = Next-Location $kind $kindIndex[$kind]
		$kindIndex[$kind]++
		$owner = if ($script:occasion % 2 -eq 0) { 'Multi0' } else { 'Multi1' }
		$validOwners = Get-ValidOwners $ruleKey[$name]
		if ($null -ne $validOwners -and $validOwners -notcontains $owner) { $owner = $validOwners[0] }
		$extra = if ($script:occasion % 4 -eq 0) { 'Health: 60' } else { '' }
		Add-Actor $name $owner $loc[0] $loc[1] $extra
	}
}

# Extra harvesters and refineries, so docking and unloading run continuously on both sides.
foreach ($b in $bases) {
	for ($i = 0; $i -lt 3; $i++) {
		Add-Actor 'harvester' $b.Owner ($b.X + 22 + 2 * $i) ($oreTop + 2)
	}

	Add-Actor 'refinery' $b.Owner ($b.X + 24) ($b.Y + 16)
}

# Spawn points: one per playable player.
Add-Actor 'mpspawn' 'Neutral' (SX 20) (SY 110)
Add-Actor 'mpspawn' 'Neutral' (SX 108) (SY 110)

# --- map.yaml ---------------------------------------------------------------------------------
$yaml = New-Object 'System.Collections.Generic.List[string]'
$yaml.Add('MapFormat: 12')
$yaml.Add('')
$yaml.Add("RequiresMod: $mod")
$yaml.Add('')
$yaml.Add('Title: Snapshot Stress')
$yaml.Add('Author: OpenRA snapshot test harness')
$yaml.Add("Tileset: $tilesetName")
$yaml.Add("MapSize: $W,$H")
$yaml.Add("Bounds: 1,1,$($W - 2),$($H - 2)")
$yaml.Add('')
$yaml.Add('Visibility: Lobby')
$yaml.Add('Categories: Conquest')
$yaml.Add('')
$yaml.Add('Players:')
$yaml.Add("`tPlayerReference@Neutral:")
$yaml.Add("`t`tName: Neutral")
$yaml.Add("`t`tOwnsWorld: True")
$yaml.Add("`t`tNonCombatant: True")
$yaml.Add("`t`tFaction: $($factions[0])")
$yaml.Add("`tPlayerReference@Creeps:")
$yaml.Add("`t`tName: Creeps")
$yaml.Add("`t`tNonCombatant: True")
$yaml.Add("`t`tFaction: $($factions[0])")
$yaml.Add("`t`tEnemies: Multi0, Multi1")
$yaml.Add("`tPlayerReference@Multi0:")
$yaml.Add("`t`tName: Multi0")
$yaml.Add("`t`tPlayable: True")
$yaml.Add("`t`tFaction: $($factions[0])")
$yaml.Add("`t`tEnemies: Creeps")
$yaml.Add("`tPlayerReference@Multi1:")
$yaml.Add("`t`tName: Multi1")
$yaml.Add("`t`tPlayable: True")
$yaml.Add("`t`tFaction: $($factions[1])")
$yaml.Add("`t`tBot: $botType")
$yaml.Add("`t`tEnemies: Creeps")
$yaml.Add('')
$yaml.Add('Actors:')
$yaml.AddRange($lines)
[System.IO.File]::WriteAllLines((Join-Path $outDir 'map.yaml'), $yaml)

"actors: $n"
"map.bin bytes: $((Get-Item (Join-Path $outDir 'map.bin')).Length)"
"kinds: " + (($actors.Values | Group-Object | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ')
