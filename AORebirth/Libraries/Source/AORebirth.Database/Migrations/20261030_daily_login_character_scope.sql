-- Per-character daily rewards. Run only with the governed backup/writer-stop prerequisites.
-- Preserve every receipt, token, account ID, state and result byte. Ambiguous old
-- account progress needs explicit reconciliation; never reset or copy it to characters.
DROP PROCEDURE IF EXISTS `aorebirth_daily_login_character_scope`;
CREATE PROCEDURE `aorebirth_daily_login_character_scope`()
BEGIN
    DECLARE old_index_count INT DEFAULT 0;
    DECLARE character_index_count INT DEFAULT 0;
    SELECT COUNT(*) INTO old_index_count FROM information_schema.STATISTICS
      WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='dailyloginclaims'
        AND INDEX_NAME='UX_dailyloginclaims_account_day';
    SELECT COUNT(*) INTO character_index_count FROM (
      SELECT INDEX_NAME FROM information_schema.STATISTICS
      WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='dailyloginclaims'
        AND INDEX_NAME='UX_dailyloginclaims_character_day' AND NON_UNIQUE=0
      GROUP BY INDEX_NAME HAVING GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX)='CharacterId,ClaimDateUtc'
    ) existing_character_index;
    IF EXISTS (SELECT INDEX_NAME FROM information_schema.STATISTICS
        WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='dailyloginclaims' AND NON_UNIQUE=0
          AND INDEX_NAME<>'UX_dailyloginclaims_account_day'
        GROUP BY INDEX_NAME HAVING GROUP_CONCAT(COLUMN_NAME ORDER BY COLUMN_NAME)='AccountId,ClaimDateUtc') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Daily character scope found an unrecognized account-wide unique key; all rows retained';
    END IF;
    IF old_index_count > 0 THEN
        IF EXISTS (SELECT 1 FROM dailyloginclaims WHERE IsLegacyImport=1 AND (
            COALESCE(JSON_LENGTH(JSON_EXTRACT(StateJson,'$.Taken')),JSON_LENGTH(JSON_EXTRACT(StateJson,'$.taken')),0)>0
            OR COALESCE(JSON_EXTRACT(StateJson,'$.ClaimedCount'),JSON_EXTRACT(StateJson,'$.claimedCount'),0)>0
            OR COALESCE(JSON_UNQUOTE(JSON_EXTRACT(StateJson,'$.LastGrantedUtc')),JSON_UNQUOTE(JSON_EXTRACT(StateJson,'$.lastGrantedUtc')),'') NOT IN ('','null')
            OR COALESCE(JSON_UNQUOTE(JSON_EXTRACT(StateJson,'$.LastClaimUtc')),JSON_UNQUOTE(JSON_EXTRACT(StateJson,'$.lastClaimUtc')),'') NOT IN ('','null')
            OR COALESCE(JSON_UNQUOTE(JSON_EXTRACT(StateJson,'$.CycleCompletedOn')),JSON_UNQUOTE(JSON_EXTRACT(StateJson,'$.cycleCompletedOn')),'') NOT IN ('','null')
        )) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Daily character scope requires reconciliation of nonempty legacy account progress; all rows retained';
        END IF;
        IF EXISTS (SELECT AccountId FROM dailyloginclaims WHERE IsLegacyImport=0
            GROUP BY AccountId HAVING COUNT(DISTINCT CharacterId)>1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Daily character scope requires reconciliation of mixed-character account receipts; all rows retained';
        END IF;
        IF EXISTS (SELECT 1 FROM dailyloginclaims WHERE CharacterId<=0) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Daily character scope requires a positive attributed character on every receipt; all rows retained';
        END IF;
        ALTER TABLE dailyloginclaims
          DROP INDEX UX_dailyloginclaims_account_day,
          ADD UNIQUE KEY UX_dailyloginclaims_character_day (CharacterId,ClaimDateUtc),
          ADD KEY IX_dailyloginclaims_character_latest (AccountId,CharacterId,Id);
    ELSEIF character_index_count<>1 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Daily character scope found an unsupported index shape; all rows retained';
    END IF;
END;
CALL `aorebirth_daily_login_character_scope`();
DROP PROCEDURE `aorebirth_daily_login_character_scope`;
