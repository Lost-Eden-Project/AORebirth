namespace ZoneEngine_New.Core.Organizations
{
    using System;
    using System.Globalization;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// The organization bank. Credits only ever move so that a crash can lose them, never copy them: a deposit stores
    /// the character's lower cash before the bank grows; a withdrawal takes from the bank before the character is paid.
    /// </summary>
    public sealed partial class OrganizationService
    {
        void BankView(Player player)
        {
            if (!RequireOrganization(player, out int organizationId) || Cached(organizationId) is not { } state
                || state.Member(player.Identity.Instance) is not { } me)
                return;
            GoverningForm form = Rules.FormOf(state.GoverningForm);
            // The lowest rank may not look, except in a single-rank form (declared retail rule).
            if (!form.SingleRank && !state.IsLeader(me.CharacterId) && me.Rank >= form.LowestRank)
            {
                Reply(player, OrgText.Org(OrgText.BankRankTooLow));
                return;
            }

            Reply(player, OrgText.Org(OrgText.BankBalance, state.Bank));
        }

        static bool TryAmount(string text, out int amount)
            => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount > 0;

        void BankAdd(Player player, string args)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            if (!TryAmount(args, out int amount))
            {
                Reply(player, OrgText.Org(OrgText.CreditsNotAllowed));
                return;
            }

            int cash = player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base);
            if (cash < amount)
            {
                Reply(player, OrgText.Org(OrgText.NotEnoughCredits));
                return;
            }

            if (Cached(organizationId) is { } cached && cached.Bank + amount > Rules.BankCap)
            {
                Reply(player, OrgText.Org(OrgText.CreditsNotAllowed));
                return;
            }

            if (player.IsPersistenceQuarantined || player.Playfield is not Playfield playfield
                || playfield.GetService<CharacterSnapshotService>() is not { } snapshot
                || playfield.GetService<InventoryFlushService>() is not { } flush)
                return;
            int characterId = player.Identity.Instance;
            if (!_busy.TryAdd(characterId, 0))
                return;

            int after = cash - amount;
            player.Stats.Set(CharacterStat.Cash, after, StatDetail.Base, dirty: true);
            player.FlushDirtyStats();
            var record = new StatRecord { StatId = (int)CharacterStat.Cash, StatValue = after };
            try
            {
                // The player's write-behind lane: the lower cash is stored first, then the bank grows.
                flush.Enqueue(player, () =>
                {
                    Outcome? outcome = null;
                    try
                    {
                        snapshot.SyncStats(player, [record]);
                        outcome = Deposit(player, organizationId, amount);
                    }
                    finally
                    {
                        _busy.TryRemove(characterId, out _);
                    }

                    Outcome done = outcome!;
                    if (done.State == null)
                        Dispatch(player, () => Refund(player, amount, done));
                    else
                        Deliver(player, done);
                });
            }
            catch
            {
                _busy.TryRemove(characterId, out _);
                throw;
            }
        }

        // Runs in the player's lane. Anything but a committed deposit returns an outcome without a state: the caller refunds.
        Outcome Deposit(Player player, int organizationId, int amount)
        {
            try
            {
                return InOrganization(player, organizationId, (tx, state, me) =>
                    tx.TryAdjustBank(amount, Rules.BankCap) ? new Outcome() : ActorLine(player, OrgText.Org(OrgText.CreditsNotAllowed)));
            }
            catch (Exception exception)
            {
                _logger.Error(exception, string.Format(CultureInfo.InvariantCulture,
                    "Organization bank deposit failed character={0} org={1} amount={2}; refunding", player.Identity.Instance, organizationId, amount));
                return new Outcome();
            }
        }

        void Refund(Player player, int amount, Outcome outcome)
        {
            int current = player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base);
            int refunded = Math.Min(amount, CashLimit.Room(current));
            PayOnTick(player, refunded);
            if (refunded < amount)
                _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Organization bank refund capped character={0} amount={1} refunded={2}", player.Identity.Instance, amount, refunded));
            foreach (LostEden.Protocol.Messages.MessageBody message in outcome.Actor)
                player.Session?.Send(message);
        }

        void BankRemove(Player player, string args)
        {
            if (!RequireOrganization(player, out int organizationId))
                return;
            if (Cached(organizationId) is { } cached && !cached.IsLeader(player.Identity.Instance))
            {
                Reply(player, OrgText.Org(OrgText.NotLeaderBankRemove));
                return;
            }

            // The character's own limit is checked first: credits taken out are never more than it can hold.
            if (!TryAmount(args, out int amount) || amount > CashLimit.Room(player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base)))
            {
                Reply(player, OrgText.Org(OrgText.CreditsNotAllowed));
                return;
            }

            Run(player, () => InOrganization(player, organizationId, (tx, state, me) =>
            {
                if (!state.IsLeader(me.CharacterId))
                    return ActorLine(player, OrgText.Org(OrgText.NotLeaderBankRemove));
                if (state.Bank < amount || !tx.TryAdjustBank(-amount, Rules.BankCap))
                    return ActorLine(player, OrgText.Org(OrgText.BankInsufficient, state.Bank));
                _logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "Organization bank withdrawal org={0} character={1} amount={2} balance={3}", state.Id, me.CharacterId, amount, state.Bank - amount));
                return new Outcome { OnActorTick = actor => PayWithdrawal(actor, organizationId, amount) };
            }));
        }

        // The bank already paid: credit the character, and put back anything it can no longer hold.
        void PayWithdrawal(Player player, int organizationId, int amount)
        {
            int current = player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base);
            int paid = Math.Min(amount, CashLimit.Room(current));
            PayOnTick(player, paid);
            if (paid < amount)
                ReturnToBank(organizationId, amount - paid);
        }

        void ReturnToBank(int organizationId, int amount)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    bool returned = _store.Transact(organizationId, tx => tx.Row != null && tx.TryAdjustBank(amount, Rules.BankCap));
                    if (!returned)
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                            "Organization bank could not take back {0} credits org={1}", amount, organizationId));
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Organization bank return failed org=" + organizationId);
                }
            });
        }

        /// <summary>On the player's tick: adds credits and stores them through its write-behind lane.</summary>
        static void PayOnTick(Player player, int amount)
        {
            if (amount <= 0)
                return;
            int value = CashLimit.Clamp((long)player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base) + amount);
            player.Stats.Set(CharacterStat.Cash, value, StatDetail.Base, dirty: true);
            player.FlushDirtyStats();
            if (player.IsPersistenceQuarantined || player.Playfield is not Playfield playfield
                || playfield.GetService<CharacterSnapshotService>() is not { } snapshot
                || playfield.GetService<InventoryFlushService>() is not { } flush)
            {
                player.SaveState.MarkDirty();
                return;
            }

            var record = new StatRecord { StatId = (int)CharacterStat.Cash, StatValue = value };
            flush.Enqueue(player, () => snapshot.SyncStats(player, [record]));
        }
    }
}
