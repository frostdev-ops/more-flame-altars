// SPDX-License-Identifier: MIT
// The server's part of the management window: the /altars command, the state it sends, and the actions it takes for a
// player who may manage (more-flame-altars.manage, admins by default). On a world a player hosts this runs in their game.
using Kiln;
using System.Text.Json;

namespace MoreFlameAltars;

public sealed partial class MoreFlameAltarsMod
{
    internal const string ManageNode = "more-flame-altars.manage";
    internal const string Channel = "more-flame-altars:ui";

    private NetChannel? _ch;
    private ModConfig<Settings>? _cfg;
    private bool _saving;  // the window or /altars set writes the settings: they apply once, after the last write

    /// <summary>Both sides: the channel (same order of messages on both), the permission node.</summary>
    private void OpenChannel()
    {
        Permissions.Register(ManageNode, PermissionLevel.Admin, "change More Flame Altars' settings and the world's Flame level");
        _ch = Network.Channel(Channel, "4")
            .Register<AltarsState>((m, _) => OnState(m), NetDirection.ToClient)
            .Register<AltarsRequest>((m, ctx) => OnRequest(m, ctx.Player), NetDirection.ToServer)
            .Register<AltarsResult>((m, _) => OnResult(m), NetDirection.ToClient)
            .Register<AltarsLimits>((m, _) => OnLimits(m), NetDirection.ToClient);
        Network.Disconnected += _ =>
        {
            if (!IsServerSide) BaseMergeHook.SetEnabled(false);  // a joined game merged because its server did
            AltarLimitHook.SetServer(null);
            AltarLimitHook.SetRules(HookRules());
            ResetWindow();
        };
    }

    /// <summary>Server: a Kiln client with the mod joined; tell it the limit at each Flame level, so its altar cursor and
    /// menus answer what the server answers.</summary>
    [Subscribe]
    private void OnPeerReady(NetPeerReadyEvent e)
    {
        if (!e.Peer.HasChannel(Channel)) return;
        if (LimitsMessage() is { ByLevel.Length: > 0 } m) _ch!.SendTo(e.Peer, m);
    }

    /// <summary>The limit at each Flame level as this server answers it now (tick thread): through the hook when it is in,
    /// else the game's table, which the game itself caps at 16.</summary>
    private static AltarsLimits LimitsMessage()
    {
        var hooked = AltarLimitHook.Installed;
        var cap = hooked ? AltarLimitHook.Live.Cap : AltarCap;
        var by = new List<byte>();
        for (var level = 0; level < 64 && WorldProgress.LimitsAt(level) is { } l; level++)
            by.Add((byte)Math.Clamp(hooked ? AltarLimitHook.Answer((uint)level, l.Altars) : l.Altars, 1, cap));
        return new AltarsLimits(cap > AltarCap, by.ToArray(), cap > AltarCap && _mergeWanted && BaseMergeHook.Installed);
    }

    /// <summary>Client: the server's limits. When they differ from this game's own (above 16, or changed while the server
    /// runs), they need the hook here too, or this game's cursor refuses what the server allows.</summary>
    private void OnLimits(AltarsLimits m)
    {
        if (Hosted) return;  // a world this game hosts: its own server part answers, in this same process
        FollowMerge(m.Merge);
        AltarLimitHook.SetServer(m.ByLevel);
        if (AltarLimitHook.Installed || !DiffersFromOwn(m.ByLevel)) return;
        if (!AltarLimitHook.Install(HookRules(), out var why))
            Log.Warn($"More Flame Altars: the server allows other altar numbers than this game's own, but this game cannot follow ({why})");
    }

    /// <summary>Client: merge the bases as the server does, or the two would disagree about which altar is in which base.</summary>
    private void FollowMerge(bool merge)
    {
        if (merge && !BaseMergeHook.Installed && !BaseMergeHook.Install(out var why))
            Log.Warn($"More Flame Altars: the server makes more than 16 altar zones one base, but this game cannot follow ({why})");
        BaseMergeHook.SetEnabled(merge && BaseMergeHook.Installed);
    }

    private static bool DiffersFromOwn(byte[] server)
    {
        for (var level = 0; level < server.Length; level++)
            if (WorldProgress.LimitsAt(level) is not { } l || Math.Min(l.Altars, AltarCap) != server[level]) return true;
        return false;
    }

    /// <summary>A change made elsewhere (Kiln's settings menu, the file): applied like one made in the window.</summary>
    [Subscribe]
    private void OnConfigChanged(ConfigChangedEvent e)
    {
        if (_saving || e.ModId != Context.Info.Id) return;
        Log.Info($"More Flame Altars: settings changed ({e.Source}): {ApplyNow()}");
    }

    [Command("altars", Description = "Opens the Flame Altars window: this world's Flame level, altars, water sources and the mod's settings")]
    private string AltarsCommand(CommandContext c)
    {
        if (c.Source.IsPlayer && IsLocalHost(c.Source.Player) && _window is not null)
        {
            SendState(c.Source.Player, open: true);
            return "";
        }
        if (c.Source.IsPlayer && Network.PeerOf(c.Source.Player) is { } peer && peer.HasChannel(Channel)
            && _ch!.SendTo(c.Source.Player, Snapshot(c.Source.Player, open: true)))
            return "";  // the window opens; nothing to say in chat
        return Summary() + (c.Source.IsPlayer ? " (the window needs More Flame Altars in your game)" : "");
    }

    [Command("altars set", Description = "Sets one of More Flame Altars' settings (altars, extraAltars, waterSources, extraWaterSources, beyondSixteen 0/1, mergeBases 0/1, buildArea 100-200); most apply at the next start",
        Permission = CommandPermission.Admin, Node = ManageNode)]
    private string SetCommand(CommandContext c, string setting, int value)
    {
        var path = SettingIndex.Paths.FirstOrDefault(p => p.Equals(setting.Trim(), StringComparison.OrdinalIgnoreCase));
        if (path is null)
        {
            c.Fail($"no setting '{setting}': {string.Join(", ", SettingIndex.Paths)}");
            return "";
        }
        var values = Saved(Cfg());
        values[Array.IndexOf(SettingIndex.Paths, path)] = value;
        if (!SettingIndex.Validate(values, out var validation))
        {
            c.Fail(validation);
            return "";
        }
        bool set;
        string error;
        _saving = true;
        try { set = Cfg().TrySet(path, SettingIndex.IsSwitch(Array.IndexOf(SettingIndex.Paths, path)) ? value != 0 : value, out error); }
        finally { _saving = false; }
        if (!set)
        {
            c.Fail($"{path} not changed: {error}");
            return "";
        }
        if (!SavedOnDisk(Cfg(), out error))
        {
            c.Fail(error);
            return "";
        }
        return $"{path} = {value}, saved. {ApplyNow()}";
    }

    [Command("altars flame", Description = "Sets the world's Flame level (1 to its maximum)", Permission = CommandPermission.Admin, Node = ManageNode)]
    private async Task FlameCommand(CommandContext c, int level)
    {
        var (ok, text) = await SetFlame(level);
        if (ok) c.Reply(text);
        else c.Fail(text);
    }

    private ModConfig<Settings> Cfg() => _cfg ??= Config.Register<Settings>();

    private static string NameOf(EntityId player) => Network.PeerOf(player)?.Name ?? player.ToString();

    private static bool Hosted => Client.IsHosting;

    private static bool IsLocalHost(EntityId player) => Hosted && !player.IsNone && player == Client.LocalPlayer;

    private void SendState(EntityId player, bool open = false)
    {
        var state = Snapshot(player, open);
        if (IsLocalHost(player)) OnState(state);
        else _ch!.SendTo(player, state);
    }

    private void SendResult(EntityId player, AltarsResult result)
    {
        if (IsLocalHost(player)) OnResult(result);
        else _ch!.SendTo(player, result);
    }

    private string AppliesWhen() => Hosted
        ? "The new numbers apply when the host starts the game again."
        : "The new numbers apply when the server starts again.";

    /// <summary>The window's state for <paramref name="player"/> (tick thread).</summary>
    private AltarsState Snapshot(EntityId player, bool open)
    {
        var cfg = Cfg();
        var running = SettingIndex.Of(cfg.Value);
        var saved = Saved(cfg);
        var hooked = AltarLimitHook.Installed;
        if (hooked)  // the altar settings apply at once through the hook: the saved ones are the running ones
            foreach (var i in SettingIndex.AltarSettings) running[i] = saved[i];
        var flame = WorldProgress.Available ? WorldProgress.Flame : null;
        var max = flame?.Max ?? 20;
        var altarsBy = new List<byte>();
        var waterBy = new List<byte>();
        for (var level = 0; level <= max && level < 64; level++)
        {
            if (WorldProgress.LimitsAt(level) is not { } l) break;
            altarsBy.Add((byte)(hooked ? AltarLimitHook.Answer((uint)level, l.Altars) : Math.Clamp(l.Altars, 0, AltarCap)));
            waterBy.Add((byte)Math.Clamp(l.WaterSources, 0, 255));
        }
        var allowed = flame is null ? null : WorldProgress.LimitsAt(flame.Level);
        var (active, note) = Activity(cfg.Value);
        if (!running.SequenceEqual(saved)) note = (note.Length > 0 ? note + " " : "") + "Saved settings differ from the running ones. " + AppliesWhen();
        return new AltarsState(open, Permissions.Has(player, ManageNode), Hosted, active, note,
            flame?.Level ?? -1, max, flame?.Altars ?? -1,
            allowed is null ? -1 : hooked ? AltarLimitHook.Answer((uint)flame!.Level, allowed.Altars) : Math.Min(allowed.Altars, AltarCap),
            flame?.WaterSources ?? -1, allowed?.WaterSources ?? -1,
            running, saved, altarsBy.ToArray(), waterBy.ToArray(), AreaNow());
    }

    /// <summary>One side of the build area at altar level 1 and up, in whole metres, as this game's table holds it now
    /// (x; the mod scales the three sides alike). Empty when the table cannot be read.</summary>
    private static int[] AreaNow()
    {
        try
        {
            if (!Assets.Available || Assets.Reflect(new AssetKey(BalancingTableId, "keen::BalancingTable")) is not { } table) return [];
            var n = Math.Min(table.Count("buildzoneSizesPerAltarLevel"), 16);
            var by = new List<int>();
            for (var level = 1; level < n; level++)
                by.Add((int)Math.Round(Convert.ToDouble(table[$"buildzoneSizesPerAltarLevel[{level}].x"], System.Globalization.CultureInfo.InvariantCulture)));
            return by.ToArray();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>The settings as the file holds them (an entry waiting for a restart keeps its file value there).</summary>
    private static int[] Saved(ModConfig<Settings> cfg)
    {
        var v = SettingIndex.Of(cfg.Value);
        foreach (var e in cfg.Entries)
        {
            var i = Array.IndexOf(SettingIndex.Paths, e.Path);
            if (i < 0 || e.FileValue is not { } fv) continue;
            if (fv is bool b) v[i] = b ? 1 : 0;
            else if (int.TryParse(Convert.ToString(fv, System.Globalization.CultureInfo.InvariantCulture), out var n)) v[i] = n;
            else if (bool.TryParse(Convert.ToString(fv, System.Globalization.CultureInfo.InvariantCulture), out var bb)) v[i] = bb ? 1 : 0;
        }
        return v;
    }

    // dev.28's TrySet logs a failed write but still returns true. Confirm persistence before claiming it was saved.
    private static bool SavedOnDisk(ModConfig<Settings> cfg, out string error)
    {
        try
        {
            using var file = JsonDocument.Parse(File.ReadAllText(cfg.FilePath), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            var saved = Saved(cfg);
            for (var i = 0; i < SettingIndex.Count; i++)
            {
                var value = file.RootElement.GetProperty(SettingIndex.Paths[i]);
                if ((SettingIndex.IsSwitch(i) ? (value.GetBoolean() ? 1 : 0) : value.GetInt32()) != saved[i])
                    throw new InvalidDataException("The file does not contain the requested settings.");
            }
            error = "";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                  KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = $"Could not confirm the settings were saved: {e.Message}";
            return false;
        }
    }

    /// <summary>Whether this start runs with the mod's numbers, and why not.</summary>
    private (bool Active, string Note) Activity(Settings s)
    {
        if (Patch(s) is null) return (true, "The settings leave the game's own numbers.");
        var state = Mods.BootState(Context.Info.Id);
        if (state.State == BootModState.Committed)
            return s.BeyondSixteen && !AltarLimitHook.Installed
                ? (false, "The data patch is active, but the experimental hook is unavailable: altar placement is capped at 16.")
                : (true, "");
        var why = state.Issues.Count > 0 ? state.Issues[0].Reason : state.State.ToString();
        if (!Capabilities.Is("mods.boot.resource_patches"))
            why = Capabilities.Why("mods.boot.resource_patches") +
                  (Hosted ? " To try it in your own world: \"preview\": [\"mods.boot.client\", \"mods.boot.resource_patches\"] in kiln/kiln.json, then restart the game." : "");
        return (false, $"Not active in this start: {why}");
    }

    private string Summary()
    {
        var s = Snapshot(EntityId.None, open: false);
        var where = s.FlameLevel < 0 ? "No world is running." :
            $"Flame level {s.FlameLevel} of {s.FlameMax}: {s.Altars} of {s.AltarsAllowed} Flame Altars, {s.Water} of {s.WaterAllowed} water sources.";
        return $"{where} {Describe(SettingIndex.From(s.Running))}. {s.Note}".Trim();
    }

    /// <summary>An action from a player's window (tick thread).</summary>
    private void OnRequest(AltarsRequest m, EntityId player)
    {
        if (m.Action == AltarsAction.Refresh)
        {
            SendState(player);
            return;
        }
        if (!Permissions.Has(player, ManageNode))
        {
            SendResult(player, new AltarsResult(false, $"Only admins may change this (permission {ManageNode})."));
            return;
        }
        switch (m.Action)
        {
            case AltarsAction.SaveSettings:
                var (ok, text) = SaveSettings(m.Settings);
                Log.Info($"More Flame Altars: {NameOf(player)} settings request: {text}");
                SendResult(player, new AltarsResult(ok, text));
                SendState(player);
                break;
            case AltarsAction.SetFlameLevel:
                _ = SetFlameFor(player, m.FlameLevel);
                break;
        }
    }

    private (bool Ok, string Text) SaveSettings(int[] values)
    {
        if (!SettingIndex.Validate(values, out var validation)) return (false, validation);
        var cfg = Cfg();
        var paths = SettingIndex.Paths;
        var errors = new List<string>();
        _saving = true;
        try
        {
            for (var i = 0; i < paths.Length; i++)
                if (!cfg.TrySet(paths[i], SettingIndex.IsSwitch(i) ? values[i] != 0 : values[i], out var error)) errors.Add($"{paths[i]}: {error}");
        }
        finally { _saving = false; }
        var applied = ApplyNow(); // earlier entries may have succeeded even if a later write failed
        if (errors.Count > 0) return (false, "Some settings could not be saved: " + string.Join("; ", errors) + ". " + applied);
        return SavedOnDisk(cfg, out var errorText)
            ? (true, $"Saved: {Describe(SettingIndex.From(values))}. {applied}")
            : (false, errorText);
    }

    private async Task SetFlameFor(EntityId player, int level)
    {
        var peer = Network.PeerOf(player);
        var session = Client.ServerWorldSession;
        var (ok, text) = await SetFlame(level);
        Context.Scheduler.Post(() =>
        {
            if (peer is not null ? !ReferenceEquals(peer, Network.PeerOf(player))
                : !IsLocalHost(player) || session != Client.ServerWorldSession) return;
            Log.Info($"More Flame Altars: {NameOf(player)} set the Flame level to {level}: {text}");
            SendResult(player, new AltarsResult(ok, text));
            SendState(player);
        });
    }

    private static async Task<(bool Ok, string Text)> SetFlame(int level)
    {
        if (!WorldProgress.Available || WorldProgress.Flame is not { } flame) return (false, "No world with a Mother Flame is running.");
        var max = flame.Max ?? 20;
        if (level < 1 || level > max) return (false, $"The Flame level is 1 to {max}.");
        if (level == flame.Level) return (true, $"The Flame level is {level} already.");
        try
        {
            var change = await WorldProgress.SetFlameLevel(level);
            return change.Ok
                ? (true, $"Flame level {change.Before} -> {change.After}.")
                : (false, $"The game refused: {change.Status}");
        }
        catch (Exception e)
        {
            return (false, $"The Flame level was not changed: {e.Message}");
        }
    }
}
