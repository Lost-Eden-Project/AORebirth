namespace ZoneEngine_New.Core.Cities
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;

    using Dapper;

    using MySqlConnector;

    using ZoneEngine_New.Core.Organizations;

    public enum CityClaimStatus
    {
        Claimed,
        AlreadyOwned,
        OrganizationOwnsCity,
        LockedOut,
        NotMember,
        /// <summary>The buyer is a member but not the organization's leader: only the leader buys its district.</summary>
        NotLeader,
        /// <summary>The organization's bank holds less than the price.</summary>
        NotEnoughInBank
    }

    /// <summary>A purchase's outcome; <see cref="Bank"/> is the organization's balance after it (or as it stood, when refused for it).</summary>
    public readonly record struct CityClaimResult(CityClaimStatus Status, long OwnershipId, CityPlotState? State, long Bank = 0);

    public enum CityWriteStatus
    {
        Done,
        NotOwner,
        NoPermission,
        Refused,
        NoHouse,
        NoCloakingDevice,
        CloakCoolingDown
    }

    public readonly record struct CityWriteResult(CityWriteStatus Status, BuildRefusal Refusal, long HouseId, CityPlotState? State)
    {
        public static CityWriteResult Fail(CityWriteStatus status, BuildRefusal refusal = BuildRefusal.None) => new(status, refusal, 0, null);
    }

    /// <summary>What a due sweep of one district did, for the owners' messages.</summary>
    public enum CityDueEventKind
    {
        UpkeepPaid,
        UpkeepWarning,
        DemolishedForUpkeep,
        ReleasedEmpty,
        ReleasedDisbanded
    }

    public readonly record struct CityDueEvent(CityDueEventKind Kind, int OrganizationId, long Upkeep, long Bank, int Days, int Hours);

    /// <summary>Who may build, demolish and cloak, from a member's stored row (governing form, stored rank, leader).</summary>
    public delegate bool CityManagePermission(int governingForm, int storedRank, bool isLeader);

    /// <summary>
    /// The city tables (city_plots, city_houses): the only authority for who owns which district and what stands on it.
    /// Every call is one transaction holding the district's row lock, re-checking there everything the tick checked
    /// against its cache, so two zone instances (a swap) never both sell a district or build on one tile. A purchase and a
    /// due sweep take the organization's row lock first (the same lock as every organization command), then the
    /// district's: one lock order everywhere. Every call blocks on the database; callers run it off the playfield tick.
    /// </summary>
    public interface ICityStore
    {
        IReadOnlyList<CityPlotState> LoadAll();

        /// <summary>The district as stored; an unsold district never written is returned unowned at version 0.</summary>
        CityPlotState Load(int playfieldId, int controllerIndex);

        IReadOnlyDictionary<(int PlayfieldId, int ControllerIndex), long> Versions();

        /// <summary>
        /// Buys a district for the organization, paid from its bank: the price leaves the bank and the district becomes the
        /// organization's, billed from now, in one transaction, so either both happen or neither does.
        /// </summary>
        CityClaimResult Claim(int playfieldId, int controllerIndex, int organizationId, int characterId, long price, float startingCharge,
            TimeSpan billingPeriod);

        /// <summary>Reserves the tiles for a house, pending the builder's house item.</summary>
        CityWriteResult ReserveHouse(CityControllerDefinition controller, CityCatalog catalog, int organizationId, int characterId,
            HouseTemplateDefinition template, int tileX, int tileZ, int rotation, int itemLowId, int itemHighId, int itemQuality,
            CityManagePermission canManage);

        /// <summary>
        /// The house item is taken: the house stands. True when it is (or already was) confirmed; false when the reservation
        /// is gone (the district was lost meanwhile, or the reservation was abandoned).
        /// </summary>
        bool ConfirmHouse(int playfieldId, int controllerIndex, long houseId, int organizationId, bool headquarters);

        /// <summary>Gives a pending reservation up (the builder no longer had the item).</summary>
        CityPlotState AbandonHouse(int playfieldId, int controllerIndex, long houseId);

        /// <summary>Starts the demolition of the house at the tile; a headquarters takes every house with it.</summary>
        CityWriteResult StartDemolish(CityControllerDefinition controller, CityCatalog catalog, int organizationId, int characterId,
            int tileX, int tileZ, CityManagePermission canManage);

        CityWriteResult ToggleCloak(CityControllerDefinition controller, CityCatalog catalog, int organizationId, int characterId,
            CityManagePermission canManage);

        /// <summary>Districts with something due: a bill or its warning, a finished demolition, an empty or orphaned district, a stale claim.</summary>
        IReadOnlyList<(int PlayfieldId, int ControllerIndex)> DuePlots(CityRules rules);

        /// <summary>Does everything due on one district in one transaction (the organization's bank and the district).</summary>
        (IReadOnlyList<CityDueEvent> Events, CityPlotState State) ProcessDue(CityControllerDefinition controller, CityCatalog catalog);
    }

    public sealed class MySqlCityStore : ICityStore
    {
        const string PlotColumns =
            "p.PlayfieldId, p.ControllerIndex, p.OrganizationId, COALESCE(o.Name, '') AS OrganizationName, p.OwnershipId, p.ClaimPending, "
            + "p.ClaimedAtUtc, p.PurchasedBy, p.PurchasedAtUtc, p.NextBillAtUtc, p.UpkeepWarnedForUtc, p.EmptySinceUtc, "
            + "p.HeadquartersBuiltAtUtc, p.CloakState, p.CloakToggledAtUtc, p.ControllerCharge, p.LockoutOrganizationId, "
            + "p.LockoutUntilUtc, p.Version";

        const string SelectPlot =
            "SELECT " + PlotColumns + " FROM city_plots p LEFT JOIN org_organizations o ON o.OrganizationId=p.OrganizationId ";

        const string HouseColumns =
            "HouseId, PlayfieldId, ControllerIndex, TemplateId, TileX, TileZ, Rotation, BuiltBy, Pending, CreatedAtUtc, DemolishAtUtc, "
            + "ItemLowId, ItemHighId, ItemQuality";

        const string ReleaseColumns =
            "OrganizationId=NULL, ClaimPending=0, ClaimedAtUtc=NULL, PurchasedBy=NULL, PurchasedAtUtc=NULL, NextBillAtUtc=NULL, "
            + "UpkeepWarnedForUtc=NULL, EmptySinceUtc=NULL, HeadquartersBuiltAtUtc=NULL, CloakState=0, CloakToggledAtUtc=NULL, "
            + "Version=Version+1";

        readonly Func<MySqlConnection> _open;

        public MySqlCityStore()
            : this(MySqlOrganizationStore.Open)
        {
        }

        public MySqlCityStore(Func<MySqlConnection> open)
        {
            _open = open ?? throw new ArgumentNullException(nameof(open));
        }

        // ------------------------------------------------------------------------------------------------ reads

        public IReadOnlyList<CityPlotState> LoadAll()
        {
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            List<PlotRow> plots = connection.Query<PlotRow>(SelectPlot, transaction: transaction).AsList();
            ILookup<(int, int), HouseRow> houses = connection.Query<HouseRow>("SELECT " + HouseColumns + " FROM city_houses",
                transaction: transaction).ToLookup(h => (h.PlayfieldId, h.ControllerIndex));
            transaction.Commit();
            return plots.Select(p => p.ToState(houses[(p.PlayfieldId, p.ControllerIndex)])).ToArray();
        }

        public CityPlotState Load(int playfieldId, int controllerIndex)
        {
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            CityPlotState state = Read(connection, transaction, playfieldId, controllerIndex);
            transaction.Commit();
            return state;
        }

        public IReadOnlyDictionary<(int PlayfieldId, int ControllerIndex), long> Versions()
        {
            using MySqlConnection connection = _open();
            return connection.Query<(int, int, long)>("SELECT PlayfieldId, ControllerIndex, Version FROM city_plots")
                .ToDictionary(r => (r.Item1, r.Item2), r => r.Item3);
        }

        static CityPlotState Read(MySqlConnection connection, MySqlTransaction transaction, int playfieldId, int controllerIndex)
        {
            PlotRow? row = connection.QuerySingleOrDefault<PlotRow>(SelectPlot + "WHERE p.PlayfieldId=@Pf AND p.ControllerIndex=@Idx",
                new { Pf = playfieldId, Idx = controllerIndex }, transaction);
            if (row == null)
                return new CityPlotState(playfieldId, controllerIndex);
            List<HouseRow> houses = connection.Query<HouseRow>(
                "SELECT " + HouseColumns + " FROM city_houses WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx ORDER BY HouseId",
                new { Pf = playfieldId, Idx = controllerIndex }, transaction).AsList();
            return row.ToState(houses);
        }

        // ------------------------------------------------------------------------------------------------ helpers

        static DateTime Now(MySqlConnection connection, MySqlTransaction transaction)
            => DateTime.SpecifyKind(connection.QuerySingle<DateTime>("SELECT UTC_TIMESTAMP(6)", transaction: transaction), DateTimeKind.Utc);

        /// <summary>Makes sure the district has its row, then locks it. Null only if the insert raced a delete (never: rows are never deleted).</summary>
        static PlotLock LockPlot(MySqlConnection connection, MySqlTransaction transaction, int playfieldId, int controllerIndex)
        {
            connection.Execute("INSERT IGNORE INTO city_plots (PlayfieldId, ControllerIndex) VALUES (@Pf, @Idx)",
                new { Pf = playfieldId, Idx = controllerIndex }, transaction);
            return connection.QuerySingle<PlotLock>(
                "SELECT OrganizationId, OwnershipId, ClaimPending, ClaimedAtUtc, NextBillAtUtc, UpkeepWarnedForUtc, EmptySinceUtc, "
                + "HeadquartersBuiltAtUtc, CloakState, CloakToggledAtUtc, LockoutOrganizationId, LockoutUntilUtc "
                + "FROM city_plots WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx FOR UPDATE",
                new { Pf = playfieldId, Idx = controllerIndex }, transaction);
        }

        static List<CityHouse> Houses(MySqlConnection connection, MySqlTransaction transaction, int playfieldId, int controllerIndex)
            => connection.Query<HouseRow>(
                    "SELECT " + HouseColumns + " FROM city_houses WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx ORDER BY HouseId",
                    new { Pf = playfieldId, Idx = controllerIndex }, transaction)
                .Select(h => h.ToHouse()).ToList();

        static void Bump(MySqlConnection connection, MySqlTransaction transaction, int playfieldId, int controllerIndex)
            => connection.Execute("UPDATE city_plots SET Version=Version+1 WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                new { Pf = playfieldId, Idx = controllerIndex }, transaction);

        /// <summary>Whether the character is a member of the organization that may manage its district, read in the transaction.</summary>
        static bool MayManage(MySqlConnection connection, MySqlTransaction transaction, int organizationId, int characterId,
            CityManagePermission canManage)
        {
            MemberRow? member = connection.QuerySingleOrDefault<MemberRow>(
                "SELECT m.OrgRank AS StoredRank, o.GoverningForm, o.LeaderId FROM org_members m "
                + "INNER JOIN org_organizations o ON o.OrganizationId=m.OrganizationId "
                + "WHERE m.CharacterId=@Character AND m.OrganizationId=@Org",
                new { Character = characterId, Org = organizationId }, transaction);
            return member != null && canManage(member.GoverningForm, member.StoredRank, member.LeaderId == characterId);
        }

        static void Release(MySqlConnection connection, MySqlTransaction transaction, int playfieldId, int controllerIndex,
            int? lockoutOrganizationId, DateTime? lockoutUntilUtc)
        {
            connection.Execute("DELETE FROM city_houses WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                new { Pf = playfieldId, Idx = controllerIndex }, transaction);
            connection.Execute("UPDATE city_plots SET " + ReleaseColumns + ", LockoutOrganizationId=@Org, LockoutUntilUtc=@Until "
                + "WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                new { Pf = playfieldId, Idx = controllerIndex, Org = lockoutOrganizationId, Until = lockoutUntilUtc }, transaction);
        }

        // ------------------------------------------------------------------------------------------------ purchase

        public CityClaimResult Claim(int playfieldId, int controllerIndex, int organizationId, int characterId, long price, float startingCharge,
            TimeSpan billingPeriod)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(price);
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            // The organization's row lock first: every claim of one organization, from any instance, is serialized here,
            // and so is every change of its leader and its bank (deposits, withdrawals, upkeep), so the leader and the
            // balance read here are the leader and the balance when the claim commits.
            OrgLock? organization = connection.QuerySingleOrDefault<OrgLock>(
                "SELECT LeaderId, Bank FROM org_organizations WHERE OrganizationId=@Org FOR UPDATE", new { Org = organizationId }, transaction);
            if (organization == null
                || connection.ExecuteScalar<long>("SELECT COUNT(*) FROM org_members WHERE CharacterId=@Character AND OrganizationId=@Org",
                    new { Character = characterId, Org = organizationId }, transaction) == 0)
                return new CityClaimResult(CityClaimStatus.NotMember, 0, null);
            if (organization.LeaderId != characterId)
                return new CityClaimResult(CityClaimStatus.NotLeader, 0, null);
            if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM city_plots WHERE OrganizationId=@Org",
                    new { Org = organizationId }, transaction) > 0)
                return new CityClaimResult(CityClaimStatus.OrganizationOwnsCity, 0, null);

            PlotLock plot = LockPlot(connection, transaction, playfieldId, controllerIndex);
            DateTime now = Now(connection, transaction);
            if (plot.OrganizationId != null)
                return new CityClaimResult(CityClaimStatus.AlreadyOwned, 0, null);
            if (plot.LockoutOrganizationId == organizationId && plot.LockoutUntilUtc is { } until && Utc(until) > now)
                return new CityClaimResult(CityClaimStatus.LockedOut, 0, null);
            // Under the organization's lock the balance read above is the balance now; the guard keeps it from going below zero regardless.
            if (organization.Bank < price
                || connection.Execute("UPDATE org_organizations SET Bank=Bank-@Price, Version=Version+1 WHERE OrganizationId=@Org AND Bank >= @Price",
                    new { Price = price, Org = organizationId }, transaction) == 0)
                return new CityClaimResult(CityClaimStatus.NotEnoughInBank, 0, null, organization.Bank);

            connection.Execute("DELETE FROM city_houses WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                new { Pf = playfieldId, Idx = controllerIndex }, transaction);
            connection.Execute(
                "UPDATE city_plots SET OrganizationId=@Org, OwnershipId=OwnershipId+1, ClaimPending=0, ClaimedAtUtc=@Now, PurchasedBy=@Character, "
                + "PurchasedAtUtc=@Now, NextBillAtUtc=@Bill, UpkeepWarnedForUtc=NULL, EmptySinceUtc=@Now, HeadquartersBuiltAtUtc=NULL, "
                + "CloakState=0, CloakToggledAtUtc=NULL, ControllerCharge=@Charge, LockoutOrganizationId=NULL, LockoutUntilUtc=NULL, "
                + "Version=Version+1 WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                new
                {
                    Pf = playfieldId, Idx = controllerIndex, Org = organizationId, Character = characterId, Now = now, Bill = now + billingPeriod,
                    Charge = startingCharge
                },
                transaction);
            CityPlotState state = Read(connection, transaction, playfieldId, controllerIndex);
            transaction.Commit();
            return new CityClaimResult(CityClaimStatus.Claimed, state.OwnershipId, state, organization.Bank - price);
        }

        // ------------------------------------------------------------------------------------------------ houses

        public CityWriteResult ReserveHouse(CityControllerDefinition controller, CityCatalog catalog, int organizationId, int characterId,
            HouseTemplateDefinition template, int tileX, int tileZ, int rotation, int itemLowId, int itemHighId, int itemQuality,
            CityManagePermission canManage)
        {
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            PlotLock plot = LockPlot(connection, transaction, controller.PlayfieldId, controller.Index);
            if (plot.OrganizationId != organizationId || plot.ClaimPending)
                return CityWriteResult.Fail(CityWriteStatus.NotOwner);
            if (!MayManage(connection, transaction, organizationId, characterId, canManage))
                return CityWriteResult.Fail(CityWriteStatus.NoPermission);
            List<CityHouse> houses = Houses(connection, transaction, controller.PlayfieldId, controller.Index);
            BuildRefusal refusal = CityPlacement.Check(controller, template, tileX, tileZ, rotation, houses,
                id => catalog.TryGetTemplate(id, out HouseTemplateDefinition t) ? t : null, catalog.Rules.MaxHousesPerPlot, allowMarket: false);
            if (refusal != BuildRefusal.None)
                return CityWriteResult.Fail(CityWriteStatus.Refused, refusal);

            DateTime now = Now(connection, transaction);
            long houseId = connection.ExecuteScalar<long>(
                "INSERT INTO city_houses (PlayfieldId, ControllerIndex, TemplateId, TileX, TileZ, Rotation, BuiltBy, Pending, CreatedAtUtc, "
                + "ItemLowId, ItemHighId, ItemQuality) VALUES (@Pf, @Idx, @Template, @X, @Z, @Rotation, @Character, 1, @Now, @Low, @High, @Ql); "
                + "SELECT LAST_INSERT_ID();",
                new
                {
                    Pf = controller.PlayfieldId, Idx = controller.Index, Template = template.Id, X = tileX, Z = tileZ, Rotation = rotation,
                    Character = characterId, Now = now, Low = itemLowId, High = itemHighId, Ql = itemQuality
                }, transaction);
            Bump(connection, transaction, controller.PlayfieldId, controller.Index);
            CityPlotState state = Read(connection, transaction, controller.PlayfieldId, controller.Index);
            transaction.Commit();
            return new CityWriteResult(CityWriteStatus.Done, BuildRefusal.None, houseId, state);
        }

        public bool ConfirmHouse(int playfieldId, int controllerIndex, long houseId, int organizationId, bool headquarters)
        {
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            PlotLock plot = LockPlot(connection, transaction, playfieldId, controllerIndex);
            if (plot.OrganizationId != organizationId)
                return false;
            bool? pending = connection.QuerySingleOrDefault<bool?>(
                "SELECT Pending FROM city_houses WHERE HouseId=@House AND PlayfieldId=@Pf AND ControllerIndex=@Idx",
                new { House = houseId, Pf = playfieldId, Idx = controllerIndex }, transaction);
            if (pending == null)
                return false;
            if (pending == true)
            {
                DateTime now = Now(connection, transaction);
                connection.Execute("UPDATE city_houses SET Pending=0 WHERE HouseId=@House", new { House = houseId }, transaction);
                connection.Execute(
                    "UPDATE city_plots SET EmptySinceUtc=NULL, "
                    + "HeadquartersBuiltAtUtc=CASE WHEN @Hq AND HeadquartersBuiltAtUtc IS NULL THEN @Now ELSE HeadquartersBuiltAtUtc END, "
                    + "Version=Version+1 WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                    new { Pf = playfieldId, Idx = controllerIndex, Hq = headquarters, Now = now }, transaction);
            }

            transaction.Commit();
            return true;
        }

        public CityPlotState AbandonHouse(int playfieldId, int controllerIndex, long houseId)
        {
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockPlot(connection, transaction, playfieldId, controllerIndex);
            if (connection.Execute("DELETE FROM city_houses WHERE HouseId=@House AND PlayfieldId=@Pf AND ControllerIndex=@Idx AND Pending=1",
                    new { House = houseId, Pf = playfieldId, Idx = controllerIndex }, transaction) > 0)
                MarkEmptyIfNoHouses(connection, transaction, playfieldId, controllerIndex);
            Bump(connection, transaction, playfieldId, controllerIndex);
            CityPlotState state = Read(connection, transaction, playfieldId, controllerIndex);
            transaction.Commit();
            return state;
        }

        static void MarkEmptyIfNoHouses(MySqlConnection connection, MySqlTransaction transaction, int playfieldId, int controllerIndex)
            => connection.Execute(
                "UPDATE city_plots SET EmptySinceUtc=COALESCE(EmptySinceUtc, UTC_TIMESTAMP(6)) WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx "
                + "AND OrganizationId IS NOT NULL AND ClaimPending=0 "
                + "AND NOT EXISTS (SELECT 1 FROM city_houses h WHERE h.PlayfieldId=@Pf AND h.ControllerIndex=@Idx)",
                new { Pf = playfieldId, Idx = controllerIndex }, transaction);

        public CityWriteResult StartDemolish(CityControllerDefinition controller, CityCatalog catalog, int organizationId, int characterId,
            int tileX, int tileZ, CityManagePermission canManage)
        {
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            PlotLock plot = LockPlot(connection, transaction, controller.PlayfieldId, controller.Index);
            if (plot.OrganizationId != organizationId || plot.ClaimPending)
                return CityWriteResult.Fail(CityWriteStatus.NotOwner);
            if (!MayManage(connection, transaction, organizationId, characterId, canManage))
                return CityWriteResult.Fail(CityWriteStatus.NoPermission);
            List<CityHouse> houses = Houses(connection, transaction, controller.PlayfieldId, controller.Index);
            CityHouse? house = CityPlacement.HouseAt(houses, tileX, tileZ);
            if (house == null || house.Pending || house.DemolishAtUtc != null)
                return CityWriteResult.Fail(CityWriteStatus.NoHouse);

            DateTime at = Now(connection, transaction) + catalog.Rules.Demolish;
            bool headquarters = catalog.TryGetTemplate(house.TemplateId, out HouseTemplateDefinition template)
                && template.Type == HouseType.Headquarters;
            // Retail text 10002/140816276: destroying the headquarters destroys every other house.
            if (headquarters)
                connection.Execute("UPDATE city_houses SET DemolishAtUtc=@At WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx AND DemolishAtUtc IS NULL",
                    new { At = at, Pf = controller.PlayfieldId, Idx = controller.Index }, transaction);
            else
                connection.Execute("UPDATE city_houses SET DemolishAtUtc=@At WHERE HouseId=@House", new { At = at, House = house.HouseId }, transaction);
            Bump(connection, transaction, controller.PlayfieldId, controller.Index);
            CityPlotState state = Read(connection, transaction, controller.PlayfieldId, controller.Index);
            transaction.Commit();
            return new CityWriteResult(CityWriteStatus.Done, BuildRefusal.None, house.HouseId, state);
        }

        public CityWriteResult ToggleCloak(CityControllerDefinition controller, CityCatalog catalog, int organizationId, int characterId,
            CityManagePermission canManage)
        {
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            PlotLock plot = LockPlot(connection, transaction, controller.PlayfieldId, controller.Index);
            if (plot.OrganizationId != organizationId || plot.ClaimPending)
                return CityWriteResult.Fail(CityWriteStatus.NotOwner);
            if (!MayManage(connection, transaction, organizationId, characterId, canManage))
                return CityWriteResult.Fail(CityWriteStatus.NoPermission);
            List<CityHouse> houses = Houses(connection, transaction, controller.PlayfieldId, controller.Index);
            if (!houses.Any(h => h.Standing && catalog.TryGetTemplate(h.TemplateId, out HouseTemplateDefinition t)
                    && CityPlacement.IsCloakingDevice(t.Type)))
                return CityWriteResult.Fail(CityWriteStatus.NoCloakingDevice);
            DateTime now = Now(connection, transaction);
            if (plot.CloakToggledAtUtc is { } toggled && Utc(toggled) + catalog.Rules.CloakToggleCooldown > now)
                return CityWriteResult.Fail(CityWriteStatus.CloakCoolingDown);

            connection.Execute("UPDATE city_plots SET CloakState=@State, CloakToggledAtUtc=@Now, Version=Version+1 "
                + "WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                new { State = plot.CloakState != 0 ? 0 : 1, Now = now, Pf = controller.PlayfieldId, Idx = controller.Index }, transaction);
            CityPlotState state = Read(connection, transaction, controller.PlayfieldId, controller.Index);
            transaction.Commit();
            return new CityWriteResult(CityWriteStatus.Done, BuildRefusal.None, 0, state);
        }

        // ------------------------------------------------------------------------------------------------ due work

        public IReadOnlyList<(int PlayfieldId, int ControllerIndex)> DuePlots(CityRules rules)
        {
            using MySqlConnection connection = _open();
            return connection.Query<(int, int)>(
                    "SELECT p.PlayfieldId, p.ControllerIndex FROM city_plots p WHERE p.OrganizationId IS NOT NULL AND ("
                    + " (p.ClaimPending=1 AND p.ClaimedAtUtc <= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @Pending SECOND))"
                    + " OR (p.ClaimPending=0 AND p.NextBillAtUtc <= UTC_TIMESTAMP(6))"
                    + " OR (p.ClaimPending=0 AND p.NextBillAtUtc <= DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @Warning SECOND)"
                    + "     AND (p.UpkeepWarnedForUtc IS NULL OR p.UpkeepWarnedForUtc <> p.NextBillAtUtc))"
                    + " OR (p.ClaimPending=0 AND p.EmptySinceUtc <= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @Empty SECOND))"
                    + " OR NOT EXISTS (SELECT 1 FROM org_organizations o WHERE o.OrganizationId=p.OrganizationId))"
                    + " UNION SELECT DISTINCT h.PlayfieldId, h.ControllerIndex FROM city_houses h"
                    + " WHERE h.DemolishAtUtc <= UTC_TIMESTAMP(6)"
                    + " OR (h.Pending=1 AND h.CreatedAtUtc <= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @Pending SECOND))"
                    + " LIMIT 200",
                    new
                    {
                        Pending = (long)rules.PendingTimeout.TotalSeconds,
                        Warning = (long)rules.UpkeepWarning.TotalSeconds,
                        Empty = (long)rules.EmptyPlotRelease.TotalSeconds
                    })
                .Select(r => (r.Item1, r.Item2)).ToArray();
        }

        public (IReadOnlyList<CityDueEvent> Events, CityPlotState State) ProcessDue(CityControllerDefinition controller, CityCatalog catalog)
        {
            CityRules rules = catalog.Rules;
            int pf = controller.PlayfieldId, idx = controller.Index;
            var events = new List<CityDueEvent>();
            using MySqlConnection connection = _open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            int? owner = connection.QuerySingleOrDefault<int?>("SELECT OrganizationId FROM city_plots WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                new { Pf = pf, Idx = idx }, transaction);
            // The organization's lock before the district's, as every purchase takes them.
            bool organizationExists = owner != null && connection.QuerySingleOrDefault<int?>(
                "SELECT OrganizationId FROM org_organizations WHERE OrganizationId=@Org FOR UPDATE", new { Org = owner }, transaction) != null;
            PlotLock plot = LockPlot(connection, transaction, pf, idx);
            if (plot.OrganizationId != owner || owner == null)
            {
                // Sold or lost between the two reads: the next sweep sees the new owner.
                CityPlotState unchanged = Read(connection, transaction, pf, idx);
                transaction.Commit();
                return (events, unchanged);
            }

            int organizationId = owner.Value;
            DateTime now = Now(connection, transaction);
            bool changed = false;
            if (!organizationExists)
            {
                Release(connection, transaction, pf, idx, null, null);
                events.Add(new CityDueEvent(CityDueEventKind.ReleasedDisbanded, organizationId, 0, 0, 0, 0));
                return Finish();
            }

            if (plot.ClaimPending)
            {
                // A claim an older server left waiting for its buyer's payment: the district goes back on sale.
                if (plot.ClaimedAtUtc is { } claimed && Utc(claimed) + rules.PendingTimeout <= now)
                {
                    Release(connection, transaction, pf, idx, plot.LockoutOrganizationId, plot.LockoutUntilUtc);
                    changed = true;
                }

                return Finish();
            }

            changed |= connection.Execute(
                "DELETE FROM city_houses WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx AND (DemolishAtUtc <= @Now OR (Pending=1 AND CreatedAtUtc <= @Stale))",
                new { Pf = pf, Idx = idx, Now = now, Stale = now - rules.PendingTimeout }, transaction) > 0;
            List<CityHouse> houses = Houses(connection, transaction, pf, idx);
            DateTime? emptySince = plot.EmptySinceUtc is { } e ? Utc(e) : null;
            if (houses.Count == 0 && emptySince == null)
            {
                emptySince = now;
                connection.Execute("UPDATE city_plots SET EmptySinceUtc=@Now WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                    new { Now = now, Pf = pf, Idx = idx }, transaction);
                changed = true;
            }

            if (houses.Count == 0 && emptySince is { } since && since + rules.EmptyPlotRelease <= now)
            {
                // Retail text 220109620: no headquarters within the time, and the buyer may not claim it again for a while.
                bool neverBuilt = plot.HeadquartersBuiltAtUtc == null;
                Release(connection, transaction, pf, idx, neverBuilt ? organizationId : null, neverBuilt ? now + rules.NoHeadquartersLockout : null);
                events.Add(new CityDueEvent(CityDueEventKind.ReleasedEmpty, organizationId, 0, 0, 0, 0));
                return Finish();
            }

            var standing = new CityPlotState(pf, idx, organizationId, string.Empty, plot.OwnershipId, false, null, null, null, null, null, null,
                0, null, 1f, null, null, 0, houses);
            long upkeep = standing.Upkeep(catalog, controller);
            if (plot.NextBillAtUtc is { } billAt && Utc(billAt) <= now)
            {
                DateTime due = Utc(billAt);
                long bank = connection.QuerySingle<long>("SELECT Bank FROM org_organizations WHERE OrganizationId=@Org", new { Org = organizationId }, transaction);
                if (connection.Execute("UPDATE org_organizations SET Bank=Bank-@Upkeep, Version=Version+1 WHERE OrganizationId=@Org AND Bank >= @Upkeep",
                        new { Upkeep = upkeep, Org = organizationId }, transaction) > 0)
                {
                    DateTime next = due + rules.BillingPeriod;
                    connection.Execute("UPDATE city_plots SET NextBillAtUtc=@Next WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                        new { Next = next, Pf = pf, Idx = idx }, transaction);
                    events.Add(new CityDueEvent(CityDueEventKind.UpkeepPaid, organizationId, upkeep, bank - upkeep,
                        (int)Math.Max(0, Math.Ceiling((next - now).TotalDays)), 0));
                }
                else
                {
                    // Retail text 223708388 and 508/254100788: not enough in the bank, and the city is demolished.
                    Release(connection, transaction, pf, idx, null, null);
                    events.Add(new CityDueEvent(CityDueEventKind.DemolishedForUpkeep, organizationId, upkeep, bank, 0, 0));
                }

                changed = true;
            }
            else if (plot.NextBillAtUtc is { } warnAt && Utc(warnAt) - rules.UpkeepWarning <= now
                && (plot.UpkeepWarnedForUtc == null || Utc(plot.UpkeepWarnedForUtc.Value) != Utc(warnAt)))
            {
                connection.Execute("UPDATE city_plots SET UpkeepWarnedForUtc=NextBillAtUtc WHERE PlayfieldId=@Pf AND ControllerIndex=@Idx",
                    new { Pf = pf, Idx = idx }, transaction);
                events.Add(new CityDueEvent(CityDueEventKind.UpkeepWarning, organizationId, upkeep, 0, 0,
                    (int)Math.Max(1, Math.Ceiling((Utc(warnAt) - now).TotalHours))));
            }

            return Finish();

            (IReadOnlyList<CityDueEvent>, CityPlotState) Finish()
            {
                if (changed || events.Count > 0)
                    Bump(connection, transaction, pf, idx);
                CityPlotState state = Read(connection, transaction, pf, idx);
                transaction.Commit();
                return (events, state);
            }
        }

        static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

        // ------------------------------------------------------------------------------------------------ rows

        sealed class OrgLock
        {
            public int LeaderId { get; set; }
            public long Bank { get; set; }
        }

        sealed class PlotLock
        {
            public int? OrganizationId { get; set; }
            public long OwnershipId { get; set; }
            public bool ClaimPending { get; set; }
            public DateTime? ClaimedAtUtc { get; set; }
            public DateTime? NextBillAtUtc { get; set; }
            public DateTime? UpkeepWarnedForUtc { get; set; }
            public DateTime? EmptySinceUtc { get; set; }
            public DateTime? HeadquartersBuiltAtUtc { get; set; }
            public int CloakState { get; set; }
            public DateTime? CloakToggledAtUtc { get; set; }
            public int? LockoutOrganizationId { get; set; }
            public DateTime? LockoutUntilUtc { get; set; }
        }

        sealed class MemberRow
        {
            public int StoredRank { get; set; }
            public int GoverningForm { get; set; }
            public int LeaderId { get; set; }
        }

        sealed class PlotRow
        {
            public int PlayfieldId { get; set; }
            public int ControllerIndex { get; set; }
            public int? OrganizationId { get; set; }
            public string OrganizationName { get; set; } = string.Empty;
            public long OwnershipId { get; set; }
            public bool ClaimPending { get; set; }
            public DateTime? ClaimedAtUtc { get; set; }
            public int? PurchasedBy { get; set; }
            public DateTime? PurchasedAtUtc { get; set; }
            public DateTime? NextBillAtUtc { get; set; }
            public DateTime? UpkeepWarnedForUtc { get; set; }
            public DateTime? EmptySinceUtc { get; set; }
            public DateTime? HeadquartersBuiltAtUtc { get; set; }
            public int CloakState { get; set; }
            public DateTime? CloakToggledAtUtc { get; set; }
            public float ControllerCharge { get; set; }
            public int? LockoutOrganizationId { get; set; }
            public DateTime? LockoutUntilUtc { get; set; }
            public long Version { get; set; }

            public CityPlotState ToState(IEnumerable<HouseRow> houses)
                => new(PlayfieldId, ControllerIndex, OrganizationId, OrganizationName, OwnershipId, ClaimPending, PurchasedBy,
                    Utc(PurchasedAtUtc), Utc(NextBillAtUtc), Utc(UpkeepWarnedForUtc), Utc(EmptySinceUtc), Utc(HeadquartersBuiltAtUtc),
                    CloakState, Utc(CloakToggledAtUtc), ControllerCharge, LockoutOrganizationId, Utc(LockoutUntilUtc), Version,
                    houses.Select(h => h.ToHouse()).ToArray());
        }

        sealed class HouseRow
        {
            public long HouseId { get; set; }
            public int PlayfieldId { get; set; }
            public int ControllerIndex { get; set; }
            public int TemplateId { get; set; }
            public int TileX { get; set; }
            public int TileZ { get; set; }
            public int Rotation { get; set; }
            public int BuiltBy { get; set; }
            public bool Pending { get; set; }
            public DateTime CreatedAtUtc { get; set; }
            public DateTime? DemolishAtUtc { get; set; }
            public int ItemLowId { get; set; }
            public int ItemHighId { get; set; }
            public int ItemQuality { get; set; }

            public CityHouse ToHouse()
                => new(HouseId, TemplateId, TileX, TileZ, Rotation, BuiltBy, Pending, Utc(CreatedAtUtc), Utc(DemolishAtUtc), ItemLowId, ItemHighId, ItemQuality);
        }
    }
}
