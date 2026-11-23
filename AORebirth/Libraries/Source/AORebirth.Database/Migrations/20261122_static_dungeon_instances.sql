-- Live static dungeon instances (fixed ACG dungeons behind world doors): one row per instance, from its build until
-- it is released, keyed by its dungeon Playfield2 id. A character stored inside one logs back into it while the row's
-- instance is live; after a restart the row only names the entrance to land them in front of. Zone instances share
-- the rows, so one team gets one instance per entrance whichever zone its members enter from. Additive and safe to
-- re-run.

CREATE TABLE IF NOT EXISTS `staticdungeoninstances` (
	`PlayfieldId` INT(32) NOT NULL,
	`EntranceType` INT(32) UNSIGNED NOT NULL,
	`EntranceInstance` INT(32) UNSIGNED NOT NULL,
	`OwnerKind` TINYINT(4) NOT NULL,
	`OwnerId` INT(32) NOT NULL,
	`CreatedAtUtcTicks` BIGINT(64) NOT NULL,
	PRIMARY KEY (`PlayfieldId`),
	UNIQUE KEY `uk_staticdungeon_owner` (`EntranceType`, `EntranceInstance`, `OwnerKind`, `OwnerId`),
	KEY `ix_staticdungeon_created` (`CreatedAtUtcTicks`)
)
COLLATE='latin1_general_ci'
ENGINE=InnoDB;
