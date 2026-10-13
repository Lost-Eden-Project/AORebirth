CREATE TABLE IF NOT EXISTS `characterresearch` (
	`CharacterId` INT(32) NOT NULL,
	`ResearchId` INT(32) NOT NULL,
	`RemainingXp` INT(32) NOT NULL,
	PRIMARY KEY (`CharacterId`, `ResearchId`)
)
COLLATE='latin1_general_ci'
ENGINE=InnoDB;
