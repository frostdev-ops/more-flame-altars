// SPDX-License-Identifier: MIT
// Altars above 16 (experimental, off by default). The game's limit function (sigdb/<side>.json: AltarLimit) answers
// min(altarsPerFlameLevel[flameLevel], 16) to every check that limits Flame Altars: on the client exe (build 26430625) its
// 11 callers are the build cursor (0x29aebc), the hosted world's placement checks (0x2d4d96, 0x2da6b5) and the UI's
// numbers, so one detour covers a world a player hosts too. With the hook in, the mod answers from its settings as they
// are saved now, so a change made in /altars applies at once: the game's own number for the level (the mod's list of
// them), raised by the rule, at most 32. A joined Kiln client answers with the server's numbers, which the server sends
// when it joins and after a change (AltarsLimits), so both sides agree.
//
// What it does not change: the game's base system tracks 16 build zones and 16 bases. Altars 17 and up are build-only
// (no comfort, base chests, survivor beds, animals, journal entry), and which altars those are can change after a
// reload (README.md, "More than 16 altars").
using Kiln;

namespace MoreFlameAltars;

/// <summary>Server to client, when a Kiln client with the mod joins and after a settings change: the altar limit at each
/// Flame level as the server answers it (above 16 only with beyondSixteen on the server).</summary>
public sealed record AltarsLimits(bool Beyond, byte[] ByLevel) : INetMessage<AltarsLimits>
{
    public void Encode(NetWriter w) => w.Bool(Beyond).Bytes(ByLevel);
    public static AltarsLimits Decode(ref NetReader r)
    {
        var beyond = r.Bool();
        var by = r.Bytes(64);
        var cap = beyond ? MoreFlameAltarsMod.AltarHardCap : MoreFlameAltarsMod.AltarCap;
        if (by.Length == 0 || by.Any(v => v < 1 || v > cap))
            throw new NetFormatException("Invalid server altar limits.");
        return new(beyond, by);
    }
}

/// <summary>How the limit is made from the game's number: at least <see cref="AtLeast"/>, the game's number plus
/// <see cref="Extra"/>, at most <see cref="Cap"/>, never below the game's number (the boot patch's rule).</summary>
internal sealed record AltarRule(int AtLeast, int Extra, int Cap)
{
    public static AltarRule Of(Settings s) => new(s.Altars, s.ExtraAltars, MoreFlameAltarsMod.CapOf(s));

    public int Apply(int game) => MoreFlameAltarsMod.Effective(game, AtLeast, Extra, Cap);
}

internal static class AltarLimitHook
{
    private const string Symbol = "more-flame-altars/AltarLimit";
    private const int TableOffset = 0x1b8;  // altarsPerFlameLevel in keen::BalancingTable (the symbol's checked hole)

    /// <summary>The rule the boot patch used for this start's table (<see cref="Boot"/>) and the one to answer with now
    /// (<see cref="Live"/>).</summary>
    internal sealed record Rules(AltarRule Boot, AltarRule Live);

    private static HookHandle? _hook;
    private static volatile Rules _rules = new(new AltarRule(1, 0, 16), new AltarRule(1, 0, 16));
    private static volatile byte[]? _server;     // a joined client: the server's limit at each Flame level
    private static long _calls;

    public static bool Installed => _hook is not null;
    public static long Calls => Interlocked.Read(ref _calls);
    public static AltarRule Live => _rules.Live;

    /// <summary>Installs the detour once (tick thread or OnLoad). False with the reason when Kiln cannot.</summary>
    public static bool Install(Rules rules, out string why)
    {
        _rules = rules;
        why = "";
        if (_hook is not null) return true;
        try
        {
            if (!Hooks.Available) { why = "Kiln's hooks are not available on this game build"; return false; }
            var s = Symbols.LoadShipped();
            if (s.SymbolsOk < 1) { why = "the game's altar-limit function was not found on this game build"; return false; }
            if (Constants.Get("more-flame-altars/altars.table") != TableOffset)
            {
                why = "the altar table moved on this game build";
                return false;
            }
            _hook = Hooks.Detour(Symbol, OnLimit);
            return true;
        }
        catch (Exception e)
        {
            why = e.Message;
            return false;
        }
    }

    public static void SetRules(Rules rules) => _rules = rules;

    /// <summary>A joined client: the server's numbers (null on leave).</summary>
    public static void SetServer(byte[]? byLevel) => _server = byLevel;

    /// <summary>The limit at <paramref name="level"/> when this start's table holds <paramref name="table"/> there. The
    /// table is the game's number with the boot rule applied, or the game's own number when the boot patch did not run;
    /// either way the live rule is applied to the game's number. A table that matches neither (a game update changed the
    /// game's numbers, or another mod the table) is taken as the game would answer it, min(table, 16): the answer never
    /// drops below what the game itself allows, and never goes past the live rule's cap.</summary>
    internal static int Answer(Rules rules, uint level, int table)
    {
        var game = MoreFlameAltarsMod.GameAltarsAt(level);
        var from = table == game || rules.Boot.Apply(game) == table ? game : Math.Min(table, MoreFlameAltarsMod.AltarCap);
        return Math.Max(1, rules.Live.Apply(from));
    }

    /// <summary>What the hook answers now at <paramref name="level"/> for a table value read on the tick thread.</summary>
    internal static int Answer(uint level, int table) => Answer(_rules, level, table);

    // Runs on whatever thread asks the game for the limit: no allocation, no Kiln API but guarded reads.
    private static nuint OnLimit(HookCall call)
    {
        Interlocked.Increment(ref _calls);
        var level = (uint)call[1];
        if (_server is { } server)
            return server.Length == 0 ? call.CallOriginal() : server[(int)Math.Min(level, (uint)server.Length - 1)];
        var rules = _rules;
        // the game's own read: table = registry+0x20; null answers 1; the bytes start at table+0x1b8 plus its displacement
        if (!Memory.TryRead<nuint>(call[0] + 0x20, out var table) || table == 0) return call.CallOriginal();
        var blob = table + TableOffset;
        if (!Memory.TryRead<int>(blob, out var disp) || !Memory.TryRead<uint>(blob + 4, out var count) || count == 0)
            return call.CallOriginal();
        var data = disp == 0 ? blob : (nuint)((nint)blob + disp);
        if (!Memory.TryRead<byte>(data + Math.Min(level, count - 1), out var value)) return call.CallOriginal();
        return (nuint)Answer(rules, level, value);
    }
}
