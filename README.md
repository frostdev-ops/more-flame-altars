# More Flame Altars

![More Flame Altars](art/nexus_banner_1920x480.png)

More Flame Altars (bases) for your world, from the first Flame level on. Optionally more water sources, and bigger
build areas around every altar.

> **Which version?** This README describes 0.4.1, which needs Kiln 0.2.0-dev.29.18 or later. On Kiln 0.2.0-dev.28 to
> 0.2.0-dev.29.17, use **0.3.0** from [Releases](https://github.com/frostdev-ops/more-flame-altars/releases) (no build
> area setting). Still on Kiln 0.2.0-dev.27? Use **0.1.0**: 16 Flame Altars at every Flame level (see
> [0.1.0, for Kiln 0.2.0-dev.27](#010-for-kiln-020-dev27)).

The game ties the number of Flame Altars to the world's Flame level: a new world allows 2, and the game only reaches 16
at Flame level 11. By default this mod allows 16 at every level, so you can found all your bases from the start. Or keep
the game's progression and add a few on top. With `buildArea`, levels 1-3 can be up to twice as wide, deep and high
as the game's; every side is capped at **239 m**, so levels 4-5 stop growing at 149%.

| Flame level | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 and up |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Altars, the game | 1 | 2 | 4 | 6 | 8 | 10 | 11 | 12 | 13 | 14 | 15 | 16 |
| Altars, this mod (default) | 16 | 16 | 16 | 16 | 16 | 16 | 16 | 16 | 16 | 16 | 16 | 16 |
| Altars, `extraAltars: 2` with `altars: 1` | 3 | 4 | 6 | 8 | 10 | 12 | 13 | 14 | 15 | 16 | 16 | 16 |
| Water sources, the game | 1 | 1 | 1 | 1 | 5 | 10 | 20 | 30 | 40 | 50 | 50 | 50 |

(The game's numbers as read from server build 26430253.)

## Needs

Kiln 0.2.0-dev.29.18 or later (mod API 0.2.10), on the server and on every player's game: the game reads these numbers
on both sides, so a player without the mod would see and be held to the game's own numbers. Kiln's modpack hands the
mod to players who join a Kiln server, and a player's game loads it with no extra setting, on a world you host and when
you join a server that runs it. (A `"preview"` line in `kiln/kiln.json` from Kiln 0.2.0-dev.28 is no longer needed;
leaving it does no harm.)

## Install

- **Kiln Launcher:** Library, import `more-flame-altars-<version>.zip`, switch it on in your profile.
- **Dedicated server with Kiln:** unzip it into the server's `kiln/mods` folder and restart the server.

## Settings

`kiln/config/more-flame-altars.json` (or the mod's settings in Kiln's in-game menu). The game's table is changed once,
when the game or the server starts, so a change applies at the next start. With `beyondSixteen` on, a change to the
Flame Altar settings (`altars`, `extraAltars`, `beyondSixteen`, `mergeBases`) applies at once instead, also when you
switch `beyondSixteen` on: the mod's hook then answers the game's altar limit from the settings as they are saved, and
`mergeBases` takes effect at the game's next rebuild of its bases (the mod asks for one). Water sources and the build
area always wait for the next start.

| Setting | Default | What it does |
|---|---|---|
| `altars` | 16 | At least this many Flame Altars at every Flame level (1 to 16; up to 128 with `beyondSixteen`). |
| `extraAltars` | 0 | Added to the game's own number at every level, up to 16 (127 with `beyondSixteen`). |
| `waterSources` | 0 | At least this many water sources at every level (0 to 50; 0 leaves the game's numbers). |
| `extraWaterSources` | 0 | Added to the game's own number of water sources at every level, up to 50. |
| `beyondSixteen` | false | **Experimental, may crash your game.** Allows more than 16 Flame Altars, up to 128. See below. |
| `mergeBases` | true | **Experimental**, only with `beyondSixteen`. With more than 16 altar zones in the world, all altars become one base, so altars past 16 are real base area. Off: altars past 16 are build-only. See below. |
| `buildArea` | 100 | Every Flame Altar's build area, in percent of the game's (100 to 200), with each side capped at 239 m. Levels 4-5 stop growing at 149%. 100 keeps the game's sizes. See [Bigger build areas](#bigger-build-areas). |

A level never gets less than the game gives it. With every setting at its lowest the mod changes nothing.

## Bigger build areas

A Flame Altar's build area is a cube around it. Its size depends on the altar's level, not on the Flame level, and
`buildArea` scales it on all three sides, then caps **each side independently at 239 m**. Existing settings of
150-200% are capped automatically when 0.4.2 loads; the saved percentage stays so levels 1-3 can still scale fully.
The window, `/altars` console output and a once-per-start log warning explain the cap.

> **Data-loss warning:** Before 0.4.1, build areas at levels 4-5 above 150% lost saved building blocks; 0.4.1 capped
> them at 240 m, and 0.4.2 at 239 m, which is safe wherever the altar stands. If blocks are missing, restore a backup
> from before the change. Update the server and **every player** to 0.4.2 before loading the world.

| Altar level | 1 | 2 | 3 | 4 | 5 |
|---|---|---|---|---|---|
| One side, the game | 40 m | 80 m | 120 m | 160 m | 160 m |
| One side, `buildArea: 150` | 60 m | 120 m | 180 m | 239 m | 239 m |
| One side, `buildArea: 200` (the most) | 80 m | 160 m | 239 m | 239 m | 239 m |

- **When it applies:** at the next start of the server (or of your game, when you host). From then on every altar has
  the bigger area: new ones and the ones already placed (seen on the lab server with level-1 altars; every altar level
  reads the same table).
- **Why 239 m per side:** the game saves a base's terrain and building blocks in 16 m chunks, and keeps at most
  4,096 of them per base (16 × 16 × 16: a 256 m cube). Past that it skips chunks without a word, scattered across the
  whole base, near the altar too, and they are gone after the next restart: about 17% of them at 254.4 m and 40% at
  288 m. Nothing wraps to a smaller area; it only looks that way. At exactly 240 m, an altar whose side happens to sit
  on the 16 m chunk grid can touch 17 chunks along that side and still lose some (seen on a lab server; 0.4.1's cap).
  Any side under 240 m touches at most 16 chunks wherever the altar stands, so 0.4.2 caps each side at 239 m.
  Levels 1-3 still reach 200% (80/160/239 m); levels 4-5 stop at 149% (239 m). Bigger areas also grow saves and reach
  neighbouring altars sooner; altars whose areas touch or overlap are one base in the game.
- **Lowering it again** (or removing the mod) shrinks every area at the next start. What lies outside the smaller area
  is then no longer base terrain: the game stops saving it, and after the next save and restart the terrain there is
  back to the world's own ground (seen on the lab server). Building blocks are terrain to the game, so **blocks you
  placed outside the smaller area go too**; placed items such as furniture are saved anywhere. Lower it only when
  nothing you built stands outside the smaller areas.
- **Every player needs the mod with the same setting.** The setting changes the game's table on each side; a player's
  game started with other settings warns in `/altars`.

## More than 16 altars (experimental)

**This can crash your game. If you want to try anyway, it is your world and your call:** the mod lets you go up to 128
and does not stop you below that. Back up your save first. Every player needs the mod (a Kiln client follows the
server's limit and merge when it joins and when they change; a game without the mod stops at 16). It is off by default
and only for admins to switch on.

The game is built for 16 bases. Its own code allows at most 16 Flame Altars whatever its table says, and the part of the
game that runs bases (comfort, crafting from base chests, beds and homes for survivors, animals, the journal's base
list) keeps 16 zones and 16 bases. With `beyondSixteen` on, the mod lifts the first limit through Kiln's hooks, up to
128. What happens past 16 depends on `mergeBases`:

- **`mergeBases` on (the default with `beyondSixteen`):** once the world has more than 16 altar zones, the mod gives the
  game's base system room for all of them and makes **all altars one base**. Every altar is then base area: comfort,
  crafting from base chests, survivor beds, animals and the rest work at all of them. The price: the journal lists one
  base, every base chest is shared by all altars, and comfort, beds and the rest are one base's. With 16 zones or fewer
  the game's own separate bases stay. The technique is XHL's (see Credits); the code is the mod's own.
- **`mergeBases` off:** the game's 16 bases stay. Altars 17 and up let you build, but they are not bases: no comfort, no
  crafting from base chests, no survivor beds, no animals, no journal entry; which altars those are follows the order
  the game lists them in, which can change after a reload.

Known risks, plainly:

- Nobody has tested more than 16 altars with this mod in a real game yet (see What is checked). The merge changes the
  game's base system in memory on the server and on every player's game; a mistake there can crash the game.
- Other mods' "unlimited altars" switches are reported to crash the game past about 36 altars. The cause was never
  pinned down. Static reading found no overflow in the game's zone code up to 255 zones, and the parts that iterate
  altar entities (spawn points, map markers, fast travel) were not checked.
- The game's zone list sent to players is a single network message; past about 43 zones the server logs that it is
  big, and past the session's message limit (unknown) a player may miss zone updates.
- A player's game draws at most 32 zone borders at once; past 32 in view some borders are not drawn.
- With `mergeBases`, the zone list has room for every zone, and the game's own base rebuild then tries to make a base
  record for each separate zone past 16, which has no room and would overwrite the zone list. The mod skips those
  records; that check is tested on a lab server, the rest of a merged world is not.

Altars that touch or overlap count as one base in the game already ("extension altars").

50 water sources is the most the game's own table gives, so the mod stays at or below it.

## /altars

Type `/altars` in the chat. With the mod in your game, a window opens: the world's Flame level, the altars and water
sources placed and allowed, the build area, the numbers at every Flame level and altar level, and the mod's settings,
each with when it applies. Admins (permission
`more-flame-altars.manage`) can change the settings there (see [Settings](#settings) for when they apply: the altars
at once with `beyondSixteen` on, otherwise at the next start of the server, or of your game when you host) and set the
world's Flame level. The window also has the `beyondSixteen` and `mergeBases` switches, with their warnings. Everyone else sees the same window, read only.

Admin commands, also from the server console: `/altars set <altars|extraAltars|waterSources|extraWaterSources|beyondSixteen|mergeBases|buildArea> <value>`
(use 0 or 1 for `beyondSixteen` and `mergeBases`, 100 to 200 for `buildArea`) and `/altars flame <level>`. They use the same `more-flame-altars.manage` permission
as the window. From the console, `/altars` prints a one-line summary.

## How it works

The numbers are lists in the game's balancing table (`keen::BalancingTable`: `altarsPerFlameLevel` and
`waterSourcesPerFlameLevel`, one number per Flame level; `buildzoneSizesPerAltarLevel`, the build area's cube at each
altar level). The game reads that table once, at its start, so the mod
changes it in Kiln's boot phase (`Mod.OnBoot`, a resource patch), before the game reads it. The limit patch does not
rewrite the game's files or your save: remove the mod and the next start has the game's numbers again. Changing the
world's Flame level through `/altars` does change world progress and is saved by the game.

## What is checked

0.4.2 is checked offline (`tests/Offline`), without starting the game or a server: every altar level and axis at
every integer percentage from 100 to 200 stays at or below 239 m, the preview agrees with the capped patch, the
warning explains the data-loss risk, and existing 0.4.0 settings from 150 to 200 load with the cap automatically.
The 4,096-chunk save limit and the 240 m grid case were measured on a lab server (Kiln's RE report, 2026-11-23).
The package is built with the pinned Kiln 0.2.0-dev.29.18 SDK. **No new in-game or lab check was run for 0.4.1.**

**Historical 0.4.0 checks were insufficient:** only level-1 altars were placed. Reading larger values in the table
did not prove that upgraded altars could save builds safely; player reports exposed the data loss.

0.4.0, on a lab server (Kiln 0.2.0-dev.29.18 as released, server build 26430253, no player), by the lab test in
`tests/LabTest` over eleven starts of one world with `buildArea` 100, 200, 100, 250 and 200 in that order (one 250
start with `beyondSixteen`; the last start ran the packed 0.4.0 zip):

- the running game's table holds the game's sizes times the setting on all three sides at every altar level
  (`[0, 80, 160, 240, 320, 320]` m at 200), and the server's zone of every altar equals its replicated range
  (`TriggerShape`);
- a new altar gets the bigger zone at once (level 1: half 40 m at 200, 50 m at 250), and the game's own zone test counts
  a point 30 m from it as inside at 200 and a point past the border as outside;
- altars placed at 100 have the bigger zone after the next start at 200, and the game's size again after a start at
  100;
- terrain built 30 m from a level-1 altar at 200 (with Kiln's own save region released, so only the game's save could
  keep it) was still there after a restart at 200 and after the first start back at 100, and gone after the next one;
- altars whose bigger zones overlap are one base in the game's base system (13 altar zones made 8 bases).

The 0.3.0 checks below ran again in the starts with `beyondSixteen` off and passed. The 0.4.0 offline checks covered
scaling only above 100 and at most 200%, the shared boot patch, and refusing 99, 201 and settings from a 0.3.0 window;
they did not check the safe per-side limit.

0.3.0, on a lab server (Kiln 0.2.0-dev.28 as released, runtime 0.2.0-dev.28+20261114.2301cc5, server build 26430253, no
player), by the lab test in `tests/LabTest`, with four settings: `altars: 128` with `beyondSixteen` and `mergeBases`;
the defaults with `beyondSixteen` off; `altars: 24` with `beyondSixteen` and `mergeBases` off; `altars: 1`,
`extraAltars: 2` with both on:

- the running game's table holds exactly the expected numbers at all 21 Flame levels (128 at every level with
  `altars: 128`), and Kiln's `WorldProgress.LimitsAt` reports them; settings that change nothing stage nothing;
- the game's own altar-limit function, called with a made-up table, answers what the settings say through the mod's
  hook: 128 at every Flame level with `altars: 128`, and the game's min(value, 16) with `beyondSixteen` off;
- `mergeBases`: the game's own base rebuild, run on the server's real base system with a made-up world of separate
  altar zones: with 20 zones, the game tries to make 20 base records and the mod skips the 4 past 16; with 20 and with 40
  zones every zone is in the base system's list and they are one base; the game's own queries put a point in the 40th
  zone in base 0 and a point between zones in no base; with the merge off, the same world gives the game's 16 zones and
  16 bases, and the 40th zone is in no base.

Offline (`tests/Offline`): the settings accept and save 128 and refuse 129, and the message that tells players the
server's limits carries the server's merge switch. Statically, for the client exe (build 26430625): every function the mod hooks has the same bytes as on the
server, and the patterns match exactly once in both exes.

0.2.0 was checked in a real game (a world hosted from the game, Kiln 0.2.0-dev.28 test build, with the `"preview"`
line above): placing more Flame Altars than the game's number for the world's Flame level, past 16 with `beyondSixteen`
too (then capped at 32), and the `/altars` window.

**Not checked yet:**

- anything of `buildArea` in a player's game: whether the player's game draws the bigger border and lets them build
  in it, and what a game without the mod (or with another setting) does at the old border;
- anything of 0.3.0 in a player's game: the client side of the merge (a player's base system and base UI lists), the
  raised limit past 32, and a client following the server's merge when it joins;
- placing more than 16 altars in a real game with 0.3.0, with `mergeBases` on or off, and past 32 or 36 altars: whether
  the merged base's comfort, base chests, survivors and the journal behave, and whether the game crashes;
- placing more water sources than the game's number; a joining player with and without the mod; removing the mod from
  a world that has more altars than the game's number allows (the altars should stay, and no new one can be placed
  until the Flame level allows it).

## Changes

- **0.4.2**: the cap is 239 m per side (was 240 m). The real limit is the game's save: at most 4,096 chunks of 16 m
  per base, with the rest skipped silently and scattered across the base. At exactly 240 m an altar whose side sits on
  the chunk grid could still lose some. Levels 4-5 now stop at 149%. Update every player and the server.
- **0.4.1**: urgent data-loss fix. Every build-area side is capped at 240 m, including existing 151-200% settings.
  Levels 1-3 still scale to 200%; levels 4-5 stop at 150%. The window shows capped sizes and explains the cap, and
  `/altars` and the log warn about unsafe saved settings. Earlier versions could wrap the area and delete builds;
  if blocks are missing, restore a backup from before the change. Update every player and the server.
- **0.4.0** (Kiln 0.2.0-dev.29.18): new `buildArea`: bigger build areas around every Flame Altar, up to twice the
  game's size on every side, in the same start-up patch as the altars. The `/altars` window is laid out again: one
  section per setting with its value and when it applies, the build area at every altar level, a "not saved yet"
  hint, and the switches under Experimental. `/altars set buildArea <100-200>`. Players need 0.4.0 too (the window's
  channel is version 4).
- **0.3.0** (Kiln 0.2.0-dev.28): up to 128 Flame Altars with `beyondSixteen` (was 32). New `mergeBases` (on by default
  when `beyondSixteen` is on): past 16 altar zones all altars become one base, so altars past 16 are real base area
  (XHL's technique, our code). The mod also stops the game's base rebuild from writing a 17th base record over its zone
  list. The window and `/altars set` have the new switch; the risks are stated in the window, here and on the mod page.
  Players need 0.3.0 too (the window's channel is version 3): a 0.2.0 game and a 0.3.0 server do not talk.
- **0.2.0** (Kiln 0.2.0-dev.28): the C# version: settings, water sources, `/altars` window and commands, the
  experimental `beyondSixteen` (up to 32, build-only past 16).
- **0.1.0** (Kiln 0.2.0-dev.27): the start-up data version in `eml/`, 16 altars at every level.

## 0.1.0, for Kiln 0.2.0-dev.27

`eml/` holds the first version, released as 0.1.0: a start-up data mod (`mod.json` + `src/mod.lua`) that Kiln runs
before the game starts. It does one thing: at least 16 Flame Altars at every Flame level. No settings file, no
`/altars`, no water sources; the number is `ALTARS` at the top of `src/mod.lua` (1 to 16). Like 0.2.0, it goes on the
server and in every player's game. Do not use 0.1.0 and 0.2.0 at once: 0.2.0 replaces it.

- **Kiln Launcher:** Library, import `more-flame-altars-0.1.0.zip`, switch it on in your profile.
- **Dedicated server with Kiln:** unzip it into the server's `kiln/eml/mods/more-flame-altars/` (so that
  `kiln/eml/mods/more-flame-altars/mod.json` exists) and restart the server.

Checked on a lab server with Kiln 0.2.0-dev.27 (the running game's table had 16 at every level); placing the altars in
a player's game is not checked yet.

To make its zip: `mkdir -p dist && (cd eml && zip -r ../dist/more-flame-altars-0.1.0.zip mod.json src)`.

## Development

```sh
ln -s ~/Kiln-release/kiln-0.2.0-dev.29.18/sdk kiln-sdk     # the SDK (its .nupkg files)
dotnet build -c Release -t:KilnPack -p:KilnPackageOutputPath=dist/
# managed regression checks; no game server needed
/mnt/Dongus/kiln-wave/tools/slot dotnet run --project tests/Offline -c Release
# lab check (a lab made with Kiln's tools/lab/lab.sh, Kiln 0.2.0-dev.29.18 in it)
lab=/path/to/lab
dotnet build -c Release -t:KilnDeploy -p:KilnServer="$lab"
(cd tests/LabTest && dotnet build -c Release -t:KilnDeploy -p:KilnServer="$lab")
kiln run --server "$lab" --wait 300 && kiln test more-flame-altars-labtest --server "$lab" && kiln run --server "$lab" --stop
```

`art/` holds the banners and thumbnails (`art/compose.py` rebuilds them from `art/source/`, made with an image
generator; `art/source/generation_prompts.json` has the prompts).

## Credits

`mergeBases` uses the technique of XHL's "XHL-Unlimited-Flame-Altars" 2.1.0 (MIT): a larger zone list for the game's
base system, all zones as one base, and the game's base queries answered from the zone list. We read XHL's mod
statically to learn it; More Flame Altars ships none of its code. Thank you, XHL.

## License

MIT, see [LICENSE](LICENSE). Unofficial, and not affiliated with or endorsed by Keen Games.
