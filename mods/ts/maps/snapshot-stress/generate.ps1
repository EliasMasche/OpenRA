# Regenerates this map: one of every placeable ts actor, alternating sides, a self-playing bot,
# tiberium beside each refinery, and a water bay.
#
#   pwsh mods/ts/maps/snapshot-stress/generate.ps1
#   bin/OpenRA.Utility.exe ts --map refresh snapshot-stress   # regenerates the preview and map.yaml
#
# The terrain is flat TEMPERATE clear ground (template 1017) with a water bay (template 1001) and
# tiberium (resource index 1), a little blue tiberium (2) and a vein patch (3). ts keeps its bridge
# spans in rules/bridges.yaml, which is excluded wholesale. This is the ts copy of
# mods/ra/maps/snapshot-stress/generate.ps1; the only differences are the parameter block below.
$ErrorActionPreference = 'Stop'
$outDir = $PSScriptRoot
$repo = Split-Path (Split-Path (Split-Path (Split-Path $outDir -Parent) -Parent) -Parent) -Parent

# --- per-mod parameters ------------------------------------------------------------------------
$mod = 'ts'
$tilesetFile = 'temperate'  # mods/<mod>/tilesets/<file>.yaml
$tilesetName = 'TEMPERATE'  # what map.yaml declares
$mapW = 160
$mapH = 160

# Terrain templates. Every one is checked against the tileset below: it must be a 1x1 template whose
# frames are *all* the named terrain type. That check is also what catches the `Size: 1, 1`
# whitespace difference the ts tilesets use, which a literal '1,1' comparison silently misses.
$groundTile = 1017; $groundTerrain = 'Clear'; $groundVariants = 1
$waterTile = 1001; $waterTerrain = 'Water'
# The resource layer's AllowedTerrainTypes decides which tile tiberium may sit on. On ts that is
# Clear, Rough or DirtRoad, so the ore cells reuse the plain ground tile.
$oreGroundTile = $groundTile; $oreGroundTerrain = 'Clear'; $oreGroundVariants = 1
$oreResource = 1      # Tiberium
$oreDensity = 12
$gemResource = 2      # BlueTiberium
$gemDensity = 12
$veinsResource = 3    # Veins
$veinsDensity = 2
$hasWater = $true

# ts is the only one of the four mods whose MapGrid is RectangularIsometric (ra, cnc and d2k are
# Rectangular). map.bin is indexed by *map* coordinates (u, v), so the layout below is laid out in
# map coordinates on every mod; on ts each placement is then converted to the cell an actor stands
# on, which is MPos.ToCPos:
#   y = (v - (v & 1)) / 2 - u,  x = v - y
# Placing a ts actor at the (u, v) itself puts it outside the map's diamond, where the cell has no
# projection at all -- which shows up much later, as a frozen actor with no footprint.
$isometric = $true

$factions = @('gdi', 'nod')
$botType = 'test'

# kind -> actors, in mod.yaml Rules order. System actors, editor markers, bridges and everything
# that is not a placeable actor are handled separately. Files not listed here (ai, player, world,
# palettes, defaults, husks, bridges, map-generators) hold no placeable actors. ts splits its rules
# per faction, which is why the gdi-*/nod-*/shared-* files are listed individually.
$plan = [ordered]@{
	'aircraft.yaml'             = 'aircraft'
	'civilian-infantry.yaml'    = 'infantry'
	'critters.yaml'             = 'infantry'
	'civilian-structures.yaml'  = 'building'
	'civilian-vehicles.yaml'    = 'vehicle'
	'gdi-infantry.yaml'         = 'infantry'
	'gdi-structures.yaml'       = 'building'
	'gdi-support.yaml'          = 'building'
	'gdi-vehicles.yaml'         = 'vehicle'
	'nod-infantry.yaml'         = 'infantry'
	'nod-structures.yaml'       = 'building'
	'nod-support.yaml'          = 'building'
	'nod-vehicles.yaml'         = 'vehicle'
	'shared-infantry.yaml'      = 'infantry'
	'shared-structures.yaml'    = 'building'
	'shared-support.yaml'       = 'building'
	'shared-vehicles.yaml'      = 'vehicle'
	'trees.yaml'                = 'decoration'
	'misc.yaml'                 = 'misc'
}

$kindOverrides = @{}

$skip = @('mpspawn', 'waypoint', 'world', 'player', 'editorplayer', 'editorworld')

# Bridge spans and huts are terrain-construction actors: the map editor builds them as a matched
# chain over a crossing, and a span placed on its own has no footprint to update when it is damaged.
$skip += @('bridge1', 'bridge2', 'bridge3', 'bridge4', 'bridgehut', 'cabhut', 'lobrdg_a', 'lobrdg_b',
	'lobrdg_r_se', 'lobrdg_r_nw', 'lobrdg_r_ne', 'lobrdg_r_sw', 'railbrdg1', 'railbrdg2')

# Starter base, as (actor, dx, dy) from the construction yard. Validated against the actor set below.
$basePlan = @(
	@('gacnst', 0, 0), @('anypower', 8, 0), @('anypower', 12, 0), @('proc', 4, 4), @('barracks', 12, 4),
	@('factory', 16, 4), @('radar', 20, 4), @('gadept', 4, 10), @('gahpad', 10, 10),
	@('gactwr', 20, 10), @('garock', 18, 12), @('gacsam', 22, 12), @('gavulc', 24, 10),
	@('harv', 16, 22), @('harv', 18, 24), @('harv', 20, 22), @('mcv', 6, 18)
)
# A neutral actor placed beside each base, if the mod has one (ra's `mine`).
$baseNeutral = ''

# Combat units, so the two sides actually fight rather than standing still.
$combat = @('e1', 'e2', 'e3', 'e1r3', 'engineer', 'medic', 'jumpjet', 'cyborg', 'cyc2', 'mhijack',
	'flameguy', 'apc', 'hvr', 'smech', 'mmch', 'hmec', 'sonic', 'jugg', 'bggy', 'bike', 'ttnk',
	'art2', 'repair', 'weed', 'sapc', 'subtank', 'stnk')

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

	# And a vein patch, which is the third ts resource.
	if ($veinsResource -ne 0) {
		for ($x = $ox + (SX 12); $x -lt [Math]::Min($ox + (SX 17), $W - 1); $x++) {
			for ($y = $oreTop; $y -lt $oreTop + (SY 5); $y++) {
				if ($oreGroundTile -ne $groundTile) { Set-Tile $x $y $oreGroundTile 0 }
				Set-Ore $x $y $veinsResource $veinsDensity
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
	# The layout is in map coordinates; the actor's Location is a cell.
	$cellX = $x
	$cellY = $y
	if ($isometric) {
		$cellY = [int](($y - ($y -band 1)) / 2) - $x
		$cellX = $y - $cellY
	}

	$script:n++
	$lines.Add("`tActor$($script:n): $($name.ToLowerInvariant())")
	$lines.Add("`t`tOwner: $owner")
	$lines.Add("`t`tLocation: $cellX,$cellY")
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
		Add-Actor 'harv' $b.Owner ($b.X + 22 + 2 * $i) ($oreTop + 2)
	}

	Add-Actor 'proc' $b.Owner ($b.X + 24) ($b.Y + 16)
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
