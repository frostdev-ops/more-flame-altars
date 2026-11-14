// SPDX-License-Identifier: MIT
// The window in a player's game: opened by the server's answer to /altars. It shows the world's Flame level, altars and
// water sources, the numbers at every Flame level, and the mod's settings; an admin can change the settings (saved on the
// server, used from its next start) and the world's Flame level. It draws on the render thread from the last state the
// server sent; every change goes to the server, which checks the permission and answers.
using Kiln;

namespace MoreFlameAltars;

public sealed partial class MoreFlameAltarsMod
{
    // The game's own lists, Flame level 0 to 20 (game data 1287369): the "game" column and the preview of new settings.
    // A game update with another number of levels hides both.
    private static readonly int[] GameAltars = [1, 2, 4, 6, 8, 10, 11, 12, 13, 14, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16];
    private static readonly int[] GameWater = [1, 1, 1, 1, 5, 10, 20, 30, 40, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50];

    /// <summary>The game's own number of Flame Altars at <paramref name="level"/> (the last one above the list).</summary>
    internal static int GameAltarsAt(uint level) => GameAltars[(int)Math.Min(level, (uint)GameAltars.Length - 1)];

    private UiWindow? _window;
    private volatile AltarsState? _state;
    private int[]? _edit;                      // the settings being edited (render thread)
    private int _flameEdit = -1;
    private bool _flameSure;
    private volatile string _result = "";
    private volatile bool _resultOk = true;
    private int[]? _booted;                    // the settings this game started with (its own table)
    private readonly object _uiLock = new();
    private bool _resetEdits;
    private uint _uiSession;

    private void OpenWindow()
    {
        _booted = SettingIndex.Of(Cfg().Value);
        _window = Ui.Window("Flame Altars", Draw, new UiWindowOptions { StartOpen = false, Width = 560, Height = 640 });
    }

    /// <summary>Client: a new state from the server (main thread).</summary>
    private void OnState(AltarsState s)
    {
        lock (_uiLock) ApplyState(s);
    }

    private void ApplyState(AltarsState s)
    {
        var first = _state is null || s.Open || _resetEdits;
        _uiSession = Client.ClientWorldSession;
        _state = s;
        if (first)
        {
            _resetEdits = false;
            _edit = (int[])s.Saved.Clone();
            _flameEdit = s.FlameLevel;
            _flameSure = false;
        }
        if (s.Open && _window is not null)
        {
            _result = "";
            _window.Show();
        }
    }

    private void OnResult(AltarsResult r)
    {
        lock (_uiLock) ApplyResult(r);
    }

    private void ApplyResult(AltarsResult r)
    {
        _resultOk = r.Ok;
        _result = r.Text;
        if (r.Ok) _resetEdits = true;  // reset only when the next state brings the new saved values
        if (!_window?.Open ?? true) Hud.Toast(r.Text, r.Ok ? ToastKind.Success : ToastKind.Warning);
    }

    private void ResetWindow()
    {
        lock (_uiLock)
        {
            _window?.Hide();
            _state = null;
            _edit = null;
            _flameEdit = -1;
            _flameSure = false;
            _resetEdits = false;
            _result = "";
        }
    }

    private void Send(AltarsAction action, int[]? settings = null, int level = 0)
    {
        var request = new AltarsRequest(action, settings ?? new int[SettingIndex.Count], level);
        if (Client.IsHosting && !Client.LocalPlayer.IsNone)
        {
            var player = Client.LocalPlayer;
            var session = Client.ServerWorldSession;
            Context.Scheduler.Post(() =>
            {
                if (IsLocalHost(player) && session == Client.ServerWorldSession) OnRequest(request, player);
            });
            return;
        }
        if (_ch is null || !_ch.SendToServer(request))
        {
            _resultOk = false;
            _result = "The server did not take the request (not connected to a server with More Flame Altars).";
        }
    }

    private void Draw(UiDraw ui)
    {
        lock (_uiLock)
        {
            if (!Client.InGame || _uiSession != Client.ClientWorldSession) { ResetWindow(); return; }
            DrawWindow(ui);
        }
    }

    private void DrawWindow(UiDraw ui)
    {
        var s = _state;
        if (s is null)
        {
            ui.Text("Waiting for the server. Type /altars in the chat.");
            return;
        }
        var edit = _edit ??= (int[])s.Saved.Clone();

        // ---- this world
        ui.Separator("This world");
        if (s.FlameLevel < 0) ui.Text("No world is running.");
        else
        {
            ui.Text($"Flame level {s.FlameLevel} of {s.FlameMax}");
            ui.Text($"Flame Altars: {s.Altars} of {s.AltarsAllowed}");
            ui.Text($"Water sources: {s.Water} of {s.WaterAllowed}");
            var next = s.FlameLevel + 1;
            if (next < s.AltarsByLevel.Length)
                ui.TextDisabled($"At Flame level {next}: {s.AltarsByLevel[next]} altars, {s.WaterByLevel[next]} water sources");
        }
        if (!s.Active) ui.Text(Color.Orange, "The mod's numbers are not active in this start.");
        if (s.Note.Length > 0) ui.TextWrapped(s.Note);
        // with the hook in, this game follows the server's altar numbers: only the water sources can differ then
        if (_booted is { } mine && !Client.IsHosting && (AltarLimitHook.Installed
                ? mine[SettingIndex.WaterSources] != s.Running[SettingIndex.WaterSources] || mine[SettingIndex.ExtraWaterSources] != s.Running[SettingIndex.ExtraWaterSources]
                : !mine.SequenceEqual(s.Running)))
            ui.TextWrapped("Your game started with other settings than the server's (yours: " + Describe(SettingIndex.From(mine)) +
                           "). Your game may then show or allow other numbers than the server. Set the same settings in your game " +
                           "(Kiln menu > Mods > More Flame Altars > Config) and restart it.");

        // ---- every Flame level
        if (ui.CollapsingHeader("Every Flame level") && ui.BeginTable("levels", "Flame level", "Altars", "Game's altars", "Water sources", "Game's water"))
        {
            var known = s.AltarsByLevel.Length == GameAltars.Length;
            for (var level = 0; level < s.AltarsByLevel.Length; level++)
                ui.Row(level == s.FlameLevel ? $"{level} (now)" : $"{level}", $"{s.AltarsByLevel[level]}", known ? $"{GameAltars[level]}" : "?",
                    $"{s.WaterByLevel[level]}", known ? $"{GameWater[level]}" : "?");
            ui.EndTable();
        }

        // ---- settings
        ui.Separator("Settings");
        if (s.CanManage)
        {
            var beyond = edit[SettingIndex.BeyondSixteen] != 0;
            var cap = beyond ? AltarHardCap : AltarCap;
            ui.Slider("At least this many altars", ref edit[SettingIndex.Altars], 1, cap);
            ui.Slider("Altars added to the game's", ref edit[SettingIndex.ExtraAltars], 0, cap - 1);
            ui.Slider("At least this many water sources", ref edit[SettingIndex.WaterSources], 0, WaterCap);
            ui.Slider("Water sources added to the game's", ref edit[SettingIndex.ExtraWaterSources], 0, WaterCap - 1);
            if (ui.Checkbox($"Allow more than 16 altars, up to {AltarHardCap} (experimental)", ref beyond))
                edit[SettingIndex.BeyondSixteen] = beyond ? 1 : 0;
            if (beyond)
                ui.TextWrapped("The game keeps track of 16 bases. Altars 17 and up only let you build: no comfort, no crafting " +
                               "from base chests, no survivor beds, no animals, no journal entry, and which altars those are " +
                               "can change after a reload. Every player needs the mod.");
            var preview = SettingIndex.From(edit);
            ui.TextWrapped("With these: " + Describe(preview) + ".");
            if (s.AltarsByLevel.Length == GameAltars.Length && s.FlameLevel >= 0 && s.FlameLevel < GameAltars.Length)
                ui.TextDisabled($"At Flame level {s.FlameLevel}: {Effective(GameAltars[s.FlameLevel], preview.Altars, preview.ExtraAltars, CapOf(preview))} altars, " +
                                $"{Effective(GameWater[s.FlameLevel], preview.WaterSources, preview.ExtraWaterSources, WaterCap)} water sources");
            if (ui.Button("Save")) Send(AltarsAction.SaveSettings, (int[])edit.Clone());
            ui.SameLine();
            if (ui.Button("Undo")) _edit = (int[])s.Saved.Clone();
            ui.SameLine();
            if (ui.Button("Defaults")) _edit = SettingIndex.Of(new Settings());
            ui.TextDisabled("With more than 16 altars allowed, a change to the altars applies at once. Water sources, and altars " +
                            (s.Hosted ? "without it, apply when the host starts the game again." : "without it, apply when the server starts again."));
        }
        else
        {
            ui.Text(Describe(SettingIndex.From(s.Running)));
            ui.TextDisabled("Only admins can change the settings.");
        }

        // ---- Flame level
        if (s.CanManage && s.FlameLevel >= 0)
        {
            ui.Separator("Flame level");
            ui.Slider("Flame level", ref _flameEdit, 1, Math.Max(1, s.FlameMax));
            ui.Checkbox("I know this changes the world's progress for everyone", ref _flameSure);
            if (ui.Button("Set the Flame level") && _flameSure && _flameEdit != s.FlameLevel)
            {
                Send(AltarsAction.SetFlameLevel, level: _flameEdit);
                _flameSure = false;
            }
            ui.TextDisabled("Lowering it is allowed; altars already placed stay, and the game decides what else follows.");
        }

        // ---- footer
        ui.Separator();
        if (_result.Length > 0) ui.Text(_resultOk ? Color.Green : Color.Orange, _result);
        if (ui.Button("Refresh")) Send(AltarsAction.Refresh);
        ui.SameLine();
        if (ui.Button("Close")) _window!.Open = false;
    }

    /// <summary>What a level allows with these settings (the boot patch's rule).</summary>
    internal static int Effective(int game, int atLeast, int extra, int cap) =>
        Math.Max(game, Math.Clamp(Math.Max(game + Math.Max(0, extra), atLeast), 0, cap));
}
