-- Additive daily-login receipt storage. Existing account JSON is imported once by
-- its authenticated account owner. Inventory and a successful receipt commit in
-- one transaction; this migration never grants, resets or copies player rewards.
CREATE TABLE IF NOT EXISTS `dailyloginclaims` (
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
    UNIQUE KEY `UX_dailyloginclaims_account_day` (`AccountId`, `ClaimDateUtc`),
    KEY `IX_dailyloginclaims_latest` (`AccountId`, `Id`),
    CONSTRAINT `CK_dailyloginclaims_state_json` CHECK (JSON_VALID(`StateJson`)),
    CONSTRAINT `CK_dailyloginclaims_result_json` CHECK (JSON_VALID(`ResultJson`)),
    CONSTRAINT `CK_dailyloginclaims_kind` CHECK (`IsLegacyImport` IN (0, 1) AND (`IsLegacyImport` = 1 OR `ClaimDateUtc` IS NOT NULL))
)
COMMENT='Atomic account daily-login receipts; legacy import is immutable and never grants items'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
ENGINE=InnoDB;
