using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Config;

public class TeleportMod : ModSystem
{
    private Dictionary<string, Dictionary<string, Vec3d>> playerHomes = new Dictionary<string, Dictionary<string, Vec3d>>();
    private Dictionary<string, DateTime> lastHomeUse = new Dictionary<string, DateTime>();
    private Dictionary<string, DateTime> lastBackUse = new Dictionary<string, DateTime>();
    private Dictionary<string, Vec3d> previousPositions = new Dictionary<string, Vec3d>();

    private double homeCooldownSeconds;
    private double backCooldownSeconds;

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
        ConfigFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), ConfigFileName);
        SaveFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), SaveFileName);
        PreviousPositionsFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), PreviousPositionsFileName);
        LogFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), LogFileName);

        LoadConfig();
        LoadHomes();
        LoadPreviousPositions();

        var commands = api.ChatCommands;

        commands.Create("sethome")
            .WithDescription("Sets a home location. Usage: /sethome [name]")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(SetHomeCommand);

        commands.Create("home")
            .WithDescription($"Teleports you to a home. Usage: /home [name] (Cooldown: {homeCooldownSeconds / 60} min)")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(HomeCommand);

        commands.Create("back")
            .WithDescription($"Teleports you to your last location (Cooldown: {backCooldownSeconds / 60} min)")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(BackCommand);

        commands.Create("listhomes")
            .WithDescription("Lists your saved home names")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(ListHomesCommand);

        commands.Create("delhome")
            .WithDescription("Deletes a saved home. Usage: /delhome <name>")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(DeleteHomeCommand);

        commands.Create("homeinfo")
            .WithDescription("Shows coordinates for all your saved homes")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(HomeInfoCommand);
    }

    private string GetHomeName(TextCommandCallingArgs args)
    {
        string raw = args.RawArgs?.ToString() ?? "";
        return !string.IsNullOrWhiteSpace(raw) ? raw.Trim().Split(' ')[0] : "default";
    }

    private TextCommandResult SetHomeCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        string homeName = GetHomeName(args);

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
        else
        {
            return TextCommandResult.Error($"No home set with name '{homeName}'. Use /sethome {homeName} first!");
        }
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
        else
        {
            return TextCommandResult.Error("No previous position found. Use /home first to create a return point!");
        }
    }

    private TextCommandResult ListHomesCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        if (playerHomes.TryGetValue(player.PlayerUID, out var homes) && homes.Count > 0)
        {
            string homeList = string.Join(", ", homes.Keys);
            return TextCommandResult.Success($"Your saved homes: {homeList}");
        }
        else
        {
            return TextCommandResult.Success("You have no saved homes. Use /sethome [name] to set one.");
        }
    }

    private TextCommandResult DeleteHomeCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        string raw = args.RawArgs?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return TextCommandResult.Error("Usage: /delhome <name>");

        string homeName = raw.Trim().Split(' ')[0];

        if (playerHomes.TryGetValue(player.PlayerUID, out var homes) && homes.Remove(homeName))
        {
            if (homes.Count == 0) playerHomes.Remove(player.PlayerUID);

            SaveHomes();
            LogAction($"{player.PlayerName} deleted home '{homeName}'");
            return TextCommandResult.Success($"Home '{homeName}' has been deleted.");
        }
        else
        {
            return TextCommandResult.Error($"No home found with the name '{homeName}'.");
        }
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
        else
        {
            return TextCommandResult.Success("You have no saved homes. Use /sethome [name] to set one.");
        }
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
                return;
            }
        }
        catch
        {
            System.Console.WriteLine("Error loading config, using default values.");
        }

        homeCooldownSeconds = 300;
        backCooldownSeconds = 300;
        SaveConfig();
    }

    private void SaveConfig()
    {
        var config = new TeleportConfig
        {
            HomeCooldownSeconds = homeCooldownSeconds,
            BackCooldownSeconds = backCooldownSeconds
        };
        try
        {
            string json = JsonUtil.ToString(config);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    private void SaveHomes()
    {
        try
        {
            File.WriteAllBytes(SaveFilePath, JsonUtil.ToBytes(playerHomes));
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error saving homes: {ex.Message}");
        }
    }

    private void LoadHomes()
    {
        try
        {
            if (File.Exists(SaveFilePath))
            {
                playerHomes = JsonUtil.FromBytes<Dictionary<string, Dictionary<string, Vec3d>>>(File.ReadAllBytes(SaveFilePath));
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error loading homes: {ex.Message}");
        }
    }

    private void SavePreviousPositions()
    {
        try
        {
            File.WriteAllBytes(PreviousPositionsFilePath, JsonUtil.ToBytes(previousPositions));
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error saving previous positions: {ex.Message}");
        }
    }

    private void LoadPreviousPositions()
    {
        try
        {
            if (File.Exists(PreviousPositionsFilePath))
            {
                previousPositions = JsonUtil.FromBytes<Dictionary<string, Vec3d>>(File.ReadAllBytes(PreviousPositionsFilePath));
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error loading previous positions: {ex.Message}");
        }
    }

    private void LogAction(string message)
    {
        try
        {
            string logEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, logEntry);
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error writing to log: {ex.Message}");
        }
    }
}

public class TeleportConfig
{
    public double HomeCooldownSeconds { get; set; } = 300;
    public double BackCooldownSeconds { get; set; } = 300;
}
