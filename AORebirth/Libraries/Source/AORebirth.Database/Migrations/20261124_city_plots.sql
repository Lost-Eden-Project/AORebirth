-- Org cities: the districts organizations buy from city controllers, and the houses on them.
--
-- city_plots has one row per district ever written, keyed by its playfield and its controller's index in the
-- playfield's RDB 1000039 record; a district for sale has no OrganizationId. An organization owns at most one district
-- (UX_city_plots_organization). A purchase claims the district (ClaimPending) before the buyer's credits are stored, and
-- the claim is confirmed after; a claim never confirmed is given up. Version grows with every change, so every zone
-- instance knows which districts to read again.
--
-- city_houses holds every house of a district. A Pending house holds its tiles while the builder's house item is taken;
-- it stands once confirmed. DemolishAtUtc marks a house being demolished and when it goes.
--
-- Additive and safe to re-run.

CREATE TABLE IF NOT EXISTS `city_plots` (
    `PlayfieldId` INT NOT NULL,
    `ControllerIndex` INT NOT NULL,
    `OrganizationId` INT NULL,
    `OwnershipId` BIGINT NOT NULL DEFAULT 0,
    `ClaimPending` TINYINT(1) NOT NULL DEFAULT 0,
    `ClaimedAtUtc` DATETIME(6) NULL,
    `PurchasedBy` INT NULL,
    `PurchasedAtUtc` DATETIME(6) NULL,
    `NextBillAtUtc` DATETIME(6) NULL,
    `UpkeepWarnedForUtc` DATETIME(6) NULL,
    `EmptySinceUtc` DATETIME(6) NULL,
    `HeadquartersBuiltAtUtc` DATETIME(6) NULL,
    `CloakState` INT NOT NULL DEFAULT 0,
    `CloakToggledAtUtc` DATETIME(6) NULL,
    `ControllerCharge` FLOAT NOT NULL DEFAULT 0,
    `LockoutOrganizationId` INT NULL,
    `LockoutUntilUtc` DATETIME(6) NULL,
    `Version` BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (`PlayfieldId`, `ControllerIndex`),
    UNIQUE KEY `UX_city_plots_organization` (`OrganizationId`),
    KEY `IX_city_plots_bill` (`NextBillAtUtc`),
    CONSTRAINT `CK_city_plots_charge` CHECK (`ControllerCharge` >= 0 AND `ControllerCharge` <= 1)
)
COMMENT='Org city districts; one per organization; Version grows with every change'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS `city_houses` (
    `HouseId` BIGINT NOT NULL AUTO_INCREMENT,
    `PlayfieldId` INT NOT NULL,
    `ControllerIndex` INT NOT NULL,
    `TemplateId` INT NOT NULL,
    `TileX` INT NOT NULL,
    `TileZ` INT NOT NULL,
    `Rotation` INT NOT NULL,
    `BuiltBy` INT NOT NULL,
    `Pending` TINYINT(1) NOT NULL DEFAULT 1,
    `CreatedAtUtc` DATETIME(6) NOT NULL,
    `DemolishAtUtc` DATETIME(6) NULL,
    `ItemLowId` INT NOT NULL,
    `ItemHighId` INT NOT NULL,
    `ItemQuality` INT NOT NULL,
    PRIMARY KEY (`HouseId`),
    UNIQUE KEY `UX_city_houses_tile` (`PlayfieldId`, `ControllerIndex`, `TileX`, `TileZ`),
    KEY `IX_city_houses_demolish` (`DemolishAtUtc`),
    CONSTRAINT `FK_city_houses_plot` FOREIGN KEY (`PlayfieldId`, `ControllerIndex`)
        REFERENCES `city_plots` (`PlayfieldId`, `ControllerIndex`) ON DELETE CASCADE,
    CONSTRAINT `CK_city_houses_rotation` CHECK (`Rotation` IN (0, 90, 180, 270))
)
COMMENT='Houses of org city districts; Pending holds tiles until the house item is taken'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
ENGINE=InnoDB;
