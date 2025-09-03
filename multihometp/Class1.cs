using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Config;

public class TeleportMod : ModSystem
{
    private ICoreServerAPI sapi;

    private Dictionary<string, Dictionary<string, Vec3d>> playerHomes = new Dictionary<string, Dictionary<string, Vec3d>>();
    private Dictionary<string, DateTime> lastHomeUse = new Dictionary<string, DateTime>();
    private Dictionary<string, DateTime> lastBackUse = new Dictionary<string, DateTime>();
    private Dictionary<string, Vec3d> previousPositions = new Dictionary<string, Vec3d>();

    private Dictionary<string, DateTime> lastSpawnUse = new Dictionary<string, DateTime>();

    private double homeCooldownSeconds;
    private double backCooldownSeconds;
    private double spawnCooldownSeconds;

    private bool singleMode; // true = csak egy "default" home engedélyezett

 
    private bool spawnCommandsEnabled;
    private bool backEnabled;

    private const string ConfigFileName = "MHT_config.json";
    private const string SaveFileName = "MHT_playerHomes.json";
    private const string PreviousPositionsFileName = "MHT_previousPositions.json";
    private const string LogFileName = "MHT_teleportmod.log";

    private string ConfigFilePath;
    private string SaveFilePath;
    private string PreviousPositionsFilePath;
    private string LogFilePath;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        ConfigFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), ConfigFileName);
        SaveFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), SaveFileName);
        PreviousPositionsFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), PreviousPositionsFileName);
        LogFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), LogFileName);

        LoadConfig();
        LoadHomes();
        LoadPreviousPositions();

        var commands = api.ChatCommands;
        var parsers = commands.Parsers;

        // --- Homes ---
        commands.Create("sethome")
            .WithDescription("Sets a home location. Usage: /sethome [name]")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(parsers.OptionalWord("name"))
            .HandleWith(SetHomeCommand);

        commands.Create("home")
            .WithDescription($"Teleports you to a home. Usage: /home [name] (Cooldown: {homeCooldownSeconds / 60} min)")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(parsers.OptionalWord("name"))
            .HandleWith(HomeCommand);

        if (backEnabled)
        {
            commands.Create("back")
                .WithDescription($"Teleports you to your last location (Cooldown: {backCooldownSeconds / 60} min)")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(BackCommand);
        }

        commands.Create("listhomes")
            .WithDescription("Lists your saved home names")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(ListHomesCommand);

        commands.Create("delhome")
            .WithDescription("Deletes a saved home. Usage: /delhome <name>")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(parsers.Word("name"))
            .HandleWith(DeleteHomeCommand);

        commands.Create("homeinfo")
            .WithDescription("Shows coordinates for all your saved homes")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(HomeInfoCommand);

        commands.Create("renamehome")
            .WithDescription("Renames a saved home. Usage: /renamehome <oldName> <newName>")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(parsers.Word("oldName"), parsers.Word("newName"))
            .HandleWith(RenameHomeCommand);

        
        if (spawnCommandsEnabled)
        {
            RegisterTpToSpawnCommand("tospawn");
            RegisterTpToSpawnCommand("tpspawn");
            RegisterTpToSpawnCommand("tptospawn");
        }
    }

    private void RegisterTpToSpawnCommand(string cmd)
    {
        string desc = spawnCooldownSeconds > 0
            ? $"Teleport to the world's default spawn point (Cooldown: {spawnCooldownSeconds / 60} min)"
            : "Teleport to the world's default spawn point";

        sapi.ChatCommands.Create(cmd)
            .WithDescription(desc)
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(TpToSpawnCommand);
    }

    private TextCommandResult TpToSpawnCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        
        DateTime now = DateTime.UtcNow;
        if (spawnCooldownSeconds > 0 && lastSpawnUse.TryGetValue(player.PlayerUID, out var last))
        {
            double elapsed = (now - last).TotalSeconds;
            if (elapsed < spawnCooldownSeconds)
            {
                double remain = spawnCooldownSeconds - elapsed;
                double mins = Math.Floor(remain / 60);
                double secs = remain % 60;
                string msg = mins > 0 ? $"{mins} min {Math.Ceiling(secs)} sec" : $"{Math.Ceiling(secs)} sec";
                return TextCommandResult.Error($"You must wait {msg} before using this command again.");
            }
        }

        var epos = sapi?.World?.DefaultSpawnPosition;
        if (epos == null) return TextCommandResult.Error("World spawn is not available.");

        BlockPos spawn = epos.AsBlockPos;

       
        previousPositions[player.PlayerUID] = player.Entity.Pos.XYZ;
        SavePreviousPositions();

        
        lastSpawnUse[player.PlayerUID] = now;

        // + 1 blokk magasra ez vajon maradjon?
        double tx = spawn.X;
        double ty = spawn.Y + 1;
        double tz = spawn.Z;

        player.Entity.TeleportToDouble(tx, ty, tz);

        LogAction($"{player.PlayerName} teleported to world spawn: {spawn.X} {spawn.Y} {spawn.Z}");
        string suffix = spawnCooldownSeconds > 0 ? $" Cooldown: {spawnCooldownSeconds / 60} min" : "";
        player.SendMessage(GlobalConstants.GeneralChatGroup,
            $"Teleported to world spawn", EnumChatType.CommandSuccess);

        return TextCommandResult.Success();
    }

    private string GetHomeName(TextCommandCallingArgs args)
    {
        if (singleMode) return "default";
        return (args.ArgCount >= 1 && args[0] is string s && !string.IsNullOrEmpty(s)) ? s : "default";
    }

    private TextCommandResult SetHomeCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        string homeName = GetHomeName(args);
        if (singleMode && homeName != "default")
            return TextCommandResult.Error("Single mode: only one home allowed. Use /sethome without a name.");

        if (!playerHomes.TryGetValue(player.PlayerUID, out var homes))
        {
            homes = new Dictionary<string, Vec3d>();
            playerHomes[player.PlayerUID] = homes;
        }

        homes[homeName] = player.Entity.Pos.XYZ;
        SaveHomes();
        LogAction($"{player.PlayerName} set home '{homeName}' at {player.Entity.Pos.XYZ}");
        player.SendMessage(GlobalConstants.GeneralChatGroup, $"Home '{homeName}' set and saved!", EnumChatType.CommandSuccess);
        return TextCommandResult.Success();
    }

    private TextCommandResult HomeCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        string homeName = GetHomeName(args);
        if (singleMode && homeName != "default")
            return TextCommandResult.Error("Single mode: only 'default' home can be used.");

        DateTime currentTime = DateTime.UtcNow;

        if (lastHomeUse.TryGetValue(player.PlayerUID, out DateTime lastUsedTime))
        {
            double timeSinceLastUse = (currentTime - lastUsedTime).TotalSeconds;
            if (timeSinceLastUse < homeCooldownSeconds)
            {
                double remainingTime = homeCooldownSeconds - timeSinceLastUse;
                double remainingMinutes = Math.Floor(remainingTime / 60);
                double remainingSeconds = remainingTime % 60;
                string timeMessage = remainingMinutes > 0
                    ? $"{remainingMinutes} min {Math.Ceiling(remainingSeconds)} sec"
                    : $"{Math.Ceiling(remainingSeconds)} sec";
                return TextCommandResult.Error($"You must wait {timeMessage} before using /home again.");
            }
        }

        if (playerHomes.TryGetValue(player.PlayerUID, out var homes) && homes.TryGetValue(homeName, out Vec3d homePos))
        {
            previousPositions[player.PlayerUID] = player.Entity.Pos.XYZ;
            SavePreviousPositions();
            lastHomeUse[player.PlayerUID] = currentTime;
            player.Entity.TeleportTo(homePos);
            LogAction($"{player.PlayerName} teleported to home '{homeName}' at {homePos}");
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                $"Teleported to home '{homeName}'! Cooldown: {homeCooldownSeconds / 60} min", EnumChatType.CommandSuccess);
            return TextCommandResult.Success();
        }
        return TextCommandResult.Error($"No home set with name '{homeName}'.");
    }

    private TextCommandResult BackCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        DateTime currentTime = DateTime.UtcNow;

        if (lastBackUse.TryGetValue(player.PlayerUID, out DateTime lastUsedTime))
        {
            double timeSinceLastUse = (currentTime - lastUsedTime).TotalSeconds;
            if (timeSinceLastUse < backCooldownSeconds)
            {
                double remainingTime = backCooldownSeconds - timeSinceLastUse;
                double remainingMinutes = Math.Floor(remainingTime / 60);
                double remainingSeconds = remainingTime % 60;
                string timeMessage = remainingMinutes > 0
                    ? $"{remainingMinutes} min {Math.Ceiling(remainingSeconds)} sec"
                    : $"{Math.Ceiling(remainingSeconds)} sec";
                return TextCommandResult.Error($"You must wait {timeMessage} before using /back again.");
            }
        }

        if (previousPositions.TryGetValue(player.PlayerUID, out Vec3d previousPos))
        {
            lastBackUse[player.PlayerUID] = currentTime;
            player.Entity.TeleportTo(previousPos);
            LogAction($"{player.PlayerName} used back to {previousPos}");
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                $"Teleported back! Cooldown: {backCooldownSeconds / 60} min", EnumChatType.CommandSuccess);
            return TextCommandResult.Success();
        }
        return TextCommandResult.Error("No previous position found.");
    }

    private TextCommandResult ListHomesCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        if (playerHomes.TryGetValue(player.PlayerUID, out var homes) && homes.Count > 0)
            return TextCommandResult.Success($"Your saved homes: {string.Join(", ", homes.Keys)}");
        return TextCommandResult.Success("You have no saved homes.");
    }

    private TextCommandResult DeleteHomeCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        string homeName = (args.ArgCount >= 1 && args[0] is string s) ? s : null;
        if (string.IsNullOrEmpty(homeName)) return TextCommandResult.Error("Usage: /delhome <name>");
        if (singleMode && homeName != "default")
            return TextCommandResult.Error("Single mode: only 'default' home can be deleted.");

        if (playerHomes.TryGetValue(player.PlayerUID, out var homes) && homes.Remove(homeName))
        {
            if (homes.Count == 0) playerHomes.Remove(player.PlayerUID);
            SaveHomes();
            LogAction($"{player.PlayerName} deleted home '{homeName}'");
            return TextCommandResult.Success($"Home '{homeName}' deleted.");
        }
        return TextCommandResult.Error($"No home found with the name '{homeName}'.");
    }

    private TextCommandResult HomeInfoCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        if (playerHomes.TryGetValue(player.PlayerUID, out var homes) && homes.Count > 0)
        {
            List<string> lines = new List<string> { "Saved homes:" };
            foreach (var kv in homes)
            {
                Vec3d pos = kv.Value;
                lines.Add($"- {kv.Key}: X={Math.Round(pos.X)} Y={Math.Round(pos.Y)} Z={Math.Round(pos.Z)}");
            }
            return TextCommandResult.Success(string.Join("\n", lines));
        }
        return TextCommandResult.Success("You have no saved homes.");
    }

    private TextCommandResult RenameHomeCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        if (singleMode) return TextCommandResult.Error("Single mode: renaming is disabled.");

        if (args.ArgCount < 2 || !(args[0] is string oldName) || !(args[1] is string newName))
            return TextCommandResult.Error("Usage: /renamehome <oldName> <newName>");
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return TextCommandResult.Error("Old and new names are the same.");
        if (!playerHomes.TryGetValue(player.PlayerUID, out var homes) || !homes.ContainsKey(oldName))
            return TextCommandResult.Error($"No home found with name '{oldName}'.");
        if (homes.ContainsKey(newName))
            return TextCommandResult.Error($"Home '{newName}' already exists.");

        var pos = homes[oldName];
        homes.Remove(oldName);
        homes[newName] = pos;
        SaveHomes();
        LogAction($"{player.PlayerName} renamed home '{oldName}' to '{newName}'");
        return TextCommandResult.Success($"Home '{oldName}' renamed to '{newName}'.");
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath);
                var config = JsonUtil.FromString<TeleportConfig>(json);
                homeCooldownSeconds = config?.HomeCooldownSeconds ?? 300;
                backCooldownSeconds = config?.BackCooldownSeconds ?? 300;
                spawnCooldownSeconds = config?.SpawnCooldownSeconds ?? 300; // alap: 300 mp
                singleMode = string.Equals(config?.TeleportMode, "single", StringComparison.OrdinalIgnoreCase);

                spawnCommandsEnabled = config?.EnableSpawnCommands ?? true;
                backEnabled = config?.EnableBackCommand ?? true;
                return;
            }
        }
        catch { System.Console.WriteLine("Error loading config, using default values."); }

        // Alapértelmezések
        homeCooldownSeconds = 300;
        backCooldownSeconds = 300;
        spawnCooldownSeconds = 300;
        singleMode = false;
        spawnCommandsEnabled = true;
        backEnabled = true;

        SaveConfig();
    }

    private void SaveConfig()
    {
        var config = new TeleportConfig
        {
            TeleportMode = singleMode ? "single" : "multi",
            HomeCooldownSeconds = homeCooldownSeconds,
            BackCooldownSeconds = backCooldownSeconds,
            SpawnCooldownSeconds = spawnCooldownSeconds,
            EnableSpawnCommands = spawnCommandsEnabled,
            EnableBackCommand = backEnabled
        };
        try { File.WriteAllText(ConfigFilePath, JsonUtil.ToString(config)); }
        catch (Exception ex) { System.Console.WriteLine($"Error saving config: {ex.Message}"); }
    }

    private void SaveHomes()
    {
        try { File.WriteAllBytes(SaveFilePath, JsonUtil.ToBytes(playerHomes)); }
        catch (Exception ex) { System.Console.WriteLine($"Error saving homes: {ex.Message}"); }
    }

    private void LoadHomes()
    {
        try
        {
            if (File.Exists(SaveFilePath))
            {
                playerHomes = JsonUtil.FromBytes<Dictionary<string, Dictionary<string, Vec3d>>>(File.ReadAllBytes(SaveFilePath));
                if (singleMode)
                {
                    foreach (var uid in new List<string>(playerHomes.Keys))
                    {
                        var homes = playerHomes[uid];
                        if (homes == null || homes.Count <= 1) continue;

                        if (homes.TryGetValue("default", out var defPos))
                            playerHomes[uid] = new Dictionary<string, Vec3d> { ["default"] = defPos };
                        else
                        {
                            Vec3d first = new Vec3d();
                            foreach (var kv in homes) { first = kv.Value; break; }
                            playerHomes[uid] = new Dictionary<string, Vec3d> { ["default"] = first };
                        }
                    }
                    SaveHomes();
                }
            }
        }
        catch (Exception ex) { System.Console.WriteLine($"Error loading homes: {ex.Message}"); }
    }

    private void SavePreviousPositions()
    {
        try { File.WriteAllBytes(PreviousPositionsFilePath, JsonUtil.ToBytes(previousPositions)); }
        catch (Exception ex) { System.Console.WriteLine($"Error saving previous positions: {ex.Message}"); }
    }

    private void LoadPreviousPositions()
    {
        try
        {
            if (File.Exists(PreviousPositionsFilePath))
                previousPositions = JsonUtil.FromBytes<Dictionary<string, Vec3d>>(File.ReadAllBytes(PreviousPositionsFilePath));
        }
        catch (Exception ex) { System.Console.WriteLine($"Error loading previous positions: {ex.Message}"); }
    }

    private void LogAction(string message)
    {
        try { File.AppendAllText(LogFilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}"); }
        catch (Exception ex) { System.Console.WriteLine($"Error writing to log: {ex.Message}"); }
    }
}

public class TeleportConfig
{
    public string TeleportMode { get; set; } = "multi"; // default is multi
    public double HomeCooldownSeconds { get; set; } = 300;
    public double BackCooldownSeconds { get; set; } = 300;

    public bool EnableSpawnCommands { get; set; } = true;
    public bool EnableBackCommand { get; set; } = true;

    public double SpawnCooldownSeconds { get; set; } = 300;
}
