// SPDX-License-Identifier: MIT
// The messages between the server's part of the mod and the window in a player's game (channel "more-flame-altars:ui").
// Registered in the same order on both sides: AltarsState 0, AltarsRequest 1, AltarsResult 2, AltarsLimits 3. Channel
// version 4 since 0.4.0 (buildArea in the settings, the build area at each altar level in AltarsState); version 3 was
// 0.3.0 (mergeBases).
using Kiln;

namespace MoreFlameAltars;

/// <summary>The settings, in the order the messages carry them.</summary>
internal static class SettingIndex
{
    public const int Altars = 0, ExtraAltars = 1, WaterSources = 2, ExtraWaterSources = 3, BeyondSixteen = 4, MergeBases = 5,
        BuildArea = 6, Count = 7;

    public static readonly string[] Paths =
        ["altars", "extraAltars", "waterSources", "extraWaterSources", "beyondSixteen", "mergeBases", "buildArea"];

    /// <summary>The lowest and highest value of each setting, in <see cref="Paths"/> order.</summary>
    public static readonly int[] Minimum = [1, 0, 0, 0, 0, 0, 100],
        Maximum = [MoreFlameAltarsMod.AltarHardCap, MoreFlameAltarsMod.AltarHardCap - 1, MoreFlameAltarsMod.WaterCap,
                   MoreFlameAltarsMod.WaterCap - 1, 1, 1, MoreFlameAltarsMod.BuildAreaCap];

    /// <summary>The settings that apply at once once the altar-limit hook is in (the rest wait for the next start).</summary>
    public static readonly int[] AltarSettings = [Altars, ExtraAltars, BeyondSixteen, MergeBases];

    /// <summary>The on/off settings (0 or 1 in the messages, true or false in the file).</summary>
    public static bool IsSwitch(int i) => i is BeyondSixteen or MergeBases;

    public static int[] Of(Settings s) =>
        [s.Altars, s.ExtraAltars, s.WaterSources, s.ExtraWaterSources, s.BeyondSixteen ? 1 : 0, s.MergeBases ? 1 : 0, s.BuildArea];

    public static bool Validate(IReadOnlyList<int> values, out string error)
    {
        error = "";
        if (values.Count != Count) { error = "Wrong number of settings."; return false; }
        for (var i = 0; i < Count; i++)
            if (values[i] < Minimum[i] || values[i] > Maximum[i])
            {
                error = $"{Paths[i]} must be {Minimum[i]} to {Maximum[i]}.";
                return false;
            }
        return true;
    }

    public static Settings From(IReadOnlyList<int> v) => new()
    {
        Altars = v[Altars], ExtraAltars = v[ExtraAltars], WaterSources = v[WaterSources], ExtraWaterSources = v[ExtraWaterSources],
        BeyondSixteen = v[BeyondSixteen] != 0, MergeBases = v[MergeBases] != 0, BuildArea = v[BuildArea],
    };
}

/// <summary>Server to client: what the window shows. <see cref="Open"/> asks the window to open (the /altars command);
/// otherwise it only refreshes an open one.</summary>
/// <param name="Running">The settings the server started with (what the world uses now).</param>
/// <param name="Saved">The settings in the server's file (used from its next start).</param>
/// <param name="AltarsByLevel">Altars the world allows at each Flame level, as the server's game reads them now.</param>
/// <param name="WaterByLevel">Water sources the world allows at each Flame level.</param>
/// <param name="AreaByAltarLevel">One side of the build area, in whole metres, at altar level 1 and up, as the server's
/// game reads it now (empty when it cannot be read).</param>
public sealed record AltarsState(
    bool Open, bool CanManage, bool Hosted, bool Active, string Note,
    int FlameLevel, int FlameMax, int Altars, int AltarsAllowed, int Water, int WaterAllowed,
    int[] Running, int[] Saved, byte[] AltarsByLevel, byte[] WaterByLevel, int[] AreaByAltarLevel) : INetMessage<AltarsState>
{
    public void Encode(NetWriter w)
    {
        w.Bool(Open).Bool(CanManage).Bool(Hosted).Bool(Active).String(Note)
         .Int32(FlameLevel).Int32(FlameMax).Int32(Altars).Int32(AltarsAllowed).Int32(Water).Int32(WaterAllowed);
        foreach (var v in Running) w.Int32(v);
        foreach (var v in Saved) w.Int32(v);
        w.Bytes(AltarsByLevel).Bytes(WaterByLevel).Byte((byte)AreaByAltarLevel.Length);
        foreach (var v in AreaByAltarLevel) w.Int32(v);
    }

    public static AltarsState Decode(ref NetReader r)
    {
        bool open = r.Bool(), manage = r.Bool(), hosted = r.Bool(), active = r.Bool();
        var note = r.String(1024);
        int level = r.Int32(), max = r.Int32(), altars = r.Int32(), altarsAllowed = r.Int32(), water = r.Int32(), waterAllowed = r.Int32();
        var running = new int[SettingIndex.Count];
        for (var i = 0; i < running.Length; i++) running[i] = r.Int32();
        var saved = new int[SettingIndex.Count];
        for (var i = 0; i < saved.Length; i++) saved[i] = r.Int32();
        var altarsBy = r.Bytes(64);
        var waterBy = r.Bytes(64);
        var areaCount = r.Byte();
        if (areaCount > 16) throw new NetFormatException("Invalid altar window state.");
        var area = new int[areaCount];
        for (var i = 0; i < area.Length; i++) area[i] = r.Int32();
        if (altarsBy.Length != waterBy.Length || max is < 1 or > 255 || level < -1 || level > max ||
            area.Any(m => m is < 0 or > 100_000) ||
            !SettingIndex.Validate(running, out _) || !SettingIndex.Validate(saved, out _))
            throw new NetFormatException("Invalid altar window state.");
        return new(open, manage, hosted, active, note, level, max, altars, altarsAllowed, water, waterAllowed, running, saved,
            altarsBy, waterBy, area);
    }
}

public enum AltarsAction : byte
{
    /// <summary>Send the state again.</summary>
    Refresh = 0,
    /// <summary>Write the settings into the server's file (most apply at its next start; see the window).</summary>
    SaveSettings = 1,
    /// <summary>Set the world's Flame level.</summary>
    SetFlameLevel = 2,
}

/// <summary>Client to server: an action from the window.</summary>
public sealed record AltarsRequest(AltarsAction Action, int[] Settings, int FlameLevel) : INetMessage<AltarsRequest>
{
    public void Encode(NetWriter w)
    {
        w.Byte((byte)Action).Int32(FlameLevel);
        foreach (var v in Settings) w.Int32(v);
    }

    public static AltarsRequest Decode(ref NetReader r)
    {
        var action = (AltarsAction)r.Byte();
        var level = r.Int32();
        var settings = new int[SettingIndex.Count];
        for (var i = 0; i < settings.Length; i++) settings[i] = r.Int32();
        return new(action, settings, level);
    }
}

/// <summary>Server to client: how an action went, in words.</summary>
public sealed record AltarsResult(bool Ok, string Text) : INetMessage<AltarsResult>
{
    public void Encode(NetWriter w) => w.Bool(Ok).String(Text);
    public static AltarsResult Decode(ref NetReader r) => new(r.Bool(), r.String(1024));
}
