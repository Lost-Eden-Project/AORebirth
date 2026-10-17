-- Persistent nano line (strain) cooldowns from a nano's local cooldown (stat 254): one row per character and
-- locked strain, absolute UTC expiry so logging out does not pause it. Additive and safe to re-run.

CREATE TABLE IF NOT EXISTS `characternanolinelocks` (
	`CharacterId` INT(32) NOT NULL,
	`NanoStrain` INT(32) NOT NULL,
	`ExpiresAtUtcTicks` BIGINT(20) NOT NULL,
	PRIMARY KEY (`CharacterId`, `NanoStrain`)
)
COLLATE='latin1_general_ci'
ENGINE=InnoDB;
