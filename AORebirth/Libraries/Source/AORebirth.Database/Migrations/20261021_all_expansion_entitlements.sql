-- Operator migration: verify a restorable database backup and stop account/character
-- writers before applying. Execute this complete file through MySqlConnector,
-- matching DatabaseMigrationTool's batch execution (no client DELIMITER commands).
-- The normalized stats DAO stores Expansion as Type=50000, StatId=389; never
-- replace a character's complete stat collection for this correction.
-- This data/default correction is independent of ZoneEngine schema readiness.

DROP PROCEDURE IF EXISTS `aorebirth_migrate_all_expansion_entitlements`;
CREATE PROCEDURE `aorebirth_migrate_all_expansion_entitlements`()
BEGIN
    DECLARE accounts_changed BIGINT DEFAULT 0;
    DECLARE characters_changed BIGINT DEFAULT 0;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    -- Rollback must cover both target tables. Refuse nontransactional storage.
    IF (SELECT COUNT(*) FROM information_schema.TABLES
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('login', 'characters', 'stats')
            AND ENGINE = 'InnoDB') <> 3 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Expansion migration requires InnoDB login, characters and stats';
    END IF;

    START TRANSACTION;

    -- Existing account-linked characters must already have their persisted stat.
    -- Missing stats require investigation; this migration never invents a row.
    IF EXISTS (
        SELECT 1 FROM `characters` c
        INNER JOIN `login` l ON l.`Username` = c.`Username`
        LEFT JOIN `stats` s ON s.`Type` = 50000 AND s.`Instance` = c.`Id` AND s.`StatId` = 389
        WHERE s.`Id` IS NULL
    ) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Account-linked character is missing persisted expansion stat 389';
    END IF;

    UPDATE `login` SET `Expansions` = 2047 WHERE `Expansions` <> 2047;
    SET accounts_changed = ROW_COUNT();

    UPDATE `stats` s
    INNER JOIN `characters` c ON c.`Id` = s.`Instance`
    INNER JOIN `login` l ON l.`Username` = c.`Username`
    SET s.`StatValue` = 2047
    WHERE s.`Type` = 50000 AND s.`StatId` = 389 AND s.`StatValue` <> 2047;
    SET characters_changed = ROW_COUNT();

    COMMIT;
    SELECT accounts_changed AS `AccountsUpdated`, characters_changed AS `CharactersUpdated`;
END;
CALL `aorebirth_migrate_all_expansion_entitlements`();
DROP PROCEDURE `aorebirth_migrate_all_expansion_entitlements`;

-- MySQL DDL implicitly commits. Set only the column default after the data
-- transaction completes; a failed ALTER can safely be retried with this file.
ALTER TABLE `login` ALTER COLUMN `Expansions` SET DEFAULT 2047;
