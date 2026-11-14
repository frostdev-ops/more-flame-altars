-- More Flame Altars (MIT): a world may have this many Flame Altars at every Flame level.
-- The game's own numbers grow with the Flame level (1, 2, 4, 6, 8, 10, 11, 12, 13, 14, 15, then 16 from level 11 on).
-- A level where the game already allows more keeps the game's number. 1 to 16: the game's own code allows at most 16
-- Flame Altars whatever this list says (README.md, "More than 16 altars").
local ALTARS = 16

local want = math.max(1, math.min(16, math.floor(tonumber(ALTARS) or 16)))
local tables, levels, raised = 0, 0, 0
for _, balancing in ipairs(game.assets.get_resources_by_type("keen::BalancingTable")) do
    tables = tables + 1
    local numbers = balancing.data.altarsPerFlameLevel
    for level, altars in ipairs(numbers) do
        levels = levels + 1
        if altars < want then
            numbers[level] = want
            raised = raised + 1
        end
    end
end

if tables == 0 or levels == 0 then
    print("More Flame Altars: the game's list of altars per Flame level was not found (a game update may have moved it): nothing changed")
else
    print(string.format("More Flame Altars: %d altars at %d of %d Flame levels", want, raised, levels))
end
