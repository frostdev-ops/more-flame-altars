// SPDX-License-Identifier: MIT
// Altars above 16 (experimental, off by default). The game's limit function (sigdb/<side>.json: AltarLimit) answers
// min(altarsPerFlameLevel[flameLevel], 16) to the server's placement checks and to the client's altar cursor. With
// beyondSixteen on, the mod replaces it: the same table read, with the mod's cap instead of 16. A joined Kiln client
// answers with the server's numbers, which the server sends when it joins (AltarsLimits), so both sides agree.
//
// What it does not change: the game's base system tracks 16 build zones and 16 bases. Altars 17 and up are build-only
// (no comfort, base chests, survivor beds, animals, journal entry), and which altars those are can change after a
// reload (README.md, "More than 16 altars").
using Kiln;

namespace MoreFlameAltars;

/// <summary>Server to client, when a Kiln client with the mod joins: the altar limit at each Flame level as the server
/// answers it (above 16 only with beyondSixteen on the server).</summary>
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

internal static class AltarLimitHook
{
    private const string Symbol = "more-flame-altars/AltarLimit";
    private const int TableOffset = 0x1b8;  // altarsPerFlameLevel in keen::BalancingTable (the symbol's checked hole)

    private static HookHandle? _hook;
    private static volatile int _cap = 16;
    private static volatile byte[]? _server;     // a joined client: the server's limit at each Flame level
    private static long _calls;

    public static bool Installed => _hook is not null;
    public static long Calls => Interlocked.Read(ref _calls);

    /// <summary>Installs the detour once (tick thread or OnLoad). False with the reason when Kiln cannot.</summary>
    public static bool Install(int cap, out string why)
    {
        _cap = cap;
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

    public static void SetCap(int cap) => _cap = cap;

    /// <summary>A joined client: the server's numbers (null on leave).</summary>
    public static void SetServer(byte[]? byLevel) => _server = byLevel;

    // Runs on whatever thread asks the game for the limit: no allocation, no Kiln API but guarded reads.
    private static nuint OnLimit(HookCall call)
    {
        Interlocked.Increment(ref _calls);
        var level = (uint)call[1];
        if (_server is { } server)
            return server.Length == 0 ? call.CallOriginal() : server[(int)Math.Min(level, (uint)server.Length - 1)];
        var cap = _cap;
        if (cap <= 16) return call.CallOriginal();
        // the game's own read: table = registry+0x20; null answers 1; the bytes start at table+0x1b8 plus its displacement
        if (!Memory.TryRead<nuint>(call[0] + 0x20, out var table) || table == 0) return call.CallOriginal();
        var blob = table + TableOffset;
        if (!Memory.TryRead<int>(blob, out var disp) || !Memory.TryRead<uint>(blob + 4, out var count) || count == 0)
            return call.CallOriginal();
        var data = disp == 0 ? blob : (nuint)((nint)blob + disp);
        if (!Memory.TryRead<byte>(data + Math.Min(level, count - 1), out var value)) return call.CallOriginal();
        return (nuint)Math.Min((int)value, cap);
    }
}
