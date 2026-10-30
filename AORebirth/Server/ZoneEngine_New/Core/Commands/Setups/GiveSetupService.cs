namespace ZoneEngine_New.Core.Commands.Setups;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using AORebirth.Enums;
using AORebirth.Modules.Events;

using SmokeLounge.AOtomation.Messaging.GameData;

using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Modules;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

/// <summary>
/// <c>.give setup &lt;url&gt;</c>: makes the issuer's worn weapons, armor, implants and symbiants exactly the aosetups setup,
/// sets the setup's profession and level and the AI level that level allows, sets abilities and skills to the setup's IP
/// raises, replaces trained perks and research with the setup's, applies its buffs, then reloads the issuer in place so
/// the client rebuilds from a full update. The web fetch runs off the tick; everything else runs on the
/// issuer's playfield owner, and nothing is changed unless the setup resolved.
/// </summary>
public sealed class GiveSetupService(
    AoSetupsClient client,
    IItemBuilder items,
    IItemTemplateCatalog catalog,
    InventoryFlushService flush,
    IInventoryRepository inventory,
    IGameData gameData,
    IZoneLogger logger)
{
    /// <summary>aosetups armor slot names to AO armor slots.</summary>
    static readonly Dictionary<string, int> ArmorSlotsByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NECK"] = (int)ArmorSlots.Neck, ["HEAD"] = (int)ArmorSlots.Head, ["BACK"] = (int)ArmorSlots.Back,
        ["SHOULDER_R"] = (int)ArmorSlots.RightShoulder, ["BODY"] = (int)ArmorSlots.Chest, ["SHOULDER_L"] = (int)ArmorSlots.LeftShoulder,
        ["ARM_R"] = (int)ArmorSlots.RightArm, ["HANDS"] = (int)ArmorSlots.Hands, ["ARM_L"] = (int)ArmorSlots.LeftArm,
        ["WRIST_R"] = (int)ArmorSlots.RightWrist, ["LEGS"] = (int)ArmorSlots.Legs, ["WRIST_L"] = (int)ArmorSlots.LeftWrist,
        ["FINGER_R"] = (int)ArmorSlots.RightFinger, ["FEET"] = (int)ArmorSlots.Feet, ["FINGER_L"] = (int)ArmorSlots.LeftFinger,
    };

    /// <summary>aosetups weapon-page slot names to AO weapon slots.</summary>
    static readonly Dictionary<string, int> WeaponSlotsByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HUD1"] = (int)WeaponSlots.Hud1, ["HUD2"] = (int)WeaponSlots.Hud2, ["HUD3"] = (int)WeaponSlots.Hud3,
        ["UTIL1"] = (int)WeaponSlots.Util1, ["UTIL2"] = (int)WeaponSlots.Util2, ["UTIL3"] = (int)WeaponSlots.Util3,
        ["WEAPON_R"] = (int)WeaponSlots.Righthand, ["WEAPON_L"] = (int)WeaponSlots.LeftHand, ["BELT"] = (int)WeaponSlots.Belt,
        ["NCU1"] = (int)WeaponSlots.Ncu1, ["NCU2"] = (int)WeaponSlots.Ncu2, ["NCU3"] = (int)WeaponSlots.Ncu3,
        ["NCU4"] = (int)WeaponSlots.Ncu4, ["NCU5"] = (int)WeaponSlots.Ncu5, ["NCU6"] = (int)WeaponSlots.Ncu6,
    };

    /// <summary>aosetups implant slot names to AO implant slots.</summary>
    static readonly Dictionary<string, int> ImplantSlotsByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eye"] = (int)ImplantSlots.Eyes, ["head"] = (int)ImplantSlots.Head, ["ear"] = (int)ImplantSlots.Ears,
        ["chest"] = (int)ImplantSlots.Chest, ["waist"] = (int)ImplantSlots.Waist, ["legs"] = (int)ImplantSlots.Legs,
        ["feet"] = (int)ImplantSlots.Feet, ["larm"] = (int)ImplantSlots.Leftarm, ["lwrist"] = (int)ImplantSlots.Leftwrist,
        ["lhand"] = (int)ImplantSlots.Lefthand, ["rarm"] = (int)ImplantSlots.Rightarm, ["rwrist"] = (int)ImplantSlots.Rightwrist,
        ["rhand"] = (int)ImplantSlots.Righthand,
    };

    readonly ConcurrentDictionary<int, byte> _loading = new();
    readonly Lazy<Dictionary<string, CharacterStat>> _skillLabels = new(() => LoadSkillLabels(gameData));
    readonly Lazy<Dictionary<(int Breed, CharacterStat Ability), int>> _breedAbilities = new(() => LoadBreedAbilities(gameData));

    sealed record Placement(Container Page, int Slot, Item Item, string Label);

    /// <summary>Starts the fetch; the setup is applied later on the issuer's playfield owner.</summary>
    public void Start(IZoneSession session, Player player, string text)
    {
        if (!AoSetupsClient.TryParseSetupId(text, out string id))
        {
            GmCommandFeedback.Send(session, player, "Usage: .give setup <https://aosetups.com/equip/id>");
            return;
        }

        if (!_loading.TryAdd(player.Identity.Instance, 0))
        {
            GmCommandFeedback.Send(session, player, "A setup is already loading.");
            return;
        }

        GmCommandFeedback.Send(session, player, "Loading setup " + id + " from " + AoSetupsClient.Host + "...");
        _ = Task.Run(async () =>
        {
            try
            {
                Task<AoSetup> setup = client.GetSetupAsync(id);
                Task<AoSetupsReference> reference = client.GetReferenceAsync();
                await Task.WhenAll(setup, reference).ConfigureAwait(false);
                OnOwner(session, player, () => Apply(session, player, setup.Result, reference.Result));
            }
            catch (Exception exception)
            {
                logger.Warn(string.Format(CultureInfo.InvariantCulture, "aosetups load failed char={0} setup={1}: {2}",
                    player.Identity.Instance, id, exception.Message));
                OnOwner(session, player, () => GmCommandFeedback.Send(session, player, "Could not load setup " + id + ": " + exception.Message));
            }
            finally
            {
                _loading.TryRemove(player.Identity.Instance, out _);
            }
        });
    }

    /// <summary>Runs on the player's current playfield owner, only if that player is still on that session.</summary>
    static void OnOwner(IZoneSession session, Player player, Action action)
    {
        Playfield? playfield = player.Playfield;
        playfield?.DispatchPlayerProjection(player, () =>
        {
            if (ReferenceEquals(player.Playfield, playfield) && ReferenceEquals(player.Session, session))
                action();
        });
    }

    void Apply(IZoneSession session, Player player, AoSetup setup, AoSetupsReference reference)
    {
        Playfield? playfield = player.Playfield;
        if (playfield == null || player.IsDead || !player.Inventory.IsHydrated || player.IsPersistenceQuarantined)
        {
            GmCommandFeedback.Send(session, player, "Setup not applied: you must be alive and fully loaded.");
            return;
        }

        PlayerInventory worn = player.Inventory;
        Container[] wearPages = [worn.Equipment, worn.Armor, worn.Implant];
        if (playfield.GetService<InventoryMoveService>()?.HasPending(player.Identity.Instance) == true
            || wearPages.Any(page => page.Content.Values.Any(item => item != null && item.Locked)))
        {
            GmCommandFeedback.Send(session, player, "Setup not applied: an equip or trade is in progress.");
            return;
        }

        // Resolve everything before touching the character.
        var skipped = new List<string>();
        var placements = new Dictionary<(IdentityType, int), Placement>();
        foreach (AoSetupItem entry in setup.Items)
        {
            Container page = entry.Weapon ? worn.Equipment : worn.Armor;
            Dictionary<string, int> slots = entry.Weapon ? WeaponSlotsByName : ArmorSlotsByName;
            if (!slots.TryGetValue(entry.Slot, out int relative))
            {
                skipped.Add(entry.Slot + ": unknown slot");
                continue;
            }

            int lowId = reference.TryResolveLowId(entry.HighId, entry.Quality, out int low) ? low : entry.HighId;
            TryPlace(page, relative, lowId, entry.HighId, entry.Quality, entry.Slot, placements, skipped);
        }

        foreach (AoSetupImplant implant in setup.Implants)
        {
            if (!ImplantSlotsByName.TryGetValue(implant.Slot, out int relative))
            {
                skipped.Add(implant.Slot + ": unknown implant slot");
                continue;
            }

            if (!reference.TryResolveImplant(implant, out int lowId, out int highId, out string combination))
            {
                skipped.Add(implant.Slot + " implant: no item for clusters " + combination);
                continue;
            }

            TryPlace(worn.Implant, relative, lowId, highId, implant.Quality, implant.Slot + " implant", placements, skipped);
        }

        foreach (AoSetupSymbiant symbiant in setup.Symbiants)
        {
            if (!ImplantSlotsByName.TryGetValue(symbiant.Slot, out int relative))
            {
                skipped.Add(symbiant.Slot + ": unknown symbiant slot");
                continue;
            }

            int lowId = reference.TryResolveLowId(symbiant.HighId, symbiant.Quality, out int low) ? low : symbiant.HighId;
            TryPlace(worn.Implant, relative, lowId, symbiant.HighId, symbiant.Quality, symbiant.Slot + " symbiant", placements, skipped);
        }

        var skills = new List<(CharacterStat Stat, int Base)>();
        var breed = (int)player.Stats.GetOrZero(CharacterStat.Breed, StatDetail.Base);
        foreach (AoSetupSkill skill in setup.Skills)
        {
            if (!_skillLabels.Value.TryGetValue(Normalize(skill.Name), out CharacterStat stat))
            {
                skipped.Add("skill " + skill.Name + ": unknown");
                continue;
            }

            int start = SkillCatalog.IsAbility(stat) ? _breedAbilities.Value.GetValueOrDefault((breed, stat), SkillCatalog.SkillFloor) : SkillCatalog.SkillFloor;
            skills.Add((stat, start + Math.Max(0, skill.PointsFromIp)));
        }

        List<int> perks = ResolvePerks(player.PerkCatalog, setup.Perks, skipped);

        // Worn gear is replaced, not kept: the result is exactly the setup.
        var graveyard = new Identity { Type = IdentityType.None, Instance = player.Identity.Instance };
        int removed = 0;
        foreach (Container page in wearPages)
        {
            foreach (int slot in page.Content.Keys.ToArray())
            {
                if (page.Remove(slot) is { } item)
                {
                    worn.Discard(item, graveyard);
                    ModuleHooks.ItemMoved(player, item, ModuleHooks.Place(ItemPlaceKind.Equipment), ModuleHooks.Place(ItemPlaceKind.Destroyed));
                    removed++;
                }
            }
        }

        foreach (Placement placement in placements.Values)
        {
            placement.Page.Add(placement.Slot, placement.Item);
            worn.MarkDirty(placement.Item, placement.Page, placement.Slot);
            ModuleHooks.ItemMoved(player, placement.Item, ModuleHooks.Place(ItemPlaceKind.None), ModuleHooks.Place(ItemPlaceKind.Equipment));
        }

        // Profession first: level IP and skill costs follow it. VisualProfession follows on rebase.
        Profession? profession = TryProfession(setup.Profession);
        if (profession is { } chosen)
            player.Stats.Set(CharacterStat.Profession, (int)chosen, StatDetail.Base);
        else if (setup.Profession.Length != 0)
            skipped.Add("profession " + setup.Profession + ": unknown");

        // GM level set: XP snaps to the start of the level, title level and IP follow.
        bool levelSet = setup.Level > 0 && player.TrySetLevel(setup.Level);
        if (setup.Level > 0 && !levelSet)
            skipped.Add("level " + setup.Level + ": not set");

        // aosetups keeps no AI level: it allows the highest one the character level reaches (AlienXp.json MinLevel).
        int alienLevel = MaxAlienLevel(player.Stats.GetOrOne(CharacterStat.Level));
        bool alienLevelSet = player.TrySetAlienLevel(alienLevel);
        if (!alienLevelSet)
            skipped.Add("AI level " + alienLevel + ": not set");

        AllocateSetupSkills(player, skills);

        // Perks and research are replaced, not kept: whatever was trained or being researched before is gone.
        player.ReplacePerksAndResearch(perks);
        int research = perks.Count(player.PerkCatalog.IsResearch);

        player.Rebase();
        // A GM setup is applied even when it costs more than the level earns; say by how much instead of hiding it.
        int ipOverBudget = Math.Max(0, -player.AvailableIp);
        foreach (Placement placement in placements.Values.Where(placement => placement.Page.Identity.Type != IdentityType.SocialPage))
            WearCastNano.ApplyItem(player, placement.Item, includeWield: placement.Page.Identity.Type == IdentityType.WeaponPage, items, inventory);

        flush.NotifyDirty(player);
        player.SaveState.MarkDirty();

        int buffs = 0;
        foreach (int nanoId in setup.Buffs)
        {
            if (catalog.TryGet(nanoId, out _) && NanoRuntime.TryApplyImmediate(player, player, nanoId, items, inventory, DateTime.UtcNow))
                buffs++;
            else
                skipped.Add("buff " + nanoId + ": not applied");
        }

        playfield.GetRequiredService<SpawnService>().ReloadInPlace(player);

        logger.Info(string.Format(CultureInfo.InvariantCulture, "aosetups applied char={0} setup='{1}' profession={2} level={3} aiLevel={4} equipped={5} removed={6} skills={7} perks={8} research={9} buffs={10} skipped={11}",
            player.Identity.Instance, setup.Name, profession?.ToString() ?? "unchanged", levelSet ? setup.Level : 0, alienLevelSet ? alienLevel : -1,
            placements.Count, removed, skills.Count, perks.Count - research, research, buffs, skipped.Count));
        GmCommandFeedback.Send(session, player, string.Format(CultureInfo.InvariantCulture,
            "Applied setup '{0}': {1}, level {2}, AI level {3}, {4} items equipped, {5} removed, {6} skills set, {7} perks, {8} research, {9}/{10} buffs.",
            setup.Name, profession?.ToString() ?? "profession unchanged", levelSet ? setup.Level.ToString(CultureInfo.InvariantCulture) : "unchanged",
            alienLevelSet ? alienLevel.ToString(CultureInfo.InvariantCulture) : "unchanged", placements.Count, removed, skills.Count,
            perks.Count - research, research, buffs, setup.Buffs.Count));
        if (ipOverBudget > 0)
            GmCommandFeedback.SendError(session, player, string.Format(CultureInfo.InvariantCulture,
                "The setup's skills cost {0} more IP than level {1} gives; applied anyway.",
                ipOverBudget, player.Stats.GetOrOne(CharacterStat.Level)));
        foreach (string reason in skipped)
            GmCommandFeedback.Send(session, player, "Skipped " + reason);
    }

    void TryPlace(Container page, int relative, int lowId, int highId, int quality, string label,
        Dictionary<(IdentityType, int), Placement> placements, List<string> skipped)
    {
        if (!catalog.TryGet(lowId, out _) || !catalog.TryGet(highId, out _))
        {
            skipped.Add(string.Format(CultureInfo.InvariantCulture, "{0}: item {1}/{2} not in items.dat", label, lowId, highId));
            return;
        }

        Item item = items.CreateWithNewInstance(lowId, highId, quality, ItemSource.Command);
        // Same Placement/Slot mask test as a client equip (InventoryMoveService.FitsWearSlot).
        int mask = item.GetStat(CharacterStat.Slot);
        if (mask <= 0 || (mask & (1 << relative)) == 0)
        {
            skipped.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1} does not fit that slot", label, item.Name));
            return;
        }

        int slot = page.Offset + relative - 1;
        placements[(page.Identity.Type, slot)] = new Placement(page, slot, item, label);
    }

    /// <summary>
    /// aosetups perk item ids to perk ids (Perks.json), regular, alien and research alike. A tier whose previous tier is
    /// not in the setup too is left out, as the client would never have trained it.
    /// </summary>
    static List<int> ResolvePerks(PerkCatalog perkCatalog, IReadOnlyList<int> itemIds, List<string> skipped)
    {
        var perkIds = new HashSet<int>();
        foreach (int itemId in itemIds)
        {
            if (perkCatalog.TryGetPerkId(itemId, out int perkId))
                perkIds.Add(perkId);
            else
                skipped.Add(string.Format(CultureInfo.InvariantCulture, "perk item {0}: not in Perks.json", itemId));
        }

        // Dropping a tier can orphan the one after it, so repeat until nothing more drops.
        bool dropped = true;
        while (dropped)
        {
            dropped = false;
            foreach (int perkId in perkIds.ToArray())
            {
                if (perkCatalog.TryGetPreviousId(perkId, out int previousId) && !perkIds.Contains(previousId))
                {
                    perkIds.Remove(perkId);
                    skipped.Add(string.Format(CultureInfo.InvariantCulture, "perk {0}: previous tier {1} not in the setup", perkId, previousId));
                    dropped = true;
                }
            }
        }

        return perkIds.Order().ToList();
    }

    /// <summary>The highest alien level whose AlienXp.json MinLevel <paramref name="level"/> reaches; 0 when none does.</summary>
    int MaxAlienLevel(int level)
    {
        int alienLevel = 0;
        while (gameData.TryGetAlienXpLevel(alienLevel + 1, out AlienXpLevelEntry? entry) && entry != null
            && entry.MinLevel > 0 && entry.MinLevel <= level)
            alienLevel++;

        return alienLevel;
    }

    /// <summary>"Martial Artist", "Nano-Technician", "Meta-Physicist": letters only, matched against player professions.</summary>
    static Profession? TryProfession(string name)
    {
        string wanted = Normalize(name);
        if (wanted.Length == 0)
            return null;

        foreach (Profession value in Enum.GetValues<Profession>())
        {
            if (value is not (Profession.None or Profession.Monster) && Normalize(value.ToString()) == wanted)
                return value;
        }

        return null;
    }

    /// <summary>
    /// The setup is the whole build, as for gear and perks: each trainable ability and skill gets the setup's value, or
    /// its starting value (the breed's ability base, or <see cref="SkillCatalog.SkillFloor"/>) when the setup does not
    /// list it, and UsedIP becomes what raising every one of them from its start costs at the character's breed and
    /// (already set) profession, priced the way training prices it. Without this the character kept its old spending,
    /// so its unspent IP had nothing to do with the skills it now has.
    /// </summary>
    void AllocateSetupSkills(Player player, IReadOnlyList<(CharacterStat Stat, int Base)> setupSkills)
    {
        int breedId = player.Stats.GetOrZero(CharacterStat.Breed, StatDetail.Base);
        var breed = (Breed)breedId;
        var profession = (Profession)player.Stats.GetOrZero(CharacterStat.Profession, StatDetail.Base);
        SkillCatalog skillCatalog = player.SkillCatalog;

        var listed = new Dictionary<CharacterStat, int>();
        foreach ((CharacterStat stat, int value) in setupSkills)
            listed[stat] = value;

        long used = 0;
        var seen = new HashSet<CharacterStat>();
        foreach (CharacterStat stat in Enum.GetValues<CharacterStat>())
        {
            if ((!SkillCatalog.IsAbility(stat) && !SkillCatalog.IsSkill(stat)) || !seen.Add(stat))
                continue;

            int start = SkillCatalog.IsAbility(stat)
                ? _breedAbilities.Value.GetValueOrDefault((breedId, stat), SkillCatalog.SkillFloor)
                : SkillCatalog.SkillFloor;
            // Not trainable at this breed and profession: leave it alone.
            if (!skillCatalog.TryGetRaiseCost(breed, profession, stat, start, start, out _))
                continue;

            int value = listed.TryGetValue(stat, out int setupValue) ? Math.Max(start, setupValue) : start;
            player.Stats.Set(stat, value, StatDetail.Base);
            if (skillCatalog.TryGetRaiseCost(breed, profession, stat, start, value, out long cost))
                used += cost;
        }

        player.Stats.Set(CharacterStat.UsedIP, (int)Math.Min(used, int.MaxValue), StatDetail.Base);
        // The level grants its IP as the bonus; the base is only extra IP. Characters from the old model kept their
        // lifetime IP there, which a setup at a lower level would otherwise keep as unspent IP.
        player.Stats.Set(CharacterStat.IP, 0, StatDetail.Base);
    }

    /// <summary>Client short stat names (Text.json category 2003, keyed by stat id) for abilities and skills.</summary>
    static Dictionary<string, CharacterStat> LoadSkillLabels(IGameData gameData)
    {
        var labels = new Dictionary<string, CharacterStat>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(gameData.RootPath, "Text.json")));
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("category", out JsonElement category) || category.GetInt32() != 2003
                || !entry.TryGetProperty("id", out JsonElement id) || !entry.TryGetProperty("text", out JsonElement text))
                continue;

            var stat = (CharacterStat)id.GetInt32();
            if (SkillCatalog.IsAbility(stat) || SkillCatalog.IsSkill(stat))
                labels.TryAdd(Normalize(text.GetString() ?? string.Empty), stat);
        }

        return labels;
    }

    /// <summary>Starting ability bases per breed from NewCharacter.json.</summary>
    static Dictionary<(int, CharacterStat), int> LoadBreedAbilities(IGameData gameData)
    {
        var abilities = new Dictionary<(int, CharacterStat), int>();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(gameData.RootPath, "NewCharacter.json")));
        if (!document.RootElement.TryGetProperty("Breeds", out JsonElement breeds))
            return abilities;

        foreach (JsonElement breed in breeds.EnumerateArray())
        {
            if (!breed.TryGetProperty("Breed", out JsonElement id))
                continue;
            foreach (JsonProperty property in breed.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && Enum.TryParse(property.Name, out CharacterStat stat) && SkillCatalog.IsAbility(stat))
                    abilities[(id.GetInt32(), stat)] = property.Value.GetInt32();
            }
        }

        return abilities;
    }

    /// <summary>"Matt. Metam" and "Matt.Metam" are the same label: letters, digits and &amp; only, lower case.</summary>
    static string Normalize(string label)
    {
        var text = new StringBuilder(label.Length);
        foreach (char c in label)
        {
            if (char.IsLetterOrDigit(c) || c == '&')
                text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }
}
