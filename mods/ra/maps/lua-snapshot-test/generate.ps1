# Regenerates this map: flat SNOW ground, two players, and a handful of named actors the test script
# refers to by name. Small on purpose, so a scripted world loads in a fraction of a second.
#
#   pwsh mods/ra/maps/lua-snapshot-test/generate.ps1
#   bin/OpenRA.Utility.exe ra --map refresh lua-snapshot-test   # regenerates the preview
#
# The actors are named rather than numbered because SpawnMapActors keys its table by the name in the
# Actors section, and MapGlobal exposes each of those as a Lua global. A test script reaches actors
# through those globals, which is why the names below are part of the test surface.
$ErrorActionPreference = 'Stop'
$outDir = $PSScriptRoot
$W = 48
$H = 40
$Bounds = "1,1,$($W - 2),$($H - 2)"

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# --- terrain -----------------------------------------------------------------
# Every cell is clear ground, so no actor placement has to reason about passability. The variant is a
# fixed function of the coordinates rather than a random draw, so regenerating gives identical bytes.
$tiles = New-Object 'byte[]' ($W * $H * 3)
$res = New-Object 'byte[]' ($W * $H * 2)
function Set-Tile([int]$x, [int]$y, [int]$tile, [int]$variant) {
	$i = ($x * $H + $y) * 3
	$t = [System.BitConverter]::GetBytes([uint16]$tile)
	$tiles[$i] = $t[0]; $tiles[$i + 1] = $t[1]; $tiles[$i + 2] = [byte]$variant
}
for ($x = 0; $x -lt $W; $x++) {
	for ($y = 0; $y -lt $H; $y++) {
		Set-Tile $x $y 255 (($x * 5 + $y * 3) % 20)
	}
}

$bin = New-Object 'System.Collections.Generic.List[byte]'
$bin.Add([byte]1)
$bin.AddRange([System.BitConverter]::GetBytes([uint16]$W))
$bin.AddRange([System.BitConverter]::GetBytes([uint16]$H))
$bin.AddRange($tiles)
$bin.AddRange($res)
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'map.bin'), $bin.ToArray())

# --- actors ------------------------------------------------------------------
$lines = New-Object 'System.Collections.Generic.List[string]'
$n = 0
function Add-Actor([string]$name, [string]$type, [string]$owner, [int]$x, [int]$y) {
	$script:n++
	$lines.Add("	$($name): $type")
	$lines.Add("		Owner: $owner")
	$lines.Add("		Location: $x,$y")
}

# Spawn points first, so the map is still startable from the map chooser.
Add-Actor 'Spawn0' 'mpspawn' 'Neutral' 8 20
Add-Actor 'Spawn1' 'mpspawn' 'Neutral' 40 20

# The script's subjects. TestWalker starts away from the footprint and walks into it; the guards and
# the blocker are the enemy's, so the script can kill them and watch its own state change.
Add-Actor 'TestWalker' 'e1' 'TestPlayer' 10 20
Add-Actor 'TestBlocker' 'e1' 'TestEnemy' 24 20
Add-Actor 'TestGuardA' 'e1' 'TestEnemy' 30 18
Add-Actor 'TestGuardB' 'e1' 'TestEnemy' 31 20
Add-Actor 'TestGuardC' 'e1' 'TestEnemy' 32 22

# A few decoration actors, so the world holds map actors that no script reaches for. Real maps are
# full of them, and they are the bulk of what RegisterMapActor puts into the globals table.
$decor = @(@(4, 4), @(6, 4), @(8, 4), @(4, 6), @(6, 6), @(8, 6), @(4, 8), @(6, 8))
for ($i = 0; $i -lt $decor.Count; $i++) {
	Add-Actor "Tree$i" 't01' 'Neutral' $decor[$i][0] $decor[$i][1]
}

# --- map.yaml ----------------------------------------------------------------
$yaml = New-Object 'System.Collections.Generic.List[string]'
$yaml.Add('MapFormat: 12')
$yaml.Add('')
$yaml.Add('RequiresMod: ra')
$yaml.Add('')
$yaml.Add('Title: Lua Snapshot Test')
$yaml.Add('Author: OpenRA Lua snapshot harness')
$yaml.Add('Tileset: SNOW')
$yaml.Add('MapSize: ' + "$W,$H")
$yaml.Add('Bounds: ' + $Bounds)
$yaml.Add('')
$yaml.Add('Visibility: Lobby')
$yaml.Add('Categories: Conquest')
$yaml.Add('')
# Without this line the map's rules.yaml is never read, and the map is not scripted at all.
$yaml.Add('Rules: rules.yaml')
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
$yaml.Add('		Enemies: TestPlayer, TestEnemy')
$yaml.Add('	PlayerReference@TestPlayer:')
$yaml.Add('		Name: TestPlayer')
$yaml.Add('		Playable: True')
$yaml.Add('		Faction: england')
$yaml.Add('		Enemies: Creeps')
$yaml.Add('	PlayerReference@TestEnemy:')
$yaml.Add('		Name: TestEnemy')
$yaml.Add('		Playable: True')
$yaml.Add('		Faction: russia')
$yaml.Add('		Enemies: Creeps')
$yaml.Add('')
$yaml.Add('Actors:')
$yaml.AddRange($lines)
[System.IO.File]::WriteAllLines((Join-Path $outDir 'map.yaml'), $yaml)

"actors: $n"
"map.bin bytes: $((Get-Item (Join-Path $outDir 'map.bin')).Length)"
