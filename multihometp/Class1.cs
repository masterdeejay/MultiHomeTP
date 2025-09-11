using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

public class TeleportMod : ModSystem
{
    private ICoreServerAPI sapi;

    private Dictionary<string, Dictionary<string, Vec3d>> playerHomes = new Dictionary<string, Dictionary<string, Vec3d>>();
    private Dictionary<string, DateTime> lastHomeUse = new Dictionary<string, DateTime>();
    private Dictionary<string, DateTime> lastBackUse = new Dictionary<string, DateTime>();
    private Dictionary<string, Vec3d> previousPositions = new Dictionary<string, Vec3d>();
    private Dictionary<string, DateTime> lastSpawnUse = new Dictionary<string, DateTime>();

    // Walk credit / progress (nem-teleport megtett táv)
    private Dictionary<string, double> walkedProgress = new Dictionary<string, double>();
    private Dictionary<string, Vec3d> lastKnownPos = new Dictionary<string, Vec3d>();

    private double homeCooldownSeconds;
    private double backCooldownSeconds;
    private double spawnCooldownSeconds;

    private bool singleMode; // true = csak egy "default" home engedélyezett
    private bool spawnCommandsEnabled;
    private bool backEnabled;

    // Teleport-költség beállítások
    private bool teleportCostEnabled;          // master switch
    private double teleportCostMultiplier;     // globális alap szorzó

    private bool backTeleportFree;             // /back ingyenes?
    private double backTeleportMultiplier;     // /back szorzó

    private bool defaultHomeFree;              // default home ingyenes?
    private double defaultHomeMultiplier;      // default home szorzó

    private double homeTeleportMultiplier;     // nem-default home szorzó

    private bool spawnTeleportFree;            // spawn teleport ingyenes?
    private double spawnTeleportMultiplier;    // spawn teleport szorzó

    private int maxHomes;                      // max létrehozható home (0 vagy kisebb = korlátlan)

    // Halálkezelés konfiguráció
    private bool resetWalkOnDeath;             // halálkor walk reset?
    private bool suppressRespawnTick;          // respawn utáni első mintavétel lenyelése?
    private HashSet<string> suppressNextWalkTick = new HashSet<string>();

    // Mintavétel/mentés intervallumok (ms)
    private int walkSampleIntervalMs;          // milyen sűrűn számolunk
    private int walkSaveIntervalMs;            // milyen sűrűn mentünk (<=0 kikapcsol)
    private int _saveAccumMs = 0;

    private const string ConfigFileName = "MHT_config.json";
    private const string SaveFileName = "MHT_playerHomes.json";
    private const string PreviousPositionsFileName = "MHT_previousPositions.json";
    private const string WalkProgressFileName = "MHT_walkprogress.json";
    private const string LogFileName = "MHT_teleportmod.log";

    private string ConfigFilePath;
    private string SaveFilePath;
    private string PreviousPositionsFilePath;
    private string WalkProgressFilePath;
    private string LogFilePath;

    private long walkTickListenerId = 0;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        ConfigFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), ConfigFileName);
        SaveFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), SaveFileName);
        PreviousPositionsFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), PreviousPositionsFileName);
        WalkProgressFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), WalkProgressFileName);
        LogFilePath = Path.Combine(api.GetOrCreateDataPath("TeleportMod"), LogFileName);

        LoadConfig();
        LoadHomes();
        LoadPreviousPositions();
        LoadWalkProgress();

        // Tick a gyaloglás méréséhez (konfigból)
        int sampleMs = walkSampleIntervalMs;
        if (sampleMs < 200) sampleMs = 200;          // min 0.2s
        if (sampleMs > 60000) sampleMs = 60000;      // max 60s
        walkTickListenerId = sapi.Event.RegisterGameTickListener(UpdateWalkProgressTick, sampleMs);

        // Halálkor reset/suppress
        sapi.Event.OnEntityDeath += OnEntityDeathResetWalk;

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

        commands.Create("delallhomes")
            .WithDescription("Deletes all saved homes")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(DeleteAllHomesCommand);

        // Új parancsok: walk credit és homes cost
        commands.Create("walkcredit")
            .WithDescription("Shows your current non-teleport walk credit in blocks")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(WalkCreditCommand);

        commands.Create("listhomescost")
            .WithDescription("Lists each home’s distance and teleport cost from your current position")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .HandleWith(ListHomesCostCommand);

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
    }

    // ===== Teleport parancsok =====

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

        Vec3d fromPos = player.Entity.Pos.XYZ;
        Vec3d toPos = new Vec3d(spawn.X, spawn.Y + 1, spawn.Z);

        int distBlocks = CalcBlockDistance(fromPos, toPos);

        var uid = player.PlayerUID;
        double charged = 0;
        bool chargeThis = teleportCostEnabled && !spawnTeleportFree;
        if (chargeThis)
        {
            double needed = distBlocks * teleportCostMultiplier * spawnTeleportMultiplier;
            double have = walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0;
            if (have < needed)
                return TextCommandResult.Error($"Not enough walk credit: need {Math.Ceiling(needed)} blocks, you have {Math.Floor(have)}.");

            walkedProgress[uid] = have - needed;
            charged = needed;
            SaveWalkProgress();
        }

        previousPositions[uid] = fromPos;
        SavePreviousPositions();
        lastSpawnUse[uid] = DateTime.UtcNow;

        // teleport ne számítson gyaloglásnak
        lastKnownPos[uid] = toPos;

        player.Entity.TeleportToDouble(toPos.X, toPos.Y, toPos.Z);

        LogAction($"{player.PlayerName} teleported to world spawn: {spawn.X} {spawn.Y} {spawn.Z} (~{distBlocks} blocks, charge={Math.Round(charged)})");
        player.SendMessage(GlobalConstants.GeneralChatGroup,
            chargeThis
                ? $"Teleported to world spawn (~{distBlocks} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress[uid])}."
                : $"Teleported to world spawn (~{distBlocks} blocks).",
            EnumChatType.CommandSuccess);

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

        // MaxHomes limit: új létrehozásnál vizsgáljuk (felülírás nem számít)
        if (!homes.ContainsKey(homeName))
        {
            int effectiveMax = singleMode ? Math.Min(1, Math.Max(0, maxHomes)) : maxHomes;
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
            Vec3d fromPos = player.Entity.Pos.XYZ;
            Vec3d toPos = homePos;
            int distBlocks = CalcBlockDistance(fromPos, toPos);

            var uid = player.PlayerUID;
            double charged = 0;

            bool isDefaultHome = homeName == "default";
            bool freeByConfig = isDefaultHome && defaultHomeFree;

            bool chargeThis = teleportCostEnabled && !freeByConfig;
            if (chargeThis)
            {
                double perCmdMult = isDefaultHome ? defaultHomeMultiplier : homeTeleportMultiplier;
                double needed = distBlocks * teleportCostMultiplier * perCmdMult;
                double have = walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0;

                if (have < needed)
                    return TextCommandResult.Error($"Not enough walk credit: need {Math.Ceiling(needed)} blocks, you have {Math.Floor(have)}.");

                walkedProgress[uid] = have - needed;
                charged = needed;
                SaveWalkProgress();
            }

            // /back-hez előző pozíció
            previousPositions[uid] = fromPos;
            SavePreviousPositions();

            lastHomeUse[uid] = currentTime;

            // teleport ne számítson gyaloglásnak
            lastKnownPos[uid] = toPos;

            player.Entity.TeleportTo(toPos);

            LogAction($"{player.PlayerName} teleported to home '{homeName}' at {toPos} (~{distBlocks} blocks, charge={Math.Round(charged)})");
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                chargeThis
                    ? $"Teleported to home '{homeName}' (~{distBlocks} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress[uid])}. Cooldown: {homeCooldownSeconds / 60} min"
                    : $"Teleported to home '{homeName}' (~{distBlocks} blocks). Cooldown: {homeCooldownSeconds / 60} min",
                EnumChatType.CommandSuccess);

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
            Vec3d fromPos = player.Entity.Pos.XYZ;
            Vec3d toPos = previousPos;
            int distBlocks = CalcBlockDistance(fromPos, toPos);

            var uid = player.PlayerUID;
            double charged = 0;
            bool chargeThis = teleportCostEnabled && !backTeleportFree;

            if (chargeThis)
            {
                double needed = distBlocks * teleportCostMultiplier * backTeleportMultiplier;
                double have = walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0;

                if (have < needed)
                    return TextCommandResult.Error($"Not enough walk credit for /back: need {Math.Ceiling(needed)} blocks, you have {Math.Floor(have)}.");

                walkedProgress[uid] = have - needed;
                charged = needed;
                SaveWalkProgress();
            }

            lastBackUse[uid] = currentTime;

            // teleport ne számítson gyaloglásnak
            lastKnownPos[uid] = toPos;

            player.Entity.TeleportTo(toPos);

            LogAction($"{player.PlayerName} used /back to {toPos} (~{distBlocks} blocks, charge={Math.Round(charged)})");
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                chargeThis
                    ? $"Teleported back (~{distBlocks} blocks). Cost: {Math.Ceiling(charged)}. Remaining credit: {Math.Floor(walkedProgress[uid])}. Cooldown: {backCooldownSeconds / 60} min"
                    : $"Teleported back (~{distBlocks} blocks). Cooldown: {backCooldownSeconds / 60} min",
                EnumChatType.CommandSuccess);

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

    // ===== Új parancsok: walkcredit és listhomescost =====

    private TextCommandResult WalkCreditCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        var uid = player.PlayerUID;
        double have = walkedProgress.ContainsKey(uid) ? walkedProgress[uid] : 0;
        string status = $"Teleport cost: {(teleportCostEnabled ? "ENABLED" : "DISABLED")}\n" +
                        $"- Global multiplier: {teleportCostMultiplier}\n" +
                        $"- Home multiplier: {homeTeleportMultiplier}, Default home multiplier: {defaultHomeMultiplier}, Default free: {defaultHomeFree}\n" +
                        $"- Spawn multiplier: {spawnTeleportMultiplier}, Spawn free: {spawnTeleportFree}\n" +
                        $"- Back multiplier: {backTeleportMultiplier}, Back free: {backTeleportFree}\n" +
                        $"- Max homes: {maxHomes}\n" +
                        $"- ResetWalkOnDeath: {resetWalkOnDeath}, SuppressRespawnTick: {suppressRespawnTick}\n" +
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
        lines.Add("Home costs from your current position:");
        foreach (var kv in homes)
        {
            string name = kv.Key;
            Vec3d pos = kv.Value;
            int dist = CalcBlockDistance(from, pos);

            bool isDefaultHome = name == "default";
            bool freeByConfig = isDefaultHome && defaultHomeFree;

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

    // ===== Walk progress kezelés =====

    private void UpdateWalkProgressTick(float dt)
    {
        try
        {
            var online = sapi?.World?.AllOnlinePlayers;
            if (online == null) return;

            foreach (IServerPlayer sp in online)
            {
                var uid = sp.PlayerUID;
                var curPos = sp.Entity?.Pos?.XYZ ?? new Vec3d();

                // Respawn utáni első tick: ne adjunk hozzá távot, csak igazítsuk a referenciát
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
                    walkedProgress[uid] += moved;
                }
                lastKnownPos[uid] = curPos;
            }

            // Periodikus mentés (ha engedélyezve)
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

                // Respawn után az első mintavételt nyeljük le (ne számítson teleportnak)
                if (suppressRespawnTick)
                    suppressNextWalkTick.Add(uid);

                // Kredit reset csak, ha engedélyezve
                if (resetWalkOnDeath)
                {
                    walkedProgress[uid] = 0;
                    SaveWalkProgress();
                    LogAction($"{sp.PlayerName} died - walk credit reset to 0");
                }
                else
                {
                    LogAction($"{sp.PlayerName} died - walk credit NOT reset (config)");
                }
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"OnEntityDeathResetWalk error: {ex.Message}");
        }
    }

    // ===== Betöltés/mentés =====

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
                spawnCooldownSeconds = config?.SpawnCooldownSeconds ?? 300;
                singleMode = string.Equals(config?.TeleportMode, "single", StringComparison.OrdinalIgnoreCase);

                spawnCommandsEnabled = config?.EnableSpawnCommands ?? true;
                backEnabled = config?.EnableBackCommand ?? true;

                teleportCostEnabled = config?.TeleportCostEnabled ?? false;
                teleportCostMultiplier = config?.TeleportCostMultiplier ?? 1.0;

                backTeleportFree = config?.BackTeleportFree ?? true;
                backTeleportMultiplier = config?.BackTeleportMultiplier ?? 1.0;

                defaultHomeFree = config?.DefaultHomeFree ?? false;
                defaultHomeMultiplier = config?.DefaultHomeMultiplier ?? 1.0;

                homeTeleportMultiplier = config?.HomeTeleportMultiplier ?? 1.0;

                spawnTeleportFree = config?.SpawnTeleportFree ?? false;
                spawnTeleportMultiplier = config?.SpawnTeleportMultiplier ?? 1.0;

                maxHomes = config?.MaxHomes ?? 0; // 0 vagy kisebb = korlátlan

                resetWalkOnDeath = config?.ResetWalkOnDeath ?? true;
                suppressRespawnTick = config?.SuppressRespawnTick ?? true;

                walkSampleIntervalMs = config?.WalkSampleIntervalMs ?? 1000;
                walkSaveIntervalMs = config?.WalkSaveIntervalMs ?? 30000;

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

        teleportCostEnabled = false;
        teleportCostMultiplier = 1.0;

        backTeleportFree = true;
        backTeleportMultiplier = 1.0;

        defaultHomeFree = false;
        defaultHomeMultiplier = 1.0;

        homeTeleportMultiplier = 1.0;

        spawnTeleportFree = false;
        spawnTeleportMultiplier = 1.0;

        maxHomes = 0;

        resetWalkOnDeath = true;
        suppressRespawnTick = true;

        walkSampleIntervalMs = 1000;
        walkSaveIntervalMs = 30000;

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
            EnableBackCommand = backEnabled,

            TeleportCostEnabled = teleportCostEnabled,
            TeleportCostMultiplier = teleportCostMultiplier,

            BackTeleportFree = backTeleportFree,
            BackTeleportMultiplier = backTeleportMultiplier,

            DefaultHomeFree = defaultHomeFree,
            DefaultHomeMultiplier = defaultHomeMultiplier,

            HomeTeleportMultiplier = homeTeleportMultiplier,

            SpawnTeleportFree = spawnTeleportFree,
            SpawnTeleportMultiplier = spawnTeleportMultiplier,

            MaxHomes = maxHomes,

            ResetWalkOnDeath = resetWalkOnDeath,
            SuppressRespawnTick = suppressRespawnTick,

            WalkSampleIntervalMs = walkSampleIntervalMs,
            WalkSaveIntervalMs = walkSaveIntervalMs
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

    private void LoadWalkProgress()
    {
        try
        {
            if (File.Exists(WalkProgressFilePath))
            {
                walkedProgress = JsonUtil.FromBytes<Dictionary<string, double>>(File.ReadAllBytes(WalkProgressFilePath))
                                  ?? new Dictionary<string, double>();
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

    // ===== 3D távolság-számítás (X, Y, Z) =====
    private int CalcBlockDistance(Vec3d from, Vec3d to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double dz = to.Z - from.Z;
        return (int)Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }
}

public class TeleportConfig
{
    public string TeleportMode { get; set; } = "multi"; // default is multi
    public double HomeCooldownSeconds { get; set; } = 300;
    public double BackCooldownSeconds { get; set; } = 300;
    public double SpawnCooldownSeconds { get; set; } = 300;

    public bool EnableSpawnCommands { get; set; } = true;
    public bool EnableBackCommand { get; set; } = true;

    // Teleport-költség rendszer
    public bool TeleportCostEnabled { get; set; } = false;
    public double TeleportCostMultiplier { get; set; } = 1.0;

    // /back
    public bool BackTeleportFree { get; set; } = true;
    public double BackTeleportMultiplier { get; set; } = 1.0;

    // default home
    public bool DefaultHomeFree { get; set; } = false;
    public double DefaultHomeMultiplier { get; set; } = 1.0;

    // nem-default home
    public double HomeTeleportMultiplier { get; set; } = 1.0;

    // spawn
    public bool SpawnTeleportFree { get; set; } = false;
    public double SpawnTeleportMultiplier { get; set; } = 1.0;

    // max home darabszám (0 vagy kisebb = korlátlan)
    public int MaxHomes { get; set; } = 0;

    // Halálkezelés
    public bool ResetWalkOnDeath { get; set; } = true;
    public bool SuppressRespawnTick { get; set; } = true;

    // Mintavétel és mentés intervallumok (ms)
    public int WalkSampleIntervalMs { get; set; } = 1000;
    public int WalkSaveIntervalMs { get; set; } = 30000;
}
