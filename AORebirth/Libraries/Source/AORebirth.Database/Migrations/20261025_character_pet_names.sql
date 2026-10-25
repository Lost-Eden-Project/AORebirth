-- Pet names a player gave with /pet rename: one row per character and pet type (the slot a summon fills), so a newly
-- summoned pet of that type takes the name again. Additive and safe to re-run.

CREATE TABLE IF NOT EXISTS `characterpetnames` (
	`CharacterId` INT(32) NOT NULL,
	`PetType` INT(32) NOT NULL,
	`Name` VARCHAR(32) NOT NULL,
	PRIMARY KEY (`CharacterId`, `PetType`)
)
COLLATE='latin1_general_ci'
ENGINE=InnoDB;
