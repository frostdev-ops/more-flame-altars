// SPDX-License-Identifier: MIT
// In-server checks with no player: `kiln test more-flame-altars-labtest`. More Flame Altars changes the game's balancing
// table before the game reads it (Kiln's boot phase), so these read what the running game holds and compare it with what
// the mod's settings ask for. Whether a player may then place that many altars needs a player: README.md, "What is checked".
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace MoreFlameAltars.LabTest;

public sealed class LabTestMod : Mod
{
    public override void OnLoad() { }
}

public sealed class Tests
{
    private const string ModId = "more-flame-altars";
    private static readonly AssetKey Balancing = new(new Guid("82706b40-61b1-4b8f-8b23-dcec6971bda1"), "keen::BalancingTable");

    // The game's own lists, Flame level 0 to 20 (server build 26430253, game data 1287369).
    private static readonly int[] GameAltars = [1, 2, 4, 6, 8, 10, 11, 12, 13, 14, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16];
    private static readonly int[] GameWater = [1, 1, 1, 1, 5, 10, 20, 30, 40, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50];

    /// <summary>The registered running settings, including when file edits are waiting for a restart.</summary>
    private static (int Altars, int ExtraAltars, int Water, int ExtraWater, bool Beyond) S => ReadSettings();

    private static (int, int, int, int, bool) ReadSettings()
    {
        var cfg = Assert.NotNull(Config.Find(ModId), "More Flame Altars registered its config");
        return (Convert.ToInt32(cfg.Get("altars")), Convert.ToInt32(cfg.Get("extraAltars")),
            Convert.ToInt32(cfg.Get("waterSources")), Convert.ToInt32(cfg.Get("extraWaterSources")),
            Convert.ToBoolean(cfg.Get("beyondSixteen")));
    }

    /// <summary>What the mod makes of the game's value: plus extra, at least atLeast, at most cap, never below the game's.</summary>
    private static int Expected(int game, int atLeast, int extra, int cap) =>
        Math.Max(game, Math.Clamp(Math.Max(game + extra, atLeast), 0, cap));

    [KilnTest]
    public void The_mods_boot_patch_is_in_this_start()
    {
        Assert.True(Capabilities.Is("mods.boot.resource_patches"), $"mods.boot.resource_patches is on ({Capabilities.Why("mods.boot.resource_patches")})");
        var state = Mods.BootState(ModId);
        Assert.True(state.State == BootModState.Committed,
            $"{ModId} is in the published boot generation (state {state.State}, definitions [{string.Join(", ", state.Definitions)}], " +
            $"issues [{string.Join("; ", state.Issues.Select(i => $"{i.Code}: {i.Reason}"))}])");
        Assert.True(state.Issues.Count == 0, $"no boot issue ({string.Join("; ", state.Issues.Select(i => $"{i.Code}: {i.Reason}"))})");
    }

    [KilnTest]
    public void The_games_tables_hold_what_the_settings_ask_for()
    {
        Assert.True(Assets.Available, "kiln.assets is on");
        var table = Assert.NotNull(Assets.Reflect(Balancing), "the game's balancing table");
        foreach (var (field, game, atLeast, extra, cap) in new[]
                 {
                     ("altarsPerFlameLevel", GameAltars, S.Altars, S.ExtraAltars, S.Beyond ? 32 : 16),
                     ("waterSourcesPerFlameLevel", GameWater, S.Water, S.ExtraWater, 50),
                 })
        {
            var levels = table.Count(field);
            Assert.Equal(game.Length, levels, $"{field}: one number per Flame level, as on build 26430253");
            var have = new List<int>();
            var want = new List<int>();
            for (var level = 0; level < levels; level++)
            {
                have.Add(Convert.ToInt32(table[$"{field}[{level}]"]));
                want.Add(Expected(game[level], atLeast, extra, cap));
                Assert.True(table.HeldBy($"{field}[{level}]") is null, $"{field}[{level}] is the game's own data, not a runtime override");
            }
            Assert.True(have.SequenceEqual(want), $"{field} (settings {atLeast} / +{extra}): have {string.Join(",", have)}, want {string.Join(",", want)}");
        }
    }

    /// <summary>What the limit function answers at <paramref name="level"/> for table value <paramref name="table"/>: the
    /// game's min(value, 16) without beyondSixteen; with it the mod's detour (AltarLimitHook.Answer): the settings' rule on
    /// the game's number when the table holds the game's number or the rule's, else on the table's value, at most 32.</summary>
    private static uint Expected(uint level, byte table)
    {
        // the hook is in when the start had beyondSixteen, or once it is switched on while the server runs; the table was
        // made with the running settings, the answer follows the saved ones (a change made while the server runs)
        var (running, saved) = (S, Saved());
        if (!running.Beyond && !saved.Beyond) return Math.Min(table, 16u);
        var game = GameAltars[(int)Math.Min(level, (uint)GameAltars.Length - 1)];
        int Rule(int g, int atLeast, int extra, bool beyond) =>
            Math.Max(g, Math.Clamp(Math.Max(g + Math.Max(0, extra), atLeast), 0, beyond ? 32 : 16));
        var from = table == game || Rule(game, running.Altars, running.ExtraAltars, running.Beyond) == table ? game : table;
        return (uint)Math.Clamp(Rule(from, saved.Altars, saved.ExtraAltars, saved.Beyond), 1, saved.Beyond ? 32 : 16);
    }

    /// <summary>The altar settings as the file holds them (a change waiting for a restart included).</summary>
    private static (int Altars, int ExtraAltars, bool Beyond) Saved()
    {
        var cfg = Assert.NotNull(Config.Find(ModId), "More Flame Altars registered its config");
        // the file value comes as the file's JSON value: read it through its text, as the mod does
        string File(string path) => Convert.ToString(cfg.Entries.FirstOrDefault(e => e.Path == path)?.FileValue ?? cfg.Get(path),
            System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return (int.Parse(File("altars")), int.Parse(File("extraAltars")), bool.Parse(File("beyondSixteen")));
    }

    /// <summary>The game's own altar-limit function, called with a made-up balancing table: with beyondSixteen it answers
    /// the mod's detour (up to 32), without it the game's min(value, 16). Proves the detour on the real function; whether
    /// the game then lets a player place the altar needs a player.</summary>
    [KilnTest]
    public unsafe void The_games_limit_function_answers_the_mods_cap()
    {
        // the mod loads its symbol only with beyondSixteen on; otherwise find the game's function by the same pattern
        var fn = Symbols.TryGet("more-flame-altars/AltarLimit", out var at) ? at
            : Symbols.Find("48 8B 41 20 48 85 C0 75 06 B8 01 00 00 00 C3 48 05 ?? ?? ?? ?? 48 63 08 85 C9 75 13 33 C9 8B C2 0F B6 04 08 B9 10 00 00 00 3A C1 0F 47 C1 C3");
        var table = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x200);
        var registry = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x40);
        try
        {
            *(nint*)(registry + 0x20) = (nint)table;
            var blob = table + 0x1b8;
            *(int*)blob = 0x10;          // self-relative displacement of the bytes
            *(uint*)(blob + 4) = 4;      // count
            byte[] values = [3, 20, 40, 16];
            for (var i = 0; i < values.Length; i++) blob[0x10 + i] = values[i];
            var limit = (delegate* unmanaged<nint, uint, uint>)fn;
            for (uint level = 0; level < values.Length; level++)
                Assert.Equal(Expected(level, values[level]), limit((nint)registry, level), $"limit at level {level} (table {values[level]}, running {S}, saved {Saved()})");
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(table);
            System.Runtime.InteropServices.NativeMemory.Free(registry);
        }
    }

    [KilnTest]
    public void Kiln_reports_the_new_limits()
    {
        Assert.True(WorldProgress.Available, "kiln.quests' world progress");
        var table = Assert.NotNull(Assets.Reflect(Balancing));
        var seen = 0;
        for (var level = 0; level < table.Count("altarsPerFlameLevel"); level++)
        {
            if (WorldProgress.LimitsAt(level) is not { } limits) continue;
            seen++;
            Assert.Equal(Convert.ToInt32(table[$"altarsPerFlameLevel[{level}]"]), limits.Altars, $"WorldProgress.LimitsAt({level}).Altars");
            Assert.Equal(Convert.ToInt32(table[$"waterSourcesPerFlameLevel[{level}]"]), limits.WaterSources, $"WorldProgress.LimitsAt({level}).WaterSources");
        }
        Assert.True(seen > 0, "WorldProgress.LimitsAt answered for at least one Flame level");
    }
}
