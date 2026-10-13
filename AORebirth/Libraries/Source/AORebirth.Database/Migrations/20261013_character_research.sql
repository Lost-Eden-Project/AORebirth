-- Personal research progress: one row per character and research tier being trained, with the research XP still
-- needed. A finished tier leaves this table and becomes a trained perk (charactersperks).
-- Additive and safe to re-run.

CREATE TABLE IF NOT EXISTS `characterresearch` (
	`CharacterId` INT(32) NOT NULL,
	`ResearchId` INT(32) NOT NULL,
	`RemainingXp` INT(32) NOT NULL,
	PRIMARY KEY (`CharacterId`, `ResearchId`)
)
COLLATE='latin1_general_ci'
ENGINE=InnoDB;
