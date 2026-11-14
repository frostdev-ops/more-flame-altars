// SPDX-License-Identifier: MIT
using Kiln;

namespace MoreFlameAltars;

/// <summary>Settings, in the mod's config file. The game's table is changed once, before the game starts, so a change
/// applies at the next start; with beyondSixteen on (the hook), a change to the Flame Altar settings applies at once.</summary>
public sealed class Settings
{
    [Comment("Flame Altars a world may have at every Flame level: at least this many. The game's own numbers grow with the " +
             "Flame level: 1, 2, 4, 6, 8, 10, 11, 12, 13, 14, 15, then 16 from Flame level 11 on. A level where the game " +
             "already allows more keeps the game's number. At most 16, or 32 with beyondSixteen."),
     Range(1, 32), RequiresRestart]
    public int Altars { get; set; } = 16;

    [Comment("Flame Altars added to the game's own number at every Flame level, up to 16 (32 with beyondSixteen). With " +
             "Altars at 1 and this at 2, Flame level 0 allows 3, a new world (level 1) allows 4, level 2 allows 6: the game's progression " +
             "with a little more room. 0 adds none."),
     Range(0, 31), RequiresRestart]
    public int ExtraAltars { get; set; }

    [Comment("EXPERIMENTAL. Allow more than 16 Flame Altars (up to 32). The game's base system keeps track of 16 bases: " +
             "altars 17 and up only let you build. They get no comfort, no crafting from base chests, no survivor beds, no " +
             "animals and no journal entry, and which altars those are can change after a reload. Every player needs the " +
             "mod. Leave it off unless you want build-only altars."),
     RequiresRestart]
    public bool BeyondSixteen { get; set; }

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
    internal const int AltarCap = 16, WaterCap = 50, AltarHardCap = 32;

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
                Log.Info($"More Flame Altars: altars above 16 are on (up to {AltarHardCap}, experimental): altars 17 and up are build-only");
            else
                Log.Warn($"More Flame Altars: altars above 16 could not be switched on ({why}); the game's limit of 16 stays");
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
        if (_observedSettings is { } old && old.SequenceEqual(Saved(Cfg()))) return;
        ApplyNow();
    }

    /// <summary>A dedicated server, or a game that hosts its world: the side whose settings decide the limits.</summary>
    private static bool IsServerSide => ServerSide();
    private static Func<bool> ServerSide = () => Client.IsDedicatedServer || Client.IsHosting;  // the offline checks set it

    private string ApplyNow()
    {
        _observedSettings = Saved(Cfg());
        if (!IsServerSide) return "A game that joins a server follows the server's numbers.";
        var saved = SettingIndex.From(_observedSettings);
        var running = Cfg().Value;
        var waterLater = saved.WaterSources != running.WaterSources || saved.ExtraWaterSources != running.ExtraWaterSources;
        string? failed = null;
        // a failure is in the text, which the window's request and /altars set log and answer
        if (!AltarLimitHook.Installed && saved.BeyondSixteen && !AltarLimitHook.Install(HookRules(), out var why)) failed = why;
        if (!AltarLimitHook.Installed)
            return failed is null ? AppliesWhen() : $"Altars above 16 could not be switched on now ({failed}). {AppliesWhen()}";
        AltarLimitHook.SetRules(HookRules());
        var limits = LimitsMessage();
        var sent = limits.ByLevel.Length > 0 ? _ch?.SendToAll(limits) ?? 0 : 0;
        var altarsOnly = new Settings { Altars = saved.Altars, ExtraAltars = saved.ExtraAltars, BeyondSixteen = saved.BeyondSixteen };
        Log.Info($"More Flame Altars: the Flame Altar limit now: {Describe(altarsOnly)}" + (sent > 0 ? $"; sent to {sent} player(s)" : ""));
        return "The Flame Altar limit applies now." + (waterLater ? " Water sources: " + AppliesWhen() : "");
    }

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
            parts.Add(Words("Flame Altars", s.Altars, s.ExtraAltars, CapOf(s)) + (s.BeyondSixteen ? " (above 16: build-only, experimental)" : ""));
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
