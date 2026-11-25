namespace ZoneEngine_New.Core.Cities
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// One house of a district, as city_houses stores it. A pending house holds its tiles while the builder's house item is
    /// being taken; nobody sees it, it gives no advantage and costs no upkeep until it is confirmed.
    /// </summary>
    public sealed record CityHouse(
        long HouseId,
        int TemplateId,
        int TileX,
        int TileZ,
        int Rotation,
        int BuiltBy,
        bool Pending,
        DateTime CreatedAtUtc,
        DateTime? DemolishAtUtc,
        int ItemLowId,
        int ItemHighId,
        int ItemQuality)
    {
        /// <summary>Shown to clients: confirmed. A house being demolished is still shown, marked.</summary>
        public bool Visible => !Pending;

        /// <summary>Counts for upkeep and advantages: confirmed and not being demolished.</summary>
        public bool Standing => !Pending && DemolishAtUtc == null;
    }

    /// <summary>
    /// One district as city_plots and city_houses store it, read in one transaction. The database is the only authority;
    /// every instance keeps the newest <see cref="Version"/> it has read (<see cref="CityService"/>).
    /// </summary>
    public sealed class CityPlotState
    {
        public CityPlotState(int playfieldId, int controllerIndex)
            : this(playfieldId, controllerIndex, null, string.Empty, 0, false, null, null, null, null, null, null, 0, null, 1f, null, null, 0, [])
        {
        }

        public CityPlotState(int playfieldId, int controllerIndex, int? organizationId, string organizationName, long ownershipId,
            bool claimPending, int? purchasedBy, DateTime? purchasedAtUtc, DateTime? nextBillAtUtc, DateTime? upkeepWarnedForUtc,
            DateTime? emptySinceUtc, DateTime? headquartersBuiltAtUtc, int cloakState, DateTime? cloakToggledAtUtc, float controllerCharge,
            int? lockoutOrganizationId, DateTime? lockoutUntilUtc, long version, IReadOnlyList<CityHouse> houses)
        {
            PlayfieldId = playfieldId;
            ControllerIndex = controllerIndex;
            OrganizationId = organizationId;
            OrganizationName = organizationName ?? string.Empty;
            OwnershipId = ownershipId;
            ClaimPending = claimPending;
            PurchasedBy = purchasedBy;
            PurchasedAtUtc = purchasedAtUtc;
            NextBillAtUtc = nextBillAtUtc;
            UpkeepWarnedForUtc = upkeepWarnedForUtc;
            EmptySinceUtc = emptySinceUtc;
            HeadquartersBuiltAtUtc = headquartersBuiltAtUtc;
            CloakState = cloakState;
            CloakToggledAtUtc = cloakToggledAtUtc;
            ControllerCharge = controllerCharge;
            LockoutOrganizationId = lockoutOrganizationId;
            LockoutUntilUtc = lockoutUntilUtc;
            Version = version;
            Houses = houses ?? [];
        }

        public (int PlayfieldId, int ControllerIndex) Key => (PlayfieldId, ControllerIndex);
        public int PlayfieldId { get; }
        public int ControllerIndex { get; }

        /// <summary>The owner; null while the district is for sale.</summary>
        public int? OrganizationId { get; }

        public string OrganizationName { get; }

        /// <summary>Raised by every purchase: a purchase's later confirmation names the claim it confirms.</summary>
        public long OwnershipId { get; }

        /// <summary>Claimed by a buyer whose payment is not stored yet: owned, but nothing may be built.</summary>
        public bool ClaimPending { get; }

        public int? PurchasedBy { get; }
        public DateTime? PurchasedAtUtc { get; }
        public DateTime? NextBillAtUtc { get; }

        /// <summary>The bill the owners were last warned about.</summary>
        public DateTime? UpkeepWarnedForUtc { get; }

        /// <summary>Since when the district has had no house; null while it has one.</summary>
        public DateTime? EmptySinceUtc { get; }

        /// <summary>When the district's first headquarters was confirmed; null until then (a district lost without one locks its buyer out).</summary>
        public DateTime? HeadquartersBuiltAtUtc { get; }

        /// <summary>1 cloaked, 0 not.</summary>
        public int CloakState { get; }

        public DateTime? CloakToggledAtUtc { get; }
        public float ControllerCharge { get; }
        public int? LockoutOrganizationId { get; }
        public DateTime? LockoutUntilUtc { get; }
        public long Version { get; }
        public IReadOnlyList<CityHouse> Houses { get; }

        public bool IsOwned => OrganizationId is > 0;

        /// <summary>Owned and paid for: what may be built on and billed.</summary>
        public bool IsActive => IsOwned && !ClaimPending;

        public bool IsLockedOut(int organizationId, DateTime nowUtc)
            => LockoutOrganizationId == organizationId && LockoutUntilUtc is { } until && until > nowUtc;

        public IEnumerable<CityHouse> StandingHouses => Houses.Where(h => h.Standing);

        public bool HasStandingType(CityCatalog catalog, Func<HouseType, bool> type)
            => StandingHouses.Any(h => catalog.TryGetTemplate(h.TemplateId, out HouseTemplateDefinition t) && type(t.Type));

        /// <summary>The month's upkeep: the land's, and every standing house's.</summary>
        public long Upkeep(CityCatalog catalog, CityControllerDefinition controller)
        {
            long total = controller.UpkeepCost;
            foreach (CityHouse house in StandingHouses)
                if (catalog.TryGetTemplate(house.TemplateId, out HouseTemplateDefinition template))
                    total += template.Upkeep;
            return total;
        }
    }
}
