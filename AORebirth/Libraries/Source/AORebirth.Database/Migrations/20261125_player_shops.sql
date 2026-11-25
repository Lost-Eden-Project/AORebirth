-- Player shops: a "Shop for Rent" terminal (identity type 0xC790) a character rents and stocks.
--
-- player_shops has one row per rented terminal, keyed by its own ShopId (an item instance id the zone allocated, so
-- every zone instance hands out distinct ones). A terminal is rented once (UX_player_shops_terminal: its playfield and
-- its Dynels.dat instance) and a character rents at most one shop (UX_player_shops_owner). Balance holds the credits
-- the shop's sales took in; its owner withdraws them at the terminal. A shop is abandoned (its row deleted) only with
-- a zero balance and nothing for sale.
--
-- The items for sale stay in item_instances, keeping their InstanceId: ContainerType 0x790 (PlayerShopInventory),
-- ContainerInstance the ShopId, ContainerPlacement the shop slot. player_shop_items holds each one's asking price.
--
-- Additive and safe to re-run.

CREATE TABLE IF NOT EXISTS `player_shops` (
    `ShopId` INT NOT NULL,
    `PlayfieldId` INT NOT NULL,
    `TerminalInstance` INT NOT NULL,
    `OwnerId` INT NOT NULL,
    `Name` VARCHAR(64) NOT NULL,
    `Balance` BIGINT NOT NULL DEFAULT 0,
    `RentedAtUtc` DATETIME(6) NOT NULL,
    PRIMARY KEY (`ShopId`),
    UNIQUE KEY `UX_player_shops_terminal` (`PlayfieldId`, `TerminalInstance`),
    UNIQUE KEY `UX_player_shops_owner` (`OwnerId`),
    CONSTRAINT `CK_player_shops_balance` CHECK (`Balance` >= 0)
)
COMMENT='Rented player shop terminals; one per character'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS `player_shop_items` (
    `ItemInstanceId` INT NOT NULL,
    `ShopId` INT NOT NULL,
    `Price` INT NOT NULL,
    PRIMARY KEY (`ItemInstanceId`),
    KEY `IX_player_shop_items_shop` (`ShopId`),
    CONSTRAINT `CK_player_shop_items_price` CHECK (`Price` > 0)
)
COMMENT='Asking price of each item a player shop has for sale (the item row is item_instances, container 0x790)'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
ENGINE=InnoDB;
