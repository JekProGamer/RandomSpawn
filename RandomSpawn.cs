using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace RandomSpawn;

public sealed class RandomSpawn : BasePlugin
{
    public override string ModuleName => "RandomSpawn";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "JekProGamer";
    public override string ModuleDescription => "Per-map manual spawn points with random respawning.";

    private const string AdminPermission = "@css/admin";
    private readonly Random _random = new();
    private SpawnConfig _config = new();
    private string CurrentMap => Server.MapName ?? string.Empty;
    private string SpawnDirectory => Path.Combine(ModuleDirectory, "spawns");
    private string CurrentMapFile => Path.Combine(SpawnDirectory, $"{SafeMapName(CurrentMap)}.json");

    private readonly Dictionary<ulong, int> _lastSpawnBySteamId = new();

    public override void Load(bool hotReload)
    {
        LoadConfig();

        AddCommand("css_spawnset", "Add a spawn point at your current position", CommandSpawnSet);
        AddCommand("css_spawnundo", "Remove the last spawn point on this map", CommandSpawnUndo);
        AddCommand("css_spawncount", "Show spawn point count for this map", CommandSpawnCount);
        AddCommand("css_spawnclear", "Remove all spawn points on this map", CommandSpawnClear);
        AddCommand("css_spawnsave", "Save spawn points for this map", CommandSpawnSave);

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        Logger.LogInformation("RandomSpawn v{Version} loaded. Map-specific spawn data will load when the server map is available.", ModuleVersion);
    }

    public override void Unload(bool hotReload)
    {
        DeregisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        _lastSpawnBySteamId.Clear();
    }

    private void LoadConfig()
    {
        Directory.CreateDirectory(ModuleDirectory);
        var path = Path.Combine(ModuleDirectory, "config.json");
        if (!File.Exists(path))
            File.WriteAllText(path, JsonSerializer.Serialize(_config, JsonOptions()));

        try
        {
            _config = JsonSerializer.Deserialize<SpawnConfig>(File.ReadAllText(path), JsonOptions()) ?? new SpawnConfig();
            _config.MinPlayerDistance = Math.Max(0, _config.MinPlayerDistance);
            _config.MaxSpawnAttempts = Math.Clamp(_config.MaxSpawnAttempts, 1, 100);
            _config.AdminPermission = string.IsNullOrWhiteSpace(_config.AdminPermission) ? AdminPermission : _config.AdminPermission;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load RandomSpawn config; defaults will be used.");
            _config = new SpawnConfig();
        }
    }

    private bool EnsureCurrentMapLoaded()
    {
        var mapName = CurrentMap;
        if (string.IsNullOrWhiteSpace(mapName))
            return false;

        if (string.Equals(_loadedMap, mapName, StringComparison.OrdinalIgnoreCase))
            return true;

        LoadMapSpawns();
        return string.Equals(_loadedMap, mapName, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadMapSpawns()
    {
        var mapName = CurrentMap;
        if (string.IsNullOrWhiteSpace(mapName))
        {
            Logger.LogWarning("Cannot load map spawns yet: Server.MapName is not available.");
            return;
        }

        Directory.CreateDirectory(SpawnDirectory);
        _loadedMap = mapName;
        if (!File.Exists(CurrentMapFile))
        {
            SaveMapSpawns();
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<MapSpawnData>(File.ReadAllText(CurrentMapFile), JsonOptions());
            _mapSpawns = data ?? new MapSpawnData();
            _mapSpawns.Spawns ??= new List<SpawnPoint>();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load spawn points for map {MapName}", CurrentMap);
            _mapSpawns = new MapSpawnData();
        }
    }

    private MapSpawnData _mapSpawns = new();

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private void SaveMapSpawns()
    {
        if (!EnsureCurrentMapLoaded())
            throw new InvalidOperationException("The current map name is not available; spawn points were not saved.");

        Directory.CreateDirectory(SpawnDirectory);
        File.WriteAllText(CurrentMapFile, JsonSerializer.Serialize(_mapSpawns, JsonOptions()));
    }

    private static string SafeMapName(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return "unknown_map";

        var safe = string.Concat(mapName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
        return string.IsNullOrWhiteSpace(safe) ? "unknown_map" : safe;
    }

    private bool IsAdmin(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid)
            return false;

        return AdminManager.PlayerHasPermissions(player, _config.AdminPermission);
    }

    private bool RequireAdmin(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid)
            return false;

        if (IsAdmin(player))
        {
            if (EnsureCurrentMapLoaded())
                return true;

            player.PrintToChat($"{ChatColors.Red}[RandomSpawn] {ChatColors.White}Название карты ещё не доступно. Попробуй команду через секунду.");
            return false;
        }

        player.PrintToChat($"{ChatColors.Red}[RandomSpawn] {ChatColors.White}У тебя нет доступа к этой команде.");
        return false;
    }

    private void CommandSpawnSet(CCSPlayerController? player, CommandInfo command)
    {
        if (!RequireAdmin(player))
            return;

        var pawn = player!.PlayerPawn.Value;
        var origin = pawn?.AbsOrigin;
        if (pawn == null || origin == null)
        {
            player.PrintToChat($"{ChatColors.Red}[RandomSpawn] {ChatColors.White}Не удалось получить координаты игрока.");
            return;
        }

        var rotation = pawn.AbsRotation;
        _mapSpawns.Spawns.Add(new SpawnPoint
        {
            X = origin.X,
            Y = origin.Y,
            Z = origin.Z,
            Pitch = rotation?.X ?? 0,
            Yaw = rotation?.Y ?? 0,
            Roll = rotation?.Z ?? 0
        });

        SaveMapSpawns();
        player.PrintToChat($"{ChatColors.Green}[RandomSpawn] {ChatColors.White}Точка спавна установлена! {ChatColors.Gold}#{_mapSpawns.Spawns.Count} {ChatColors.White}| Карта: {ChatColors.LightBlue}{CurrentMap}");
    }

    private void CommandSpawnUndo(CCSPlayerController? player, CommandInfo command)
    {
        if (!RequireAdmin(player)) return;
        if (_mapSpawns.Spawns.Count == 0)
        {
            player!.PrintToChat($"{ChatColors.Red}[RandomSpawn] {ChatColors.White}На этой карте нет точек.");
            return;
        }

        var index = _mapSpawns.Spawns.Count;
        _mapSpawns.Spawns.RemoveAt(index - 1);
        SaveMapSpawns();
        player!.PrintToChat($"{ChatColors.Green}[RandomSpawn] {ChatColors.White}Последняя точка удалена. Осталось: {ChatColors.Gold}{_mapSpawns.Spawns.Count}");
    }

    private void CommandSpawnCount(CCSPlayerController? player, CommandInfo command)
    {
        if (!RequireAdmin(player)) return;
        player!.PrintToChat($"{ChatColors.Green}[RandomSpawn] {ChatColors.White}Карта {ChatColors.LightBlue}{CurrentMap}{ChatColors.White}: точек спавна {ChatColors.Gold}{_mapSpawns.Spawns.Count}");
    }

    private void CommandSpawnClear(CCSPlayerController? player, CommandInfo command)
    {
        if (!RequireAdmin(player)) return;
        _mapSpawns.Spawns.Clear();
        _lastSpawnBySteamId.Clear();
        SaveMapSpawns();
        player!.PrintToChat($"{ChatColors.Green}[RandomSpawn] {ChatColors.White}Все точки текущей карты удалены.");
    }

    private void CommandSpawnSave(CCSPlayerController? player, CommandInfo command)
    {
        if (!RequireAdmin(player)) return;
        SaveMapSpawns();
        player!.PrintToChat($"{ChatColors.Green}[RandomSpawn] {ChatColors.White}Точки карты сохранены. Всего: {ChatColors.Gold}{_mapSpawns.Spawns.Count}");
    }

    private string _loadedMap = string.Empty;

    private HookResult OnPlayerSpawn(EventPlayerSpawn gameEvent, GameEventInfo info)
    {
        // Load the correct map file only after the engine has initialized Server.MapName.
        if (!EnsureCurrentMapLoaded())
            return HookResult.Continue;

        var player = gameEvent.Userid;
        if (!_config.Enabled || player == null || !player.IsValid || _mapSpawns.Spawns.Count == 0)
            return HookResult.Continue;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return HookResult.Continue;

        var chosenIndex = ChooseSpawn(player);
        if (chosenIndex < 0)
            return HookResult.Continue;

        var point = _mapSpawns.Spawns[chosenIndex];
        _lastSpawnBySteamId[player.SteamID] = chosenIndex;

        Server.NextFrame(() =>
        {
            if (!player.IsValid || player.PlayerPawn.Value == null || !player.PlayerPawn.Value.IsValid)
                return;

            player.PlayerPawn.Value.Teleport(
                new Vector(point.X, point.Y, point.Z),
                new QAngle(point.Pitch, point.Yaw, point.Roll),
                new Vector(0, 0, 0));
        });

        return HookResult.Continue;
    }

    private int ChooseSpawn(CCSPlayerController player)
    {
        var points = _mapSpawns.Spawns;
        if (points.Count == 1) return 0;

        var occupied = Utilities.GetPlayers()
            .Where(p => p.IsValid && p.PlayerPawn.Value?.AbsOrigin != null && p.SteamID != player.SteamID)
            .Select(p => p.PlayerPawn.Value!.AbsOrigin!)
            .ToList();

        var candidates = Enumerable.Range(0, points.Count).ToList();
        if (_config.AvoidLastSpawn && _lastSpawnBySteamId.TryGetValue(player.SteamID, out var last) && candidates.Count > 1)
            candidates.Remove(last);

        var safe = candidates
            .Select(i => new { Index = i, MinDistance = MinimumDistance(points[i], occupied) })
            .Where(x => x.MinDistance >= _config.MinPlayerDistance)
            .ToList();

        if (safe.Count > 0)
            return safe[_random.Next(safe.Count)].Index;

        // If every point is closer than the configured threshold, use the point
        // with the greatest distance from other players (best available fallback).
        var fallback = candidates
            .Select(i => new { Index = i, MinDistance = MinimumDistance(points[i], occupied) })
            .OrderByDescending(x => x.MinDistance)
            .ToList();

        return fallback.Count > 0 ? fallback[0].Index : _random.Next(points.Count);
    }

    private static float MinimumDistance(SpawnPoint point, List<Vector> occupied)
    {
        if (occupied.Count == 0) return float.MaxValue;

        var min = float.MaxValue;
        foreach (var position in occupied)
        {
            var dx = point.X - position.X;
            var dy = point.Y - position.Y;
            var dz = point.Z - position.Z;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance < min) min = distance;
        }

        return min;
    }

    private sealed class SpawnConfig
    {
        public bool Enabled { get; set; } = true;
        public float MinPlayerDistance { get; set; } = 300.0f;
        public bool AvoidLastSpawn { get; set; } = true;
        public int MaxSpawnAttempts { get; set; } = 20;
        public string AdminPermission { get; set; } = "@css/admin";
    }

    private sealed class MapSpawnData
    {
        public List<SpawnPoint> Spawns { get; set; } = new();
    }

    private sealed class SpawnPoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Pitch { get; set; }
        public float Yaw { get; set; }
        public float Roll { get; set; }
    }
}
