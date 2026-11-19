-- Names a player gave their own bags (backpacks): one row per character and bag, keyed by the bag's item instance
-- (its 0xC749 Container identity). A row belongs to the character who named the bag: a traded bag reaches its new
-- owner without one, and the old owner's row is dropped. Additive and safe to re-run.

CREATE TABLE IF NOT EXISTS `characterbagnames` (
	`CharacterId` INT(32) NOT NULL,
	`BagInstanceId` INT(32) NOT NULL,
	`Name` VARCHAR(32) NOT NULL,
	PRIMARY KEY (`CharacterId`, `BagInstanceId`)
)
COLLATE='latin1_general_ci'
ENGINE=InnoDB;
