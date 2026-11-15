// SPDX-License-Identifier: MIT
using Kiln;

namespace MoreFlameAltars;

/// <summary>Settings, in the mod's config file. The game's table is changed once, before the game starts, so a change
/// applies at the next start; with beyondSixteen on (the hook), a change to the Flame Altar settings applies at once.</summary>
public sealed class Settings
{
    [Comment("Flame Altars a world may have at every Flame level: at least this many. The game's own numbers grow with the " +
             "Flame level: 1, 2, 4, 6, 8, 10, 11, 12, 13, 14, 15, then 16 from Flame level 11 on. A level where the game " +
             "already allows more keeps the game's number. At most 16, or 128 with beyondSixteen."),
     Range(1, 128), RequiresRestart]
    public int Altars { get; set; } = 16;

    [Comment("Flame Altars added to the game's own number at every Flame level, up to 16 (128 with beyondSixteen). With " +
             "Altars at 1 and this at 2, Flame level 0 allows 3, a new world (level 1) allows 4, level 2 allows 6: the game's progression " +
             "with a little more room. 0 adds none."),
     Range(0, 127), RequiresRestart]
    public int ExtraAltars { get; set; }

    [Comment("EXPERIMENTAL, may crash your game. Allow more than 16 Flame Altars (up to 128). The game is built for 16 " +
             "bases; past 16 it was never tested by its makers, and reports say other mods' 'unlimited altars' crash it " +
             "past about 36. With mergeBases off, altars 17 and up only let you build (no comfort, base chests, survivor " +
             "beds, animals, journal entry). Every player needs the mod. Your world, your call."),
     RequiresRestart]
    public bool BeyondSixteen { get; set; }

    [Comment("EXPERIMENTAL, with beyondSixteen. When the world has more than 16 altar zones, the mod makes all altars " +
             "ONE base (XHL's technique), so altars past 16 get comfort, base chests and the rest too. The price: one " +
             "base in the journal, every base chest shared by all altars, one base's comfort and beds. Off: the game's 16 " +
             "bases, altars past 16 build-only. Not tested in a real game yet."),
     RequiresRestart]
    public bool MergeBases { get; set; } = true;

    [Comment("Water sources a world may have at every Flame level: at least this many. The game's own numbers: 1 up to " +
             "Flame level 3, then 5, 10, 20, 30, 40, and 50 from Flame level 9 on. 0 leaves the game's numbers."),
     Range(0, 50), RequiresRestart]
    public int WaterSources { get; set; }

    [Comment("Water sources added to the game's own number at every Flame level, up to 50. 0 adds none."),
     Range(0, 49), RequiresRestart]
    public int ExtraWaterSources { get; set; }
}

/// <summary>Raises the game's lists of Flame Altars and water sources per Flame level (keen::BalancingTable,
/// altarsPerFlameLevel and waterSourcesPerFlameLevel) before the game reads them. The game reads that table once, at its
/// start, so the change is made in the boot phase.</summary>
public sealed partial class MoreFlameAltarsMod : Mod
{
    /// <summary>The game's balancing table: the one resource of its type.</summary>
    private static readonly Guid BalancingTable = Guid.Parse("82706b40-61b1-4b8f-8b23-dcec6971bda1");

    /// <summary>The highest number the game's own lists hold: the game's code caps altars at 16, and 50 water sources is
    /// the most its table gives. The mod never goes past either, except altars with beyondSixteen (up to
    /// <see cref="AltarHardCap"/>, see AltarLimitHook.cs).</summary>
    internal const int AltarCap = 16, WaterCap = 50, AltarHardCap = 128;

    internal static int CapOf(Settings s) => s.BeyondSixteen ? AltarHardCap : AltarCap;

    public override void OnBoot(BootContext boot)
    {
        var s = Config.Register<Settings>().Value;
        var block = Patch(s);
        if (block is null)
        {
            Log.Info("More Flame Altars: every setting leaves the game's numbers, nothing to change");
            return;
        }
        var staged = BootDefinitions.For(boot).Define("altars", block);
        if (!staged.Ok) throw new InvalidOperationException($"More Flame Altars: the change was not accepted: {staged.Reason}");
        Log.Info($"More Flame Altars: {Describe(s)}, from this start on");
    }

    public override void OnLoad()
    {
        OpenChannel();
        _observedSettings = Saved(Cfg());
        Context.Tick.Pre += CheckSettings;
        if (Ui.Available) OpenWindow();
        if (Cfg().Value.BeyondSixteen)
        {
            if (AltarLimitHook.Install(HookRules(), out var why))
                Log.Info($"More Flame Altars: altars above 16 are on (up to {AltarHardCap}, experimental)" +
                         (Cfg().Value.MergeBases ? "" : ": altars 17 and up are build-only"));
            else
                Log.Warn($"More Flame Altars: altars above 16 could not be switched on ({why}); the game's limit of 16 stays");
        }
        // merging needs the limit hook too: without it the server tells players it has no more than 16 (no merge)
        _mergeWanted = Cfg().Value.BeyondSixteen && Cfg().Value.MergeBases && AltarLimitHook.Installed;
        if (_mergeWanted)
        {
            if (BaseMergeHook.Install(out var why))
                Log.Info("More Flame Altars: more than 16 altar zones become one base (mergeBases, experimental)");
            else
                Log.Warn($"More Flame Altars: mergeBases could not be switched on ({why}); altars 17 and up stay build-only");
            BaseMergeHook.SetEnabled(BaseMergeHook.Installed && Client.IsDedicatedServer);
        }
        if (Patch(Cfg().Value) is null) return;  // OnBoot said so
        var state = Mods.BootState(Context.Info.Id);
        if (state.State == BootModState.Committed)
            Log.Info("More Flame Altars: the game started with the raised lists");
        else
            Log.Warn($"More Flame Altars: not active in this start ({state.State}" +
                     (state.Issues.Count > 0 ? $": {state.Issues[0].Reason}" : "") +
                     "). It changes the game's data before the game starts, so it needs a restart after it is switched on " +
                     "or after its settings change.");
    }

    /// <summary>The hook's rules: this start's table was made with the running settings (what OnBoot read); the answer
    /// follows the settings as they are saved now.</summary>
    private AltarLimitHook.Rules HookRules() =>
        new(AltarRule.Of(Cfg().Value), AltarRule.Of(SettingIndex.From(Saved(Cfg()))));

    /// <summary>After the settings changed (tick thread): with the hook in, or beyondSixteen just switched on, the altar
    /// settings apply at once, here and in every joined Kiln client. Says what applies when.</summary>
    private int[]? _observedSettings;
    private long _nextSettingsCheck;

    // Kiln emits no event when TrySet restores a restart entry to its running value: look at the saved settings once a
    // second (a server, or a game that hosts; a joined game follows its server).
    private void CheckSettings(in TickInfo tick)
    {
        var now = Environment.TickCount64;
        if (_saving || now < _nextSettingsCheck || !IsServerSide) return;
        _nextSettingsCheck = now + 1000;
        // a server, or a game that hosts its world, merges by its own settings (a joined game: by the server's, OnLimits)
        BaseMergeHook.SetEnabled(BaseMergeHook.Installed && _mergeWanted);
        if (_observedSettings is { } old && old.SequenceEqual(Saved(Cfg()))) return;
        ApplyNow();
    }

    /// <summary>A dedicated server, or a game that hosts its world: the side whose settings decide the limits.</summary>
    private static bool IsServerSide => ServerSide();
    private static Func<bool> ServerSide = () => Client.IsDedicatedServer || Client.IsHosting;  // the offline checks set it

    private string ApplyNow()
    {
        _observedSettings = Saved(Cfg());
        var saved = SettingIndex.From(_observedSettings);
        // kept up to date while this game is joined too, so hosting a world later merges by the settings as they are now
        _mergeWanted = saved.BeyondSixteen && saved.MergeBases && AltarLimitHook.Installed;
        if (!IsServerSide) return "A game that joins a server follows the server's numbers.";
        var running = Cfg().Value;
        var waterLater = saved.WaterSources != running.WaterSources || saved.ExtraWaterSources != running.ExtraWaterSources;
        string? failed = null;
        // a failure is in the text, which the window's request and /altars set log and answer
        if (!AltarLimitHook.Installed && saved.BeyondSixteen && !AltarLimitHook.Install(HookRules(), out var why)) failed = why;
        var merge = ApplyMerge(saved.BeyondSixteen && saved.MergeBases && AltarLimitHook.Installed);
        if (!AltarLimitHook.Installed)
            return (failed is null ? AppliesWhen() : $"Altars above 16 could not be switched on now ({failed}). {AppliesWhen()}") + merge;
        AltarLimitHook.SetRules(HookRules());
        var limits = LimitsMessage();
        var sent = limits.ByLevel.Length > 0 ? _ch?.SendToAll(limits) ?? 0 : 0;
        var altarsOnly = new Settings
        {
            Altars = saved.Altars, ExtraAltars = saved.ExtraAltars, BeyondSixteen = saved.BeyondSixteen, MergeBases = saved.MergeBases,
        };
        Log.Info($"More Flame Altars: the Flame Altar limit now: {Describe(altarsOnly)}" + (sent > 0 ? $"; sent to {sent} player(s)" : ""));
        return "The Flame Altar limit applies now." + (waterLater ? " Water sources: " + AppliesWhen() : "") + merge;
    }

    /// <summary>Whether this server (or hosted world) merges bases past 16 zones: the saved settings' wish, set on the
    /// tick thread (CheckSettings applies it once a second).</summary>
    private static volatile bool _mergeWanted;

    /// <summary>Server side: mergeBases as saved, now. Installs the hooks the first time. A sentence for the answer when
    /// something is worth saying.</summary>
    private static string ApplyMerge(bool wanted)
    {
        _mergeWanted = wanted;
        if (!wanted)
        {
            var was = BaseMergeHook.Enabled;
            BaseMergeHook.SetEnabled(false);
            return was ? " Bases are no longer merged: the game's 16 bases return at its next rebuild of them." : "";
        }
        if (!BaseMergeHook.Installed && !BaseMergeHook.Install(out var why))
            return $" mergeBases could not be switched on ({why}): altars 17 and up stay build-only.";
        var on = !BaseMergeHook.Enabled;
        BaseMergeHook.SetEnabled(true);
        return on ? " With more than 16 altar zones, all altars are one base from the game's next rebuild of its bases." : "";
    }

    public override void OnUnload() => BaseMergeHook.Release();

    /// <summary>The boot patch for these settings, or null when they leave every number as the game has it.</summary>
    internal static ResourcePatchBlock? Patch(Settings s)
    {
        var block = new ResourcePatchBlock { Resource = BalancingTable, Type = "keen::BalancingTable" };
        if (Raise(s.Altars, s.ExtraAltars, CapOf(s)) is { } altars) block.Set["altarsPerFlameLevel[*]"] = altars;
        if (Raise(s.WaterSources, s.ExtraWaterSources, WaterCap) is { } water) block.Set["waterSourcesPerFlameLevel[*]"] = water;
        return block.Set.Count == 0 ? null : block;
    }

    /// <summary>The game's value plus <paramref name="extra"/>, at least <paramref name="atLeast"/>, at most
    /// <paramref name="cap"/>, and never below the game's own value. Null when it changes nothing.</summary>
    private static ResourceValue? Raise(int atLeast, int extra, int cap)
    {
        atLeast = Math.Clamp(atLeast, 0, cap);
        extra = Math.Clamp(extra, 0, cap);
        if (atLeast <= 1 && extra == 0) return null;  // every level already allows at least 1
        return new ResourceValue
        {
            Add = extra == 0 ? null : extra,
            Min = atLeast <= 1 ? null : atLeast,
            Max = cap,
            RaiseOnly = true,
        };
    }

    internal static string Describe(Settings s)
    {
        var parts = new List<string>();
        if (Raise(s.Altars, s.ExtraAltars, CapOf(s)) is not null)
            parts.Add(Words("Flame Altars", s.Altars, s.ExtraAltars, CapOf(s)) +
                      (!s.BeyondSixteen ? "" : s.MergeBases ? " (above 16: one merged base, experimental)" : " (above 16: build-only, experimental)"));
        if (Raise(s.WaterSources, s.ExtraWaterSources, WaterCap) is not null)
            parts.Add(Words("water sources", s.WaterSources, s.ExtraWaterSources, WaterCap));
        return string.Join("; ", parts);
    }

    private static string Words(string what, int atLeast, int extra, int cap)
    {
        if (Math.Min(atLeast, cap) >= cap) return $"{cap} {what} at every Flame level";
        var rule = extra > 0 ? $"the game's number + {extra}" : "the game's number";
        if (atLeast > 1) rule += $", but at least {atLeast}";
        return $"{what}: {rule} at every Flame level, at most {cap}";
    }
}
