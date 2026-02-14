using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
public class TeleportMod : ModSystem
{
    private ICoreServerAPI sapi;

    private Dictionary<string, Dictionary<string, Vec3d>> playerHomes = new Dictionary<string, Dictionary<string, Vec3d>>();
    private Dictionary<string, Dictionary<string, Vec3d>> playerFarms = new Dictionary<string, Dictionary<string, Vec3d>>();
    private Dictionary<string, Dictionary<string, Vec3d>> playerIndustries = new Dictionary<string, Dictionary<string, Vec3d>>();

    private Dictionary<string, DateTime> lastHomeUse = new Dictionary<string, DateTime>();
    private Dictionary<string, DateTime> lastFarmUse = new Dictionary<string, DateTime>();
    private Dictionary<string, DateTime> lastIndustryUse = new Dictionary<string, DateTime>();
    private Dictionary<string, DateTime> lastBackUse = new Dictionary<string, DateTime>();
    private Dictionary<string, Vec3d> previousPositions = new Dictionary<string, Vec3d>();
    private Dictionary<string, DateTime> lastSpawnUse = new Dictionary<string, DateTime>();

    // Walk credit / progress (non-teleport walked distance)
    private Dictionary<string, double> walkedProgress = new Dictionary<string, double>();
    private Dictionary<string, Vec3d> lastKnownPos = new Dictionary<string, Vec3d>();

    private double homeCooldownSeconds;
    private double farmCooldownSeconds;
    private double industryCooldownSeconds;
    private double backCooldownSeconds;
    private double spawnCooldownSeconds;

    // New: global teleport single-mode AND per-type single-mode for homes
    private bool teleportSingleMode; // global single-mode switch for home/farm/industry
    private bool homeSingleMode;     // per-home single mode (explicit)
    private bool spawnCommandsEnabled;
    private bool backEnabled;
    private bool enableFarmCommands;
    private bool enableIndustryCommands;

    // Teleport cost settings (global + per-type)
    private bool teleportCostEnabled;          // master switch (applies to home/farm/industry unless per-type free)
    private double teleportCostMultiplier;     // global multiplier

    private bool backTeleportFree;             // /back free?
    private double backTeleportMultiplier;     // /back multiplier

    // Home cost flags
    private bool defaultHomeFree;              // default home free?
    private bool homeTeleportFree;             // home teleports free by config (non-default)
    private double defaultHomeMultiplier;      // default home multiplier
    private double homeTeleportMultiplier;     // non-default home multiplier

    private bool spawnTeleportFree;            // spawn free?
    private double spawnTeleportMultiplier;    // spawn multiplier

    // Farm settings
    private bool farmSingleMode;
    private bool farmTeleportFree;
    private double farmTeleportMultiplier;
    private int maxFarms;

    // Industry settings
    private bool industrySingleMode;
    private bool industryTeleportFree;
    private double industryTeleportMultiplier;
    private int maxIndustries;

    // TP2P
    private bool tp2pEnabled;
    private bool tp2pFree;
    private double tp2pTeleportMultiplier;
    private Dictionary<string, PendingTp2p> pendingTp2p = new Dictionary<string, PendingTp2p>();
    private double tp2pRequestTimeoutSeconds = 60;

    private class PendingTp2p
    {
        public string RequesterUid;
        public string RequesterName;
        public DateTime CreatedUtc;
    }

    private int maxHomes;

    // Death handling
    private bool resetWalkOnDeath;
    private bool suppressRespawnTick;
    private HashSet<string> suppressNextWalkTick = new HashSet<string>();

    private double deathWalkLossPercent;
    private double maxWalkCredit;

    private int walkSampleIntervalMs;
    private int walkSaveIntervalMs;
    private int _saveAccumMs = 0;

    private const string ConfigFileName = "MHT_config.json";
    private const string SaveFileName = "MHT_playerHomes.json";
    private const string SaveFarmsFileName = "MHT_playerFarms.json";
    private const string SaveIndustriesFileName = "MHT_playerIndustries.json";
    private const string PreviousPositionsFileName = "MHT_previousPositions.json";
    private const string WalkProgressFileName = "MHT_walkprogress.json";
    private const string LogFileName = "MHT_teleportmod.log";

    private string ConfigFilePath;
    private string SaveFilePath;
    private string SaveFarmsFilePath;
    private string SaveIndustriesFilePath;
    private string PreviousPositionsFilePath;
    private string WalkProgressFilePath;
    private string LogFilePath;

    private long walkTickListenerId = 0;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        ConfigFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), ConfigFileName);
        SaveFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), SaveFileName);
        SaveFarmsFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), SaveFarmsFileName);
        SaveIndustriesFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), SaveIndustriesFileName);
        PreviousPositionsFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), PreviousPositionsFileName);
        WalkProgressFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), WalkProgressFileName);
        LogFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), LogFileName);

        LoadConfig();
        LoadHomes();
        LoadFarms();
        LoadIndustries();
        LoadPreviousPositions();
        LoadWalkProgress();

        int sampleMs = walkSampleIntervalMs;
        if (sampleMs < 200) sampleMs = 200;
        if (sampleMs > 60000) sampleMs = 60000;
        walkTickListenerId = sapi.Event.RegisterGameTickListener(UpdateWalkProgressTick, sampleMs);

        sapi.Event.OnEntityDeath += OnEntityDeathResetWalk;

        var commands = api.ChatCommands;
        var parsers = commands.Parsers;

        // Homes
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

        commands.Create("renamehome")
            .WithDescription("Renames a saved home. Usage: /renamehome <oldName> <newName>")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(parsers.Word("oldName"), parsers.Word("newName"))
            .HandleWith(RenameHomeCommand);

        commands.Create("delallhomes")
            .WithDescription("Deletes all saved homes")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(DeleteAllHomesCommand);

        // Farms (guarded by enableFarmCommands)
        if (enableFarmCommands)
        {
            commands.Create("setfarm")
                .WithDescription("Sets a farm location. Usage: /setfarm [name]")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.OptionalWord("name"))
                .HandleWith(SetFarmCommand);

            commands.Create("farm")
                .WithDescription($"Teleports you to a farm. Usage: /farm [name] (Cooldown: {farmCooldownSeconds / 60} min)")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.OptionalWord("name"))
                .HandleWith(FarmCommand);

            commands.Create("listfarm")
                .WithDescription("Lists your saved farm names")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(ListFarmsCommand);

            commands.Create("delfarm")
                .WithDescription("Deletes a saved farm. Usage: /delfarm <name>")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.Word("name"))
                .HandleWith(DeleteFarmCommand);

            commands.Create("renamefarm")
                .WithDescription("Renames a saved farm. Usage: /renamefarm <oldName> <newName>")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.Word("oldName"), parsers.Word("newName"))
                .HandleWith(RenameFarmCommand);
            commands.Create("delallfarms")
    .WithDescription("Deletes all saved farms")
    .RequiresPrivilege(Privilege.chat)
    .RequiresPlayer()
    .HandleWith(DeleteAllFarmsCommand);
        }

        // Industries (guarded by enableIndustryCommands)
        if (enableIndustryCommands)
        {
            commands.Create("setindustry")
                .WithDescription("Sets an industry location. Usage: /setindustry [name]")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.OptionalWord("name"))
                .HandleWith(SetIndustryCommand);

            commands.Create("industry")
                .WithDescription($"Teleports you to an industry. Usage: /industry [name] (Cooldown: {industryCooldownSeconds / 60} min)")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.OptionalWord("name"))
                .HandleWith(IndustryCommand);

            commands.Create("listindustry")
                .WithDescription("Lists your saved industry names")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(ListIndustriesCommand);

            commands.Create("delindustry")
                .WithDescription("Deletes a saved industry. Usage: /delindustry <name>")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.Word("name"))
                .HandleWith(DeleteIndustryCommand);

            commands.Create("renameindustry")
                .WithDescription("Renames a saved industry. Usage: /renameindustry <oldName> <newName>")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.Word("oldName"), parsers.Word("newName"))
                .HandleWith(RenameIndustryCommand);
            commands.Create("delallindustries")
    .WithDescription("Deletes all saved industries")
    .RequiresPrivilege(Privilege.chat)
    .RequiresPlayer()
    .HandleWith(DeleteAllIndustriesCommand);
        }

        // Walk credit + costs
        commands.Create("walkcredit")
            .WithDescription("Shows your current non-teleport walk credit in blocks")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(WalkCreditCommand);

        commands.Create("listhomescost")
            .WithDescription("Lists each home's distance and teleport cost from your current position")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(ListHomesCostCommand);

        // Spawn / TP2P commands remain unchanged
        if (spawnCommandsEnabled)
        {
            string desc = spawnCooldownSeconds > 0
                ? $"Teleport to the world's default spawn point (Cooldown: {spawnCooldownSeconds / 60} min)"
                : "Teleport to the world's default spawn point";

            commands.Create("tospawn")
                .WithDescription(desc)
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(TpToSpawnCommand);

            commands.Create("tpspawn")
                .WithDescription(desc)
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(TpToSpawnCommand);

            commands.Create("tptospawn")
                .WithDescription(desc)
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(TpToSpawnCommand);
        }

        if (tp2pEnabled)
        {
            commands.Create("tp2p")
                .WithDescription("Send a teleport request to a player. Usage: /tp2p <player>")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.Word("player"))
                .HandleWith(Tp2pRequestCommand);

            commands.Create("tpaccept")
                .WithDescription("Accept a TP2P request. Usage: /tpaccept <player>")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.Word("player"))
                .HandleWith(Tp2pAcceptCommand);

            commands.Create("tpdeny")
                .WithDescription("Deny a TP2P request. Usage: /tpdeny <player>")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(parsers.Word("player"))
                .HandleWith(Tp2pDenyCommand);

            commands.Create("tp2pcost")
                .WithDescription("Show TP2P cost to a player or all online players. Usage: /tp2pcost [player]")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .WithArgs(commands.Parsers.OptionalWord("player"))
                .HandleWith(Tp2pCostCommand);
        }

        if (backEnabled)
        {
            commands.Create("back")
                .WithDescription($"Teleports you to your last location (Cooldown: {backCooldownSeconds / 60} min)")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(BackCommand);
        }
    }

    // ===== Helpers =====

    private string FormatCooldownTime(double seconds)
    {
        double mins = Math.Floor(seconds / 60);
        double secs = seconds % 60;
        return mins > 0 ? $"{mins} min {Math.Ceiling(secs)} sec" : $"{Math.Ceiling(secs)} sec";
    }

    private TextCommandResult CooldownError(string commandName, double remainingSeconds)
    {
        string timeMessage = FormatCooldownTime(remainingSeconds);
        return TextCommandResult.Error($"You must wait {timeMessage} before using /{commandName} again.");
    }

    private bool TryChargeForTeleport(string uid, int distBlocks, double perTypeMultiplier, bool typeFree, out double charged, out string error)
    {
        charged = 0;
        error = null;
        if (!teleportCostEnabled || typeFree) return true;
        double needed = distBlocks * teleportCostMultiplier * perTypeMultiplier;
        double have = walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0;
        if (have < needed)
        {
            error = $"Not enough walk credit: need {Math.Ceiling(needed)} blocks, you have {Math.Floor(have)}.";
            return false;
        }
        walkedProgress[uid] = have - needed;
        charged = needed;
        SaveWalkProgress();
        return true;
    }

    private void DoTeleport(IServerPlayer player, Vec3d fromPos, Vec3d toPos)
    {
        var uid = player.PlayerUID;
        previousPositions[uid] = fromPos;
        SavePreviousPositions();
        lastKnownPos[uid] = toPos;
        player.Entity.TeleportTo(toPos);
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
                return CooldownError("tospawn", remain);
            }
        }

        var epos = sapi?.World?.DefaultSpawnPosition;
        if (epos == null) return TextCommandResult.Error("World spawn is not available.");
        BlockPos spawn = epos.AsBlockPos;

        Vec3d fromPos = player.Entity.Pos.XYZ;
        Vec3d toPos = new Vec3d(spawn.X, spawn.Y + 1, spawn.Z);

        int distBlocks = CalcBlockDistance(fromPos, toPos);
        var uid = player.PlayerUID;
        double charged = 0;
        bool chargeThis = teleportCostEnabled && !spawnTeleportFree;
        if (chargeThis)
        {
            if (!TryChargeForTeleport(uid, distBlocks, spawnTeleportMultiplier, spawnTeleportFree, out charged, out var err))
                return TextCommandResult.Error(err);
        }

        DoTeleport(player, fromPos, toPos);
        lastSpawnUse[uid] = DateTime.UtcNow;

        LogAction($"{player.PlayerName} teleported to world spawn: {spawn.X} {spawn.Y} {spawn.Z} (~{distBlocks} blocks, charge={Math.Round(charged)})");
        player.SendMessage(GlobalConstants.GeneralChatGroup,
            chargeThis
                ? $"Teleported to world spawn (~{distBlocks} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress[uid])}."
                : $"Teleported to world spawn (~{distBlocks} blocks).",
            EnumChatType.CommandSuccess);

        return TextCommandResult.Success();
    }

    private string GetNameArg(TextCommandCallingArgs args, bool singleModeForType)
    {
        if (singleModeForType) return "default";
        return (args.ArgCount >= 1 && args[0] is string s && !string.IsNullOrEmpty(s)) ? s : "default";
    }

    // Home commands
    private TextCommandResult SetHomeCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        string homeName = GetNameArg(args, homeSingleMode);
        if (homeSingleMode && homeName != "default")
            return TextCommandResult.Error("Single mode: only one home allowed. Use /sethome without a name.");

        if (!playerHomes.TryGetValue(player.PlayerUID, out var homes))
        {
            homes = new Dictionary<string, Vec3d>();
            playerHomes[player.PlayerUID] = homes;
        }

        if (!homes.ContainsKey(homeName))
        {
            int effectiveMax = homeSingleMode ? Math.Min(1, Math.Max(0, maxHomes)) : maxHomes;
            if (effectiveMax > 0 && homes.Count >= effectiveMax)
                return TextCommandResult.Error($"You reached the home limit ({effectiveMax}). Delete one with /delhome <name>.");
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

        string homeName = GetNameArg(args, homeSingleMode);
        if (homeSingleMode && homeName != "default")
            return TextCommandResult.Error("Single mode: only 'default' home can be used.");

        DateTime currentTime = DateTime.UtcNow;
        if (lastHomeUse.TryGetValue(player.PlayerUID, out DateTime lastUsedTime))
        {
            double timeSinceLastUse = (currentTime - lastUsedTime).TotalSeconds;
            if (timeSinceLastUse < homeCooldownSeconds)
            {
                double remainingTime = homeCooldownSeconds - timeSinceLastUse;
                return CooldownError("home", remainingTime);
            }
        }

        if (playerHomes.TryGetValue(player.PlayerUID, out var homes) && homes.TryGetValue(homeName, out Vec3d homePos))
        {
            Vec3d fromPos = player.Entity.Pos.XYZ;
            Vec3d toPos = homePos;
            int distBlocks = CalcBlockDistance(fromPos, toPos);
            var uid = player.PlayerUID;
            double charged = 0;

            bool isDefault = homeName == "default";
            bool freeByConfig = isDefault ? defaultHomeFree : homeTeleportFree;
            double perTypeMult = isDefault ? defaultHomeMultiplier : homeTeleportMultiplier;

            if (!TryChargeForTeleport(uid, distBlocks, perTypeMult, freeByConfig, out charged, out var err))
                return TextCommandResult.Error(err);

            DoTeleport(player, fromPos, toPos);
            lastHomeUse[uid] = currentTime;

            LogAction($"{player.PlayerName} teleported to home '{homeName}' at {toPos} (~{distBlocks} blocks, charge={Math.Round(charged)})");
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                teleportCostEnabled && !freeByConfig
                    ? $"Teleported to home '{homeName}' (~{distBlocks} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress[uid])}. Cooldown: {homeCooldownSeconds / 60} min"
                    : $"Teleported to home '{homeName}' (~{distBlocks} blocks). Cooldown: {homeCooldownSeconds / 60} min",
                EnumChatType.CommandSuccess);

            return TextCommandResult.Success();
        }
        return TextCommandResult.Error($"No home set with name '{homeName}'.");
    }

    // Farm commands
    private TextCommandResult SetFarmCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        bool effectiveFarmSingle = farmSingleMode || teleportSingleMode;
        string name = GetNameArg(args, effectiveFarmSingle);
        if (effectiveFarmSingle && name != "default")
            return TextCommandResult.Error("Single mode: only one farm allowed. Use /setfarm without a name.");

        if (!playerFarms.TryGetValue(player.PlayerUID, out var farms))
        {
            farms = new Dictionary<string, Vec3d>();
            playerFarms[player.PlayerUID] = farms;
        }

        if (!farms.ContainsKey(name))
        {
            int effectiveMax = effectiveFarmSingle ? Math.Min(1, Math.Max(0, maxFarms)) : maxFarms;
            if (effectiveMax > 0 && farms.Count >= effectiveMax)
                return TextCommandResult.Error($"You reached the farm limit ({effectiveMax}). Delete one with /delfarm <name>.");
        }

        farms[name] = player.Entity.Pos.XYZ;
        SaveFarms();
        LogAction($"{player.PlayerName} set farm '{name}' at {player.Entity.Pos.XYZ}");
        player.SendMessage(GlobalConstants.GeneralChatGroup, $"Farm '{name}' set and saved!", EnumChatType.CommandSuccess);
        return TextCommandResult.Success();
    }

    private TextCommandResult FarmCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        bool effectiveFarmSingle = farmSingleMode || teleportSingleMode;
        string name = GetNameArg(args, effectiveFarmSingle);
        if (effectiveFarmSingle && name != "default")
            return TextCommandResult.Error("Single mode: only 'default' farm can be used.");

        DateTime now = DateTime.UtcNow;
        if (farmCooldownSeconds > 0 && lastFarmUse.TryGetValue(player.PlayerUID, out var last))
        {
            double elapsed = (now - last).TotalSeconds;
            if (elapsed < farmCooldownSeconds)
            {
                double remain = farmCooldownSeconds - elapsed;
                return CooldownError("farm", remain);
            }
        }

        if (playerFarms.TryGetValue(player.PlayerUID, out var farms) && farms.TryGetValue(name, out Vec3d pos))
        {
            Vec3d fromPos = player.Entity.Pos.XYZ;
            Vec3d toPos = pos;
            int dist = CalcBlockDistance(fromPos, toPos);
            var uid = player.PlayerUID;
            double charged = 0;
            if (!TryChargeForTeleport(uid, dist, farmTeleportMultiplier, farmTeleportFree, out charged, out var err))
                return TextCommandResult.Error(err);

            DoTeleport(player, fromPos, toPos);
            lastFarmUse[uid] = DateTime.UtcNow;

            LogAction($"{player.PlayerName} teleported to farm '{name}' at {toPos} (~{dist} blocks, charge={Math.Round(charged)})");
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                teleportCostEnabled && !farmTeleportFree
                    ? $"Teleported to farm '{name}' (~{dist} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress[uid])}."
                    : $"Teleported to farm '{name}' (~{dist} blocks).",
                EnumChatType.CommandSuccess);

            return TextCommandResult.Success();
        }
        return TextCommandResult.Error($"No farm set with name '{name}'.");
    }

    private TextCommandResult ListFarmsCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        if (playerFarms.TryGetValue(player.PlayerUID, out var farms) && farms.Count > 0)
            return TextCommandResult.Success($"Your saved farms: {string.Join(", ", farms.Keys)}");
        return TextCommandResult.Success("You have no saved farms.");
    }

    private TextCommandResult DeleteFarmCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        string name = (args.ArgCount >= 1 && args[0] is string s) ? s : null;
        if (string.IsNullOrEmpty(name)) return TextCommandResult.Error("Usage: /delfarm <name>");
        bool effectiveFarmSingle = farmSingleMode || teleportSingleMode;
        if (effectiveFarmSingle && name != "default")
            return TextCommandResult.Error("Single mode: only 'default' farm can be deleted.");

        if (playerFarms.TryGetValue(player.PlayerUID, out var farms) && farms.Remove(name))
        {
            if (farms.Count == 0) playerFarms.Remove(player.PlayerUID);
            SaveFarms();
            LogAction($"{player.PlayerName} deleted farm '{name}'");
            return TextCommandResult.Success($"Farm '{name}' deleted.");
        }
        return TextCommandResult.Error($"No farm found with the name '{name}'.");
    }

    private TextCommandResult RenameFarmCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        bool effectiveFarmSingle = farmSingleMode || teleportSingleMode;
        if (effectiveFarmSingle) return TextCommandResult.Error("Single mode: renaming is disabled for farms.");

        if (args.ArgCount < 2 || !(args[0] is string oldName) || !(args[1] is string newName))
            return TextCommandResult.Error("Usage: /renamefarm <oldName> <newName>");
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return TextCommandResult.Error("Old and new names are the same.");
        if (!playerFarms.TryGetValue(player.PlayerUID, out var farms) || !farms.ContainsKey(oldName))
            return TextCommandResult.Error($"No farm found with name '{oldName}'.");
        if (farms.ContainsKey(newName))
            return TextCommandResult.Error($"Farm '{newName}' already exists.");

        var pos = farms[oldName];
        farms.Remove(oldName);
        farms[newName] = pos;
        SaveFarms();
        LogAction($"{player.PlayerName} renamed farm '{oldName}' to '{newName}'");
        return TextCommandResult.Success($"Farm '{oldName}' renamed to '{newName}'.");
    }

    // Industry commands
    private TextCommandResult SetIndustryCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        bool effectiveIndustrySingle = industrySingleMode || teleportSingleMode;
        string name = GetNameArg(args, effectiveIndustrySingle);
        if (effectiveIndustrySingle && name != "default")
            return TextCommandResult.Error("Single mode: only one industry allowed. Use /setindustry without a name.");

        if (!playerIndustries.TryGetValue(player.PlayerUID, out var inds))
        {
            inds = new Dictionary<string, Vec3d>();
            playerIndustries[player.PlayerUID] = inds;
        }

        if (!inds.ContainsKey(name))
        {
            int effectiveMax = effectiveIndustrySingle ? Math.Min(1, Math.Max(0, maxIndustries)) : maxIndustries;
            if (effectiveMax > 0 && inds.Count >= effectiveMax)
                return TextCommandResult.Error($"You reached the industry limit ({effectiveMax}). Delete one with /delindustry <name>.");
        }

        inds[name] = player.Entity.Pos.XYZ;
        SaveIndustries();
        LogAction($"{player.PlayerName} set industry '{name}' at {player.Entity.Pos.XYZ}");
        player.SendMessage(GlobalConstants.GeneralChatGroup, $"Industry '{name}' set and saved!", EnumChatType.CommandSuccess);
        return TextCommandResult.Success();
    }

    private TextCommandResult IndustryCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        bool effectiveIndustrySingle = industrySingleMode || teleportSingleMode;
        string name = GetNameArg(args, effectiveIndustrySingle);
        if (effectiveIndustrySingle && name != "default")
            return TextCommandResult.Error("Single mode: only 'default' industry can be used.");

        DateTime now = DateTime.UtcNow;
        if (industryCooldownSeconds > 0 && lastIndustryUse.TryGetValue(player.PlayerUID, out var last))
        {
            double elapsed = (now - last).TotalSeconds;
            if (elapsed < industryCooldownSeconds)
            {
                double remain = industryCooldownSeconds - elapsed;
                return CooldownError("industry", remain);
            }
        }

        if (playerIndustries.TryGetValue(player.PlayerUID, out var inds) && inds.TryGetValue(name, out Vec3d pos))
        {
            Vec3d fromPos = player.Entity.Pos.XYZ;
            Vec3d toPos = pos;
            int dist = CalcBlockDistance(fromPos, toPos);
            var uid = player.PlayerUID;
            double charged = 0;
            if (!TryChargeForTeleport(uid, dist, industryTeleportMultiplier, industryTeleportFree, out charged, out var err))
                return TextCommandResult.Error(err);

            DoTeleport(player, fromPos, toPos);
            lastIndustryUse[uid] = DateTime.UtcNow;

            LogAction($"{player.PlayerName} teleported to industry '{name}' at {toPos} (~{dist} blocks, charge={Math.Round(charged)})");
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                teleportCostEnabled && !industryTeleportFree
                    ? $"Teleported to industry '{name}' (~{dist} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress[uid])}."
                    : $"Teleported to industry '{name}' (~{dist} blocks).",
                EnumChatType.CommandSuccess);

            return TextCommandResult.Success();
        }
        return TextCommandResult.Error($"No industry set with name '{name}'.");
    }

    private TextCommandResult ListIndustriesCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        if (playerIndustries.TryGetValue(player.PlayerUID, out var inds) && inds.Count > 0)
            return TextCommandResult.Success($"Your saved industries: {string.Join(", ", inds.Keys)}");
        return TextCommandResult.Success("You have no saved industries.");
    }

    private TextCommandResult DeleteIndustryCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        string name = (args.ArgCount >= 1 && args[0] is string s) ? s : null;
        if (string.IsNullOrEmpty(name)) return TextCommandResult.Error("Usage: /delindustry <name>");
        bool effectiveIndustrySingle = industrySingleMode || teleportSingleMode;
        if (effectiveIndustrySingle && name != "default")
            return TextCommandResult.Error("Single mode: only 'default' industry can be deleted.");

        if (playerIndustries.TryGetValue(player.PlayerUID, out var inds) && inds.Remove(name))
        {
            if (inds.Count == 0) playerIndustries.Remove(player.PlayerUID);
            SaveIndustries();
            LogAction($"{player.PlayerName} deleted industry '{name}'");
            return TextCommandResult.Success($"Industry '{name}' deleted.");
        }
        return TextCommandResult.Error($"No industry found with the name '{name}'.");
    }

    private TextCommandResult RenameIndustryCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        bool effectiveIndustrySingle = industrySingleMode || teleportSingleMode;
        if (effectiveIndustrySingle) return TextCommandResult.Error("Single mode: renaming is disabled for industries.");

        if (args.ArgCount < 2 || !(args[0] is string oldName) || !(args[1] is string newName))
            return TextCommandResult.Error("Usage: /renameindustry <oldName> <newName>");
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return TextCommandResult.Error("Old and new names are the same.");
        if (!playerIndustries.TryGetValue(player.PlayerUID, out var inds) || !inds.ContainsKey(oldName))
            return TextCommandResult.Error($"No industry found with name '{oldName}'.");
        if (inds.ContainsKey(newName))
            return TextCommandResult.Error($"Industry '{newName}' already exists.");

        var pos = inds[oldName];
        inds.Remove(oldName);
        inds[newName] = pos;
        SaveIndustries();
        LogAction($"{player.PlayerName} renamed industry '{oldName}' to '{newName}'");
        return TextCommandResult.Success($"Industry '{oldName}' renamed to '{newName}'.");
    }

    // Back command
    private TextCommandResult BackCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        var uid = player.PlayerUID;
        DateTime currentTime = DateTime.UtcNow;

        // Cooldown check (standardized)
        if (lastBackUse.TryGetValue(uid, out DateTime lastUsedTime))
        {
            double timeSinceLastUse = (currentTime - lastUsedTime).TotalSeconds;
            if (timeSinceLastUse < backCooldownSeconds)
            {
                double remainingTime = backCooldownSeconds - timeSinceLastUse;
                return CooldownError("back", remainingTime);
            }
        }

        // Try in-memory previous position first
        if (!previousPositions.TryGetValue(uid, out Vec3d previousPos))
        {
            // Attempt to reload from disk (in case it wasn't loaded or was changed on-disk)
            try
            {
                LoadPreviousPositions();
            }
            catch { /* ignore; we'll handle absence below */ }

            if (!previousPositions.TryGetValue(uid, out previousPos))
            {
                // Diagnostics: log keys present to help debugging (no sensitive data)
                try
                {
                    LogAction($"Back: no previous position for {player.PlayerName} ({uid}). Stored keys: {(previousPositions.Count > 0 ? string.Join(",", previousPositions.Keys) : "<none>")}");
                }
                catch { /* best-effort logging */ }

                return TextCommandResult.Error("No previous position found. Teleport commands store your previous location — use /home, /farm, /industry, /tospawn or /tp2p first.");
            }
        }

        // Perform teleport cost check
        Vec3d fromPos = player.Entity.Pos.XYZ;
        Vec3d toPos = previousPos;
        int distBlocks = CalcBlockDistance(fromPos, toPos);

        double charged = 0;
        bool chargeThis = teleportCostEnabled && !backTeleportFree;
        if (chargeThis)
        {
            if (!TryChargeForTeleport(uid, distBlocks, backTeleportMultiplier, backTeleportFree, out charged, out var err))
                return TextCommandResult.Error(err);
        }

        // Update cooldown and last-known and teleport
        lastBackUse[uid] = currentTime;
        lastKnownPos[uid] = toPos;

        // Teleport (use TeleportToDouble to avoid interpolation issues on server)
        player.Entity.TeleportToDouble(toPos.X, toPos.Y, toPos.Z);

        // Remove previous position after using /back to avoid repeated identical backs
        previousPositions.Remove(uid);
        SavePreviousPositions();

        LogAction($"{player.PlayerName} used /back to {toPos} (~{distBlocks} blocks, charge={Math.Round(charged)})");
        player.SendMessage(GlobalConstants.GeneralChatGroup,
            chargeThis
                ? $"Teleported back (~{distBlocks} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0)}. Cooldown: {backCooldownSeconds / 60} min"
                : $"Teleported back (~{distBlocks} blocks). Cooldown: {backCooldownSeconds / 60} min",
            EnumChatType.CommandSuccess);

        return TextCommandResult.Success();
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
        if (homeSingleMode && homeName != "default")
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
        if (homeSingleMode) return TextCommandResult.Error("Single mode: renaming is disabled.");

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

    private TextCommandResult DeleteAllHomesCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        var uid = player.PlayerUID;

        if (playerHomes.ContainsKey(uid))
        {
            playerHomes.Remove(uid);
            SaveHomes();
            LogAction($"{player.PlayerName} deleted ALL homes");
            return TextCommandResult.Success("All your saved homes have been deleted.");
        }

        return TextCommandResult.Error("You have no saved homes to delete.");
    }
    private TextCommandResult DeleteAllFarmsCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        var uid = player.PlayerUID;

        if (playerFarms.ContainsKey(uid))
        {
            playerFarms.Remove(uid);
            SaveFarms();
            LogAction($"{player.PlayerName} deleted ALL farms");
            return TextCommandResult.Success("All your saved farms have been deleted.");
        }

        return TextCommandResult.Error("You have no saved farms to delete.");
    }

    private TextCommandResult DeleteAllIndustriesCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        var uid = player.PlayerUID;

        if (playerIndustries.ContainsKey(uid))
        {
            playerIndustries.Remove(uid);
            SaveIndustries();
            LogAction($"{player.PlayerName} deleted ALL industries");
            return TextCommandResult.Success("All your saved industries have been deleted.");
        }

        return TextCommandResult.Error("You have no saved industries to delete.");
    }
    // Walk credit & costs
    private TextCommandResult WalkCreditCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        var uid = player.PlayerUID;
        double have = walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0;
        string status = $"Teleport cost: {(teleportCostEnabled ? "ENABLED" : "DISABLED")}\n" +
                        $"- Global multiplier: {teleportCostMultiplier}\n" +
                        $"- Home: single={homeSingleMode}, multiplier={homeTeleportMultiplier}, free={homeTeleportFree}, defaultFree={defaultHomeFree}\n" +
                        $"- Farm: single={farmSingleMode || teleportSingleMode}, multiplier={farmTeleportMultiplier}, free={farmTeleportFree}, max={maxFarms}\n" +
                        $"- Industry: single={industrySingleMode || teleportSingleMode}, multiplier={industryTeleportMultiplier}, free={industryTeleportFree}, max={maxIndustries}\n" +
                        $"- Spawn multiplier: {spawnTeleportMultiplier}, Spawn free: {spawnTeleportFree}\n" +
                        $"- Back multiplier: {backTeleportMultiplier}, Back free: {backTeleportFree}\n" +
                        $"- TP2P: {(tp2pEnabled ? "ENABLED" : "DISABLED")}, TP2P multiplier: {tp2pTeleportMultiplier}, TP2P free: {tp2pFree}\n" +
                        $"- Global single-mode: {teleportSingleMode}\n" +
                        $"- Max homes: {maxHomes}\n" +
                        $"- ResetWalkOnDeath: {resetWalkOnDeath}, SuppressRespawnTick: {suppressRespawnTick}\n" +
                        $"- DeathWalkLossPercent: {deathWalkLossPercent}%\n" +
                        $"- MaxWalkCredit: {(maxWalkCredit > 0 ? maxWalkCredit.ToString() : "∞")}\n" +
                        $"- WalkSampleIntervalMs: {walkSampleIntervalMs}, WalkSaveIntervalMs: {walkSaveIntervalMs}";
        return TextCommandResult.Success($"{status}\nYour walk credit: {Math.Floor(have)} blocks");
    }

    private TextCommandResult ListHomesCostCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        var uid = player.PlayerUID;
        if (!playerHomes.TryGetValue(uid, out var homes) || homes.Count == 0)
            return TextCommandResult.Success("You have no saved homes.");

        Vec3d from = player.Entity.Pos.XYZ;
        double have = walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0;

        List<string> lines = new List<string>();
        lines.Add($"Teleport cost: {(teleportCostEnabled ? "ENABLED" : "DISABLED")}");
        lines.Add($"Global multiplier: {teleportCostMultiplier}");
        lines.Add($"MaxWalkCredit: {(maxWalkCredit > 0 ? maxWalkCredit.ToString() : "∞")}");
        lines.Add("Home costs from your current position:");
        foreach (var kv in homes)
        {
            string name = kv.Key;
            Vec3d pos = kv.Value;
            int dist = CalcBlockDistance(from, pos);

            bool isDefaultHome = name == "default";
            bool freeByConfig = isDefaultHome ? defaultHomeFree : homeTeleportFree;

            if (!teleportCostEnabled || freeByConfig)
            {
                lines.Add($"- {name}: distance ~{dist} blocks, cost 0 (free or system OFF)");
            }
            else
            {
                double perCmdMult = isDefaultHome ? defaultHomeMultiplier : homeTeleportMultiplier;
                double cost = dist * teleportCostMultiplier * perCmdMult;
                bool enough = have >= cost;
                lines.Add($"- {name}: distance ~{dist} blocks, cost {Math.Ceiling(cost)} (you have {Math.Floor(have)}) {(enough ? "" : "[NOT ENOUGH]")}");
            }
        }
        return TextCommandResult.Success(string.Join("\n", lines));
    }

    // TP2P helpers
    private IServerPlayer ResolveOnlinePlayerByNameOrPrefix(string name, out string err)
    {
        err = null;
        if (sapi?.World?.AllOnlinePlayers == null) { err = "No online players."; return null; }
        IServerPlayer exact = null;
        IServerPlayer prefix = null;
        int prefixCount = 0;
        foreach (IServerPlayer sp in sapi.World.AllOnlinePlayers)
        {
            if (sp.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase)) { exact = sp; break; }
            if (sp.PlayerName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { prefix = sp; prefixCount++; }
        }
        if (exact != null) return exact;
        if (prefixCount == 1 && prefix != null) return prefix;
        if (prefixCount > 1) { err = "Name is ambiguous, please type more letters."; return null; }
        err = "Player not found.";
        return null;
    }

    private void PruneExpiredTp2p()
    {
        if (pendingTp2p == null || pendingTp2p.Count == 0) return;
        List<string> remove = new List<string>();
        DateTime now = DateTime.UtcNow;
        foreach (var kv in pendingTp2p)
        {
            if ((now - kv.Value.CreatedUtc).TotalSeconds > tp2pRequestTimeoutSeconds) remove.Add(kv.Key);
        }
        foreach (var k in remove) pendingTp2p.Remove(k);
    }

    private TextCommandResult Tp2pRequestCommand(TextCommandCallingArgs args)
    {
        var caller = args.Caller.Player as IServerPlayer;
        if (caller == null) return TextCommandResult.Error("This command can only be used by a player.");
        if (!tp2pEnabled) return TextCommandResult.Error("TP2P is disabled.");
        string pname = args[0] as string;
        if (string.IsNullOrWhiteSpace(pname)) return TextCommandResult.Error("Usage: /tp2p <player>");
        if (pname.Equals(caller.PlayerName, StringComparison.OrdinalIgnoreCase)) return TextCommandResult.Error("You cannot send a request to yourself.");
        PruneExpiredTp2p();

        string err;
        var target = ResolveOnlinePlayerByNameOrPrefix(pname, out err);
        if (target == null) return TextCommandResult.Error(err);
        var targetUid = target.PlayerUID;

        pendingTp2p[targetUid] = new PendingTp2p { RequesterUid = caller.PlayerUID, RequesterName = caller.PlayerName, CreatedUtc = DateTime.UtcNow };

        target.SendMessage(GlobalConstants.GeneralChatGroup, $"{caller.PlayerName} wants to teleport to you. Type /tpaccept {caller.PlayerName} to accept or /tpdeny {caller.PlayerName} to deny. Expires in {tp2pRequestTimeoutSeconds}s.", EnumChatType.CommandSuccess);
        caller.SendMessage(GlobalConstants.GeneralChatGroup, $"Teleport request sent to {target.PlayerName}.", EnumChatType.CommandSuccess);
        LogAction($"{caller.PlayerName} sent TP2P request to {target.PlayerName}");
        return TextCommandResult.Success();
    }

    private TextCommandResult Tp2pAcceptCommand(TextCommandCallingArgs args)
    {
        var target = args.Caller.Player as IServerPlayer; // the receiver of the request
        if (target == null) return TextCommandResult.Error("This command can only be used by a player.");
        if (!tp2pEnabled) return TextCommandResult.Error("TP2P is disabled.");
        string rname = args[0] as string;
        if (string.IsNullOrWhiteSpace(rname)) return TextCommandResult.Error("Usage: /tpaccept <player>");
        PruneExpiredTp2p();

        string err;
        var requester = ResolveOnlinePlayerByNameOrPrefix(rname, out err);
        if (requester == null) return TextCommandResult.Error(err);

        PendingTp2p pend;
        if (!pendingTp2p.TryGetValue(target.PlayerUID, out pend) || pend.RequesterUid != requester.PlayerUID)
            return TextCommandResult.Error("No pending request from that player or it has expired.");

        if ((DateTime.UtcNow - pend.CreatedUtc).TotalSeconds > tp2pRequestTimeoutSeconds)
        {
            pendingTp2p.Remove(target.PlayerUID);
            return TextCommandResult.Error("Request expired.");
        }

        if (requester.Entity?.Pos == null || target.Entity?.Pos == null)
        {
            pendingTp2p.Remove(target.PlayerUID);
            return TextCommandResult.Error("Teleport failed: player entity not ready, try again.");
        }
        Vec3d fromPos = requester.Entity.Pos.XYZ;
        Vec3d toPos = target.Entity.Pos.XYZ;
        int distBlocks = CalcBlockDistance(fromPos, toPos);

        var uid = requester.PlayerUID;
        double charged = 0;
        bool chargeThis = teleportCostEnabled && !tp2pFree;
        if (chargeThis)
        {
            if (!TryChargeForTeleport(uid, distBlocks, tp2pTeleportMultiplier, tp2pFree, out charged, out var err2))
            {
                pendingTp2p.Remove(target.PlayerUID);
                requester.SendMessage(GlobalConstants.GeneralChatGroup, $"Not enough walk credit for TP2P: need {Math.Ceiling(distBlocks * teleportCostMultiplier * tp2pTeleportMultiplier)}, you have {Math.Floor(walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0)}.", EnumChatType.CommandError);
                target.SendMessage(GlobalConstants.GeneralChatGroup, $"{requester.PlayerName} does not have enough walk credit to teleport.", EnumChatType.CommandError);
                return TextCommandResult.Error("Not enough credit.");
            }
        }

        previousPositions[uid] = fromPos;
        SavePreviousPositions();
        lastKnownPos[uid] = toPos;

        requester.Entity.TeleportToDouble(toPos.X, toPos.Y, toPos.Z);

        LogAction($"{requester.PlayerName} TP2P to {target.PlayerName} (~{distBlocks} blocks, charge={Math.Round(charged)})");
        requester.SendMessage(GlobalConstants.GeneralChatGroup,
            chargeThis ? $"Teleported to {target.PlayerName} (~{distBlocks} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress[uid])}."
                       : $"Teleported to {target.PlayerName} (~{distBlocks} blocks).",
            EnumChatType.CommandSuccess);
        target.SendMessage(GlobalConstants.GeneralChatGroup, $"{requester.PlayerName} teleported to you.", EnumChatType.CommandSuccess);

        pendingTp2p.Remove(target.PlayerUID);
        return TextCommandResult.Success();
    }

    private TextCommandResult Tp2pDenyCommand(TextCommandCallingArgs args)
    {
        var target = args.Caller.Player as IServerPlayer;
        if (target == null) return TextCommandResult.Error("This command can only be used by a player.");
        if (!tp2pEnabled) return TextCommandResult.Error("TP2P is disabled.");
        string rname = args[0] as string;
        if (string.IsNullOrWhiteSpace(rname)) return TextCommandResult.Error("Usage: /tpdeny <player>");
        PruneExpiredTp2p();

        string err;
        var requester = ResolveOnlinePlayerByNameOrPrefix(rname, out err);
        if (requester == null) return TextCommandResult.Error(err);

        PendingTp2p pend;
        if (!pendingTp2p.TryGetValue(target.PlayerUID, out pend) || pend.RequesterUid != requester.PlayerUID)
            return TextCommandResult.Error("No pending request from that player or it has expired.");

        pendingTp2p.Remove(target.PlayerUID);
        requester.SendMessage(GlobalConstants.GeneralChatGroup, $"{target.PlayerName} denied your teleport request.", EnumChatType.CommandError);
        target.SendMessage(GlobalConstants.GeneralChatGroup, $"You denied {requester.PlayerName}'s teleport request.", EnumChatType.CommandSuccess);
        LogAction($"{target.PlayerName} denied TP2P from {requester.PlayerName}");
        return TextCommandResult.Success();
    }

    private TextCommandResult Tp2pCostCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");
        if (!tp2pEnabled) return TextCommandResult.Error("TP2P is disabled.");

        string opt = args.ArgCount >= 1 ? args[0] as string : null;
        Vec3d from = player.Entity.Pos.XYZ;
        double have = walkedProgress.ContainsKey(player.PlayerUID) ? walkedProgress[player.PlayerUID] : 0;
        bool chargeThis = teleportCostEnabled && !tp2pFree;

        if (!string.IsNullOrWhiteSpace(opt))
        {
            string err;
            var target = ResolveOnlinePlayerByNameOrPrefix(opt, out err);
            if (target == null) return TextCommandResult.Error(err);
            Vec3d to = target.Entity.Pos.XYZ;
            int dist = CalcBlockDistance(from, to);
            double cost = chargeThis ? dist * teleportCostMultiplier * tp2pTeleportMultiplier : 0;
            bool enough = have >= cost;
            return TextCommandResult.Success($"To {target.PlayerName}: distance ~{dist} blocks, cost {Math.Ceiling(cost)} (you have {Math.Floor(have)}) {(enough ? "" : "[NOT ENOUGH]")}");
        }
        else
        {
            var online = sapi?.World?.AllOnlinePlayers;
            if (online == null) return TextCommandResult.Success("No other online players.");
            List<string> lines = new List<string>();
            lines.Add($"TP2P cost: {(teleportCostEnabled ? "ENABLED" : "DISABLED")}, TP2P free: {tp2pFree}, TP2P multiplier: {tp2pTeleportMultiplier}");
            foreach (IServerPlayer sp in online)
            {
                if (sp.PlayerUID == player.PlayerUID) continue;
                if (sp.Entity?.Pos == null) continue;
                Vec3d to = sp.Entity.Pos.XYZ;

                int dist = CalcBlockDistance(from, to);
                double cost = chargeThis ? dist * teleportCostMultiplier * tp2pTeleportMultiplier : 0;
                bool enough = have >= cost;
                lines.Add($"- {sp.PlayerName}: ~{dist} blocks, cost {Math.Ceiling(cost)} (you have {Math.Floor(have)}) {(enough ? "" : "[NOT ENOUGH]")}");
                if (lines.Count >= 12) break;
            }
            return TextCommandResult.Success(string.Join("\n", lines));
        }
    }

    // Walk progress
    private void UpdateWalkProgressTick(float dt)
    {
        try
        {
            var online = sapi?.World?.AllOnlinePlayers;
            if (online == null) return;

            foreach (IServerPlayer sp in online)
            {
                var uid = sp.PlayerUID;
                if (sp.Entity?.Pos == null) continue;
                var curPos = sp.Entity.Pos.XYZ;

                if (suppressRespawnTick && suppressNextWalkTick.Contains(uid))
                {
                    lastKnownPos[uid] = curPos;
                    suppressNextWalkTick.Remove(uid);
                    continue;
                }

                if (!lastKnownPos.TryGetValue(uid, out var prev))
                {
                    lastKnownPos[uid] = curPos;
                    continue;
                }

                int moved = CalcBlockDistance(prev, curPos);
                if (moved > 0)
                {
                    if (!walkedProgress.ContainsKey(uid)) walkedProgress[uid] = 0;
                    walkedProgress[uid] = ClampToMax(walkedProgress[uid] + moved);
                }
                lastKnownPos[uid] = curPos;
            }

            if (walkSaveIntervalMs > 0)
            {
                _saveAccumMs += walkSampleIntervalMs;
                if (_saveAccumMs >= walkSaveIntervalMs)
                {
                    _saveAccumMs = 0;
                    SaveWalkProgress();
                }
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"UpdateWalkProgressTick error: {ex.Message}");
        }
    }

    private void OnEntityDeathResetWalk(Entity entity, DamageSource damageSource)
    {
        try
        {
            if (entity is EntityPlayer ep && ep.Player is IServerPlayer sp)
            {
                var uid = sp.PlayerUID;

                if (suppressRespawnTick)
                    suppressNextWalkTick.Add(uid);

                double lossPercent = resetWalkOnDeath ? 100.0 : Math.Max(0, Math.Min(100, deathWalkLossPercent));

                double oldVal = walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0;
                double newVal;

                if (lossPercent >= 100.0)
                    newVal = 0;
                else if (lossPercent <= 0.0)
                    newVal = oldVal;
                else
                    newVal = oldVal * (1.0 - (lossPercent / 100.0));

                newVal = ClampToMax(Math.Max(0, newVal));
                walkedProgress[uid] = newVal;
                SaveWalkProgress();

                LogAction($"{sp.PlayerName} died - walk credit changed from {Math.Floor(oldVal)} to {Math.Floor(newVal)} (lossPercent={lossPercent}%)");
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"OnEntityDeathResetWalk error: {ex.Message}");
        }
    }

    // Persistence
    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath);
                var config = JsonUtil.FromString<TeleportConfig>(json);

                // cooldowns
                homeCooldownSeconds = config?.HomeCooldownSeconds ?? 300;
                farmCooldownSeconds = config?.FarmCooldownSeconds ?? 300;
                industryCooldownSeconds = config?.IndustryCooldownSeconds ?? 300;
                backCooldownSeconds = config?.BackCooldownSeconds ?? 300;
                spawnCooldownSeconds = config?.SpawnCooldownSeconds ?? 300;

                // teleport single-mode: support legacy TeleportMode string OR new bool
                bool legacySingle = string.Equals(config?.TeleportMode, "single", StringComparison.OrdinalIgnoreCase);
                teleportSingleMode = config?.TeleportSingleMode ?? legacySingle;

                // per-type single modes: per-type config or fallback to global teleportSingleMode
                homeSingleMode = config?.HomeSingleMode ?? teleportSingleMode;
                farmSingleMode = config?.FarmSingleMode ?? teleportSingleMode;
                industrySingleMode = config?.IndustrySingleMode ?? teleportSingleMode;

                spawnCommandsEnabled = config?.EnableSpawnCommands ?? true;
                backEnabled = config?.EnableBackCommand ?? true;
                enableFarmCommands = config?.EnableFarmCommands ?? true;
                enableIndustryCommands = config?.EnableIndustryCommands ?? true;

                // teleport cost global
                teleportCostEnabled = config?.TeleportCostEnabled ?? false;
                teleportCostMultiplier = config?.TeleportCostMultiplier ?? 1.0;

                // back
                backTeleportFree = config?.BackTeleportFree ?? true;
                backTeleportMultiplier = config?.BackTeleportMultiplier ?? 1.0;

                // home cost flags
                defaultHomeFree = config?.DefaultHomeFree ?? false;
                homeTeleportFree = config?.HomeTeleportFree ?? defaultHomeFree;
                defaultHomeMultiplier = config?.DefaultHomeMultiplier ?? 1.0;
                homeTeleportMultiplier = config?.HomeTeleportMultiplier ?? 1.0;

                // spawn
                spawnTeleportFree = config?.SpawnTeleportFree ?? false;
                spawnTeleportMultiplier = config?.SpawnTeleportMultiplier ?? 1.0;

                // Farm
                // farmSingleMode is already assigned above
                farmTeleportFree = config?.FarmTeleportFree ?? false;
                farmTeleportMultiplier = config?.FarmTeleportMultiplier ?? 1.0;
                maxFarms = config?.MaxFarms ?? 0;

                // Industry
                // industrySingleMode assigned above
                industryTeleportFree = config?.IndustryTeleportFree ?? false;
                industryTeleportMultiplier = config?.IndustryTeleportMultiplier ?? 1.0;
                maxIndustries = config?.MaxIndustries ?? 0;

                // TP2P
                tp2pEnabled = config?.EnableTP2P ?? true;
                tp2pFree = config?.TP2PTeleportFree ?? false;
                tp2pTeleportMultiplier = config?.TP2PTeleportMultiplier ?? 1.0;
                tp2pRequestTimeoutSeconds = config?.TP2PRequestTimeoutSeconds ?? 60;

                maxHomes = config?.MaxHomes ?? 0;

                resetWalkOnDeath = config?.ResetWalkOnDeath ?? true;
                suppressRespawnTick = config?.SuppressRespawnTick ?? true;

                walkSampleIntervalMs = config?.WalkSampleIntervalMs ?? 1000;
                walkSaveIntervalMs = config?.WalkSaveIntervalMs ?? 30000;

                deathWalkLossPercent = config?.DeathWalkLossPercent ?? 0;
                maxWalkCredit = config?.MaxWalkCredit ?? 0;

                if (deathWalkLossPercent < 0) deathWalkLossPercent = 0;
                if (deathWalkLossPercent > 100) deathWalkLossPercent = 100;

                return;
            }
        }
        catch { System.Console.WriteLine("Error loading config, using default values."); }

        // defaults
        homeCooldownSeconds = 300;
        farmCooldownSeconds = 300;
        industryCooldownSeconds = 300;
        backCooldownSeconds = 300;
        spawnCooldownSeconds = 300;

        teleportSingleMode = false;
        homeSingleMode = false;
        farmSingleMode = false;
        industrySingleMode = false;

        spawnCommandsEnabled = true;
        backEnabled = true;
        enableFarmCommands = true;
        enableIndustryCommands = true;

        teleportCostEnabled = false;
        teleportCostMultiplier = 1.0;

        backTeleportFree = true;
        backTeleportMultiplier = 1.0;

        defaultHomeFree = false;
        homeTeleportFree = false;
        defaultHomeMultiplier = 1.0;
        homeTeleportMultiplier = 1.0;

        spawnTeleportFree = false;
        spawnTeleportMultiplier = 1.0;

        farmTeleportFree = false;
        farmTeleportMultiplier = 1.0;
        maxFarms = 0;

        industryTeleportFree = false;
        industryTeleportMultiplier = 1.0;
        maxIndustries = 0;

        tp2pEnabled = true;
        tp2pFree = false;
        tp2pTeleportMultiplier = 1.0;
        tp2pRequestTimeoutSeconds = 60;

        maxHomes = 0;

        resetWalkOnDeath = true;
        suppressRespawnTick = true;

        walkSampleIntervalMs = 1000;
        walkSaveIntervalMs = 30000;

        deathWalkLossPercent = 0;
        maxWalkCredit = 0;

        SaveConfig();
        SaveConfigSample();
    }

    private void SaveConfig()
    {
        var config = new TeleportConfig
        {
            TeleportMode = teleportSingleMode ? "single" : "multi", // legacy string supported
            TeleportSingleMode = teleportSingleMode,
            HomeSingleMode = homeSingleMode,

            HomeCooldownSeconds = homeCooldownSeconds,
            FarmCooldownSeconds = farmCooldownSeconds,
            IndustryCooldownSeconds = industryCooldownSeconds,
            BackCooldownSeconds = backCooldownSeconds,
            SpawnCooldownSeconds = spawnCooldownSeconds,

            EnableSpawnCommands = spawnCommandsEnabled,
            EnableBackCommand = backEnabled,
            EnableFarmCommands = enableFarmCommands,
            EnableIndustryCommands = enableIndustryCommands,

            TeleportCostEnabled = teleportCostEnabled,
            TeleportCostMultiplier = teleportCostMultiplier,

            // Farm
            FarmSingleMode = farmSingleMode,
            FarmTeleportFree = farmTeleportFree,
            FarmTeleportMultiplier = farmTeleportMultiplier,
            MaxFarms = maxFarms,

            // Industry
            IndustrySingleMode = industrySingleMode,
            IndustryTeleportFree = industryTeleportFree,
            IndustryTeleportMultiplier = industryTeleportMultiplier,
            MaxIndustries = maxIndustries,

            // TP2P
            EnableTP2P = tp2pEnabled,
            TP2PTeleportFree = tp2pFree,
            TP2PTeleportMultiplier = tp2pTeleportMultiplier,
            TP2PRequestTimeoutSeconds = tp2pRequestTimeoutSeconds,

            BackTeleportFree = backTeleportFree,
            BackTeleportMultiplier = backTeleportMultiplier,

            DefaultHomeFree = defaultHomeFree,
            HomeTeleportFree = homeTeleportFree,
            DefaultHomeMultiplier = defaultHomeMultiplier,

            HomeTeleportMultiplier = homeTeleportMultiplier,

            SpawnTeleportFree = spawnTeleportFree,
            SpawnTeleportMultiplier = spawnTeleportMultiplier,

            MaxHomes = maxHomes,

            ResetWalkOnDeath = resetWalkOnDeath,
            SuppressRespawnTick = suppressRespawnTick,

            WalkSampleIntervalMs = walkSampleIntervalMs,
            WalkSaveIntervalMs = walkSaveIntervalMs,

            DeathWalkLossPercent = deathWalkLossPercent,
            MaxWalkCredit = maxWalkCredit
        };

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(config, options);
            string tmp = ConfigFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, ConfigFilePath, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config: {ex.Message}");
        }
    }
    private void SaveConfigSample()
    {
        try
        {
            // Create a TeleportConfig populated with current/default values so the generated sample
            // JSON is grouped and shows meaningful defaults.
            var sample = new TeleportConfig
            {
                TeleportMode = teleportSingleMode ? "single" : "multi",
                TeleportSingleMode = teleportSingleMode,
                HomeSingleMode = homeSingleMode,

                HomeCooldownSeconds = homeCooldownSeconds,
                FarmCooldownSeconds = farmCooldownSeconds,
                IndustryCooldownSeconds = industryCooldownSeconds,
                BackCooldownSeconds = backCooldownSeconds,
                SpawnCooldownSeconds = spawnCooldownSeconds,

                EnableSpawnCommands = spawnCommandsEnabled,
                EnableBackCommand = backEnabled,
                EnableFarmCommands = enableFarmCommands,
                EnableIndustryCommands = enableIndustryCommands,

                TeleportCostEnabled = teleportCostEnabled,
                TeleportCostMultiplier = teleportCostMultiplier,

                FarmSingleMode = farmSingleMode,
                FarmTeleportFree = farmTeleportFree,
                FarmTeleportMultiplier = farmTeleportMultiplier,
                MaxFarms = maxFarms,

                IndustrySingleMode = industrySingleMode,
                IndustryTeleportFree = industryTeleportFree,
                IndustryTeleportMultiplier = industryTeleportMultiplier,
                MaxIndustries = maxIndustries,

                EnableTP2P = tp2pEnabled,
                TP2PTeleportFree = tp2pFree,
                TP2PTeleportMultiplier = tp2pTeleportMultiplier,
                TP2PRequestTimeoutSeconds = tp2pRequestTimeoutSeconds,

                BackTeleportFree = backTeleportFree,
                BackTeleportMultiplier = backTeleportMultiplier,

                DefaultHomeFree = defaultHomeFree,
                HomeTeleportFree = homeTeleportFree,
                DefaultHomeMultiplier = defaultHomeMultiplier,

                HomeTeleportMultiplier = homeTeleportMultiplier,

                SpawnTeleportFree = spawnTeleportFree,
                SpawnTeleportMultiplier = spawnTeleportMultiplier,

                MaxHomes = maxHomes,

                ResetWalkOnDeath = resetWalkOnDeath,
                SuppressRespawnTick = suppressRespawnTick,

                WalkSampleIntervalMs = walkSampleIntervalMs,
                WalkSaveIntervalMs = walkSaveIntervalMs,

                DeathWalkLossPercent = deathWalkLossPercent,
                MaxWalkCredit = maxWalkCredit
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(sample, options);

            // Prepend a short explanatory header (stored in a separate example file next to the real config).
            string header = "/* Example grouped config for MultiHomeTP - read-only example. */\n";

            string examplePath = ConfigFilePath + ".example.json";
            File.WriteAllText(examplePath, header + json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config sample: {ex.Message}");
        }
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
                if (homeSingleMode)
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

    private void SaveFarms()
    {
        try { File.WriteAllBytes(SaveFarmsFilePath, JsonUtil.ToBytes(playerFarms)); }
        catch (Exception ex) { System.Console.WriteLine($"Error saving farms: {ex.Message}"); }
    }

    private void LoadFarms()
    {
        try
        {
            if (File.Exists(SaveFarmsFilePath))
            {
                playerFarms = JsonUtil.FromBytes<Dictionary<string, Dictionary<string, Vec3d>>>(File.ReadAllBytes(SaveFarmsFilePath));
                if (farmSingleMode || teleportSingleMode)
                {
                    foreach (var uid in new List<string>(playerFarms.Keys))
                    {
                        var farms = playerFarms[uid];
                        if (farms == null || farms.Count <= 1) continue;

                        if (farms.TryGetValue("default", out var defPos))
                            playerFarms[uid] = new Dictionary<string, Vec3d> { ["default"] = defPos };
                        else
                        {
                            Vec3d first = new Vec3d();
                            foreach (var kv in farms) { first = kv.Value; break; }
                            playerFarms[uid] = new Dictionary<string, Vec3d> { ["default"] = first };
                        }
                    }
                    SaveFarms();
                }
            }
        }
        catch (Exception ex) { System.Console.WriteLine($"Error loading farms: {ex.Message}"); }
    }

    private void SaveIndustries()
    {
        try { File.WriteAllBytes(SaveIndustriesFilePath, JsonUtil.ToBytes(playerIndustries)); }
        catch (Exception ex) { System.Console.WriteLine($"Error saving industries: {ex.Message}"); }
    }

    private void LoadIndustries()
    {
        try
        {
            if (File.Exists(SaveIndustriesFilePath))
            {
                playerIndustries = JsonUtil.FromBytes<Dictionary<string, Dictionary<string, Vec3d>>>(File.ReadAllBytes(SaveIndustriesFilePath));
                if (industrySingleMode || teleportSingleMode)
                {
                    foreach (var uid in new List<string>(playerIndustries.Keys))
                    {
                        var inds = playerIndustries[uid];
                        if (inds == null || inds.Count <= 1) continue;

                        if (inds.TryGetValue("default", out var defPos))
                            playerIndustries[uid] = new Dictionary<string, Vec3d> { ["default"] = defPos };
                        else
                        {
                            Vec3d first = new Vec3d();
                            foreach (var kv in inds) { first = kv.Value; break; }
                            playerIndustries[uid] = new Dictionary<string, Vec3d> { ["default"] = first };
                        }
                    }
                    SaveIndustries();
                }
            }
        }
        catch (Exception ex) { System.Console.WriteLine($"Error loading industries: {ex.Message}"); }
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

    private void LoadWalkProgress()
    {
        try
        {
            if (File.Exists(WalkProgressFilePath))
            {
                walkedProgress = JsonUtil.FromBytes<Dictionary<string, double>>(File.ReadAllBytes(WalkProgressFilePath))
                                  ?? new Dictionary<string, double>();
            }

            if (walkedProgress != null && maxWalkCredit > 0)
            {
                var keys = new List<string>(walkedProgress.Keys);
                foreach (var k in keys) walkedProgress[k] = ClampToMax(Math.Max(0, walkedProgress[k]));
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error loading walk progress: {ex.Message}");
            walkedProgress = new Dictionary<string, double>();
        }
    }

    private void SaveWalkProgress()
    {
        try
        {
            if (walkedProgress != null && maxWalkCredit > 0)
            {
                var keys = new List<string>(walkedProgress.Keys);
                foreach (var k in keys) walkedProgress[k] = ClampToMax(Math.Max(0, walkedProgress[k]));
            }

            File.WriteAllBytes(WalkProgressFilePath, JsonUtil.ToBytes(walkedProgress));
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error saving walk progress: {ex.Message}");
        }
    }

    private void LogAction(string message)
    {
        try { File.AppendAllText(LogFilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}"); }
        catch (Exception ex) { System.Console.WriteLine($"Error writing to log: {ex.Message}"); }
    }

    private int CalcBlockDistance(Vec3d from, Vec3d to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double dz = to.Z - from.Z;
        return (int)Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }

    private double ClampToMax(double value)
    {
        if (maxWalkCredit > 0) return Math.Min(value, maxWalkCredit);
        return value;
    }
}

public class TeleportConfig
{
    // Legacy string preserved for compatibility
    public string TeleportMode { get; set; } = "multi";

    // New global single-mode boolean
    public bool TeleportSingleMode { get; set; } = false;

    // Per-type single-mode (home explicit)
    public bool HomeSingleMode { get; set; } = false;

    public double HomeCooldownSeconds { get; set; } = 300;
    public double FarmCooldownSeconds { get; set; } = 300;
    public double IndustryCooldownSeconds { get; set; } = 300;
    public double BackCooldownSeconds { get; set; } = 300;
    public double SpawnCooldownSeconds { get; set; } = 300;

    public bool EnableSpawnCommands { get; set; } = true;
    public bool EnableBackCommand { get; set; } = true;
    public bool EnableFarmCommands { get; set; } = true;
    public bool EnableIndustryCommands { get; set; } = true;

    public bool TeleportCostEnabled { get; set; } = false;
    public double TeleportCostMultiplier { get; set; } = 1.0;

    // Farm
    public bool FarmSingleMode { get; set; } = false;
    public bool FarmTeleportFree { get; set; } = false;
    public double FarmTeleportMultiplier { get; set; } = 1.0;
    public int MaxFarms { get; set; } = 0;

    // Industry
    public bool IndustrySingleMode { get; set; } = false;
    public bool IndustryTeleportFree { get; set; } = false;
    public double IndustryTeleportMultiplier { get; set; } = 1.0;
    public int MaxIndustries { get; set; } = 0;

    // TP2P
    public bool EnableTP2P { get; set; } = true;
    public bool TP2PTeleportFree { get; set; } = false;
    public double TP2PTeleportMultiplier { get; set; } = 1.0;
    public double TP2PRequestTimeoutSeconds { get; set; } = 60;

    public bool BackTeleportFree { get; set; } = true;
    public double BackTeleportMultiplier { get; set; } = 1.0;

    // Home flags
    public bool DefaultHomeFree { get; set; } = false;
    public bool HomeTeleportFree { get; set; } = false;
    public double DefaultHomeMultiplier { get; set; } = 1.0;
    public double HomeTeleportMultiplier { get; set; } = 1.0;

    public bool SpawnTeleportFree { get; set; } = false;
    public double SpawnTeleportMultiplier { get; set; } = 1.0;

    public int MaxHomes { get; set; } = 0;

    public bool ResetWalkOnDeath { get; set; } = true;
    public double DeathWalkLossPercent { get; set; } = 0;
    public bool SuppressRespawnTick { get; set; } = true;

    public int WalkSampleIntervalMs { get; set; } = 1000;
    public int WalkSaveIntervalMs { get; set; } = 30000;

    public double MaxWalkCredit { get; set; } = 0;
}