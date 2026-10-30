CREATE TABLE `dailyloginclaims` (
    `Id` BIGINT NOT NULL AUTO_INCREMENT,
    `AccountId` INT NOT NULL,
    `ClaimToken` VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    `CharacterId` INT NOT NULL,
    `ClaimDateUtc` DATE NULL,
    `StateJson` MEDIUMTEXT NOT NULL,
    `ResultJson` MEDIUMTEXT NOT NULL,
    `IsLegacyImport` TINYINT UNSIGNED NOT NULL DEFAULT 0,
    `CreatedAtUtcTicks` BIGINT NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `UX_dailyloginclaims_token` (`ClaimToken`),
    UNIQUE KEY `UX_dailyloginclaims_character_day` (`CharacterId`, `ClaimDateUtc`),
    KEY `IX_dailyloginclaims_latest` (`AccountId`, `Id`),
    KEY `IX_dailyloginclaims_character_latest` (`AccountId`, `CharacterId`, `Id`),
    CONSTRAINT `CK_dailyloginclaims_state_json` CHECK (JSON_VALID(`StateJson`)),
    CONSTRAINT `CK_dailyloginclaims_result_json` CHECK (JSON_VALID(`ResultJson`)),
    CONSTRAINT `CK_dailyloginclaims_kind` CHECK (`IsLegacyImport` IN (0, 1) AND (`IsLegacyImport` = 1 OR `ClaimDateUtc` IS NOT NULL))
)
COMMENT='Atomic character daily-login receipts; account ownership and original history are retained'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
ENGINE=InnoDB;
