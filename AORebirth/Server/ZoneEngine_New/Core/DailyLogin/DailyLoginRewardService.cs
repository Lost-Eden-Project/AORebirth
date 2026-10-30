namespace ZoneEngine_New.Core.DailyLogin;

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Characters;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;

/// <summary>Plans the entire grant, commits progress and items together, then publishes inventory.</summary>
public sealed class DailyLoginRewardService(
    DailyLoginRewardSelector selector, IItemBuilder items, InventoryFlushService flush, IDailyLoginClaimDao dao)
{
    public DailyLoginClaimResult Claim(Player player, string authenticatedAccount, string token,
        DailyLoginAccountState importedState, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(player);
        string account = (authenticatedAccount ?? string.Empty).Trim().ToLowerInvariant();
        int characterId = player.Identity.Instance;
        if (string.IsNullOrEmpty(account) || characterId <= 0 || !IsValidToken(token))
            return new DailyLoginClaimResult { Message = "Daily Login claim authentication is invalid.", Terminal = true };
        utcNow = DailyLoginAccountState.Utc(utcNow);
        lock (player.PersistenceGate)
        {
            if (player.IsPersistenceQuarantined || !player.Inventory.IsHydrated)
                return Failure("Character inventory is not ready.", new DailyLoginAccountState());
            DailyLoginClaimSnapshotData? prior = dao.FindDailyLoginClaim(account, characterId, token);
            if (prior is not null)
                return Replay(prior, account, characterId);

            DailyLoginClaimSnapshotData? snapshot = dao.LoadDailyLoginState(account, characterId);
            if (snapshot is null)
            {
                // Validate without changing the retained import baseline, including a completed old cycle.
                DailyLoginAccountState import = DailyLoginAccountState.Parse(importedState.ToJson());
                bool hasImportedProgress = (import.Taken?.Length ?? 0) > 0 || import.ClaimedCount > 0 ||
                    !string.IsNullOrEmpty(import.LastGrantedUtc) || !string.IsNullOrEmpty(import.LastClaimUtc) ||
                    !string.IsNullOrEmpty(import.CycleCompletedOn);
                if (hasImportedProgress && (import.CharacterId != characterId ||
                    !string.Equals(import.AccountKey, account, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Daily Login progress cannot be imported without a verified character owner.");
                BindOwner(import, account, characterId);
                import.Normalize(utcNow);
                snapshot = dao.ImportDailyLoginState(account, characterId,
                    import.ToJson(), import.LastGrantedDateUtc, utcNow.Ticks);
            }
            RequireOwner(snapshot, account, characterId);
            DailyLoginAccountState state = DailyLoginAccountState.Parse(snapshot.StateJson).Normalize(utcNow);
            BindOwner(state, account, characterId);
            if (state.HasClaimedOn(utcNow))
                return Failure("You already claimed today's daily reward on this character.", state, true, alreadyClaimedToday: true);
            int day = state.NextDay;
            if (day == 0)
                return Failure("All 28 daily rewards are claimed. Board resets tomorrow.", state, true);
            if (!selector.TrySelect(day, player.Stats.GetOrOne(CharacterStat.Level),
                player.Stats.GetOrZero(CharacterStat.Profession), Random.Shared, out var selection, out string error))
                return Failure(error, state, true);
            if (!player.Inventory.HasFreeInventorySlots(selection!.ItemCount))
                return Failure("There is not enough inventory space for the complete daily reward.", state);

            var grants = new List<Item>(selection.ItemCount);
            for (int index = 0; index < selection.ItemCount; index++)
            {
                Item item = items.CreateWithNewInstance(selection.LowId, selection.HighId, selection.Quality,
                    ItemSource.Other, selection.StackCount);
                // ItemBuilder's normal mint default must not replace the configured reward quantity.
                item.StackCount = selection.StackCount;
                if (item.LowId != selection.LowId || item.HighId != selection.HighId || item.Quality != selection.Quality)
                    return Failure("Daily reward item creation changed its selected identity or quality.", state);
                grants.Add(item);
            }
            if (!InventoryGrantPlan.TryCreate(player, grants, out InventoryGrantPlan plan))
                return Failure("There is not enough inventory space for the complete daily reward.", state);

            DailyLoginAccountState next = state.RecordGrant(day, player.Identity.Instance, utcNow);
            DailyLoginClaimResult success = DailyLoginClaimResult.ForState(account, characterId, token, next, utcNow,
                "You received " + selection.ItemName + " x" + selection.Amount + ".");
            success.Ok = success.Committed = true;
            success.Terminal = true;
            success.Day = day;
            success.ItemId = selection.LowId;
            success.Amount = selection.Amount;
            success.Quality = selection.Quality;
            bool committed = false;
            try
            {
                flush.HardFlush(player);
                DailyLoginClaimCommitResultData result = dao.CommitDailyLoginClaim(new DailyLoginClaimCommitData
                {
                    AccountUsername = account, CharacterId = player.Identity.Instance, ClaimToken = token,
                    ClaimDateUtc = utcNow.Date, ExpectedStateToken = snapshot.ClaimToken, StateJson = next.ToJson(),
                    ResultJson = success.ToJson(), Inserts = plan.Rows.Select(ToPersistedItem).ToList(), NowUtcTicks = utcNow.Ticks
                });
                if (result.Status == DailyLoginClaimCommitStatus.Applied)
                {
                    committed = true;
                    plan.PublishAfterCommit();
                    return success;
                }
                if (result.Status == DailyLoginClaimCommitStatus.AlreadyApplied && result.Snapshot is not null)
                    return Replay(result.Snapshot, account, characterId);
                DailyLoginAccountState current = state;
                if (result.Snapshot is not null && result.Snapshot.CharacterId == characterId &&
                    string.Equals(result.Snapshot.AccountUsername, account, StringComparison.OrdinalIgnoreCase))
                {
                    current = DailyLoginAccountState.Parse(result.Snapshot.StateJson).Normalize(utcNow);
                    BindOwner(current, account, characterId);
                }
                return Failure(result.Status == DailyLoginClaimCommitStatus.AlreadyClaimedToday
                    ? "You already claimed today's daily reward on this character."
                    : result.Status == DailyLoginClaimCommitStatus.TokenOwnedByAnotherAccount
                    ? "This Daily Login claim token belongs to a different character or account."
                    : "Daily reward state changed. Reopen daily rewards and try again.", current,
                    result.Status is DailyLoginClaimCommitStatus.AlreadyClaimedToday or DailyLoginClaimCommitStatus.TokenOwnedByAnotherAccount,
                    alreadyClaimedToday: result.Status == DailyLoginClaimCommitStatus.AlreadyClaimedToday);
            }
            catch (Exception exception)
            {
                if (committed || exception is DatabaseCommitOutcomeUnknownException or CharacterPersistenceCommitOutcomeUnknownException
                    || player.IsPersistenceQuarantined)
                {
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    throw;
                }
                return Failure("Daily reward could not be saved. No reward was granted; try again.", state);
            }
        }

        DailyLoginClaimResult Failure(string message, DailyLoginAccountState state, bool terminal = false, bool alreadyClaimedToday = false)
        {
            DailyLoginClaimResult result = DailyLoginClaimResult.ForState(account, characterId, token, state, utcNow, message);
            result.Terminal = terminal;
            result.AlreadyClaimedToday = alreadyClaimedToday;
            return result;
        }
    }

    public static bool IsValidToken(string? token)
        => token is { Length: > 0 and <= 64 } && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    static void RequireOwner(DailyLoginClaimSnapshotData snapshot, string account, int characterId)
    {
        if (snapshot.CharacterId != characterId || !string.Equals(snapshot.AccountUsername, account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Daily Login state belongs to a different character or account.");
    }

    static void BindOwner(DailyLoginAccountState state, string account, int characterId)
    {
        if (state.CharacterId > 0 && state.CharacterId != characterId ||
            !string.IsNullOrWhiteSpace(state.AccountKey) && !string.Equals(state.AccountKey, account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Daily Login progress belongs to a different character or account.");
        state.CharacterId = characterId;
        state.AccountKey = account;
    }

    static DailyLoginClaimResult Replay(DailyLoginClaimSnapshotData snapshot, string account, int characterId)
    {
        RequireOwner(snapshot, account, characterId);
        DailyLoginClaimResult result = DailyLoginClaimResult.Parse(snapshot.ResultJson);
        if (result.CharacterId > 0 && result.CharacterId != characterId ||
            !string.IsNullOrWhiteSpace(result.AccountKey) && !string.Equals(result.AccountKey, account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Daily Login receipt belongs to a different character or account.");
        // A validated ledger owner can qualify an old receipt without changing its retained file.
        result.CharacterId = characterId;
        result.AccountKey = account;
        result.HasIdentity = true;
        result.Replayed = true;
        return result;
    }

    static PersistedItemData ToPersistedItem(ItemInstanceRecord row) => new()
    {
        InstanceId = row.InstanceId, ContainerType = row.ContainerType, ContainerInstance = row.ContainerInstance,
        ContainerPlacement = row.ContainerPlacement, ItemType = row.ItemType, LowId = row.LowId, HighId = row.HighId,
        Quality = row.Quality, StackCount = row.StackCount, Source = (int)row.Source
    };
}
