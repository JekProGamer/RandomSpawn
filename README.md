# RandomSpawn

CounterStrikeSharp plugin for manually placing spawn points per map and randomly respawning players at those points.

## Commands (admins only)
- `!spawnset` — save the administrator's current position and view rotation as a spawn point. It is saved immediately.
- `!spawnundo` — remove the last point on the current map.
- `!spawncount` — show the number of points on the current map.
- `!spawnclear` — remove all points on the current map.
- `!spawnsave` — save current map points.

The plugin registers CounterStrikeSharp commands as `css_spawnset`, etc.; CounterStrikeSharp exposes them as chat commands with the `!` prefix.

## Config
```json
{
  "Enabled": true,
  "MinPlayerDistance": 300.0,
  "AvoidLastSpawn": true,
  "MaxSpawnAttempts": 20,
  "AdminPermission": "@css/admin"
}
```

## Storage
Points are stored separately by map:
`addons/counterstrikesharp/plugins/RandomSpawn/spawns/<mapname>.json`

The plugin automatically creates the map file when it first loads and writes it after every add/remove/clear operation.

## Spawn selection
On each player spawn, the plugin chooses a random configured point that is at least `MinPlayerDistance` units from all other living players, if such a point exists. If none meets the threshold, it chooses the point with the greatest minimum distance to other players. It avoids the player's previous point when possible.

**Important placement note:** `!spawnset` saves the player's current pawn origin. Place yourself at a clear, grounded location before using it. The current implementation does not perform a full world-geometry/hull trace; manually verify that saved points are not inside walls, props, or off the playable floor.

## Build
Requires .NET 10 SDK:
```bash
dotnet restore
dotnet build -c Release
```

Copy `bin/Release/net10.0/RandomSpawn.dll` and the generated plugin config/folder to:
`game/csgo/addons/counterstrikesharp/plugins/RandomSpawn/`
