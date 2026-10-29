-- Normal binary rollback retains dailyloginclaims and its JSON mirrors. Never
-- remove a populated receipt ledger: doing so could regrant already earned days.
-- This optional reverse migration is ONLY for an empty, stopped development DB.
-- A refusal leaves the table, inventory and migration ledger unchanged. Remove
-- this version's guard procedure after inspecting a refusal, then retain the data.
DELIMITER $$
CREATE PROCEDURE aorebirth_daily_login_20261029_empty_rollback()
BEGIN
    IF EXISTS (SELECT 1 FROM dailyloginclaims LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Daily-login rollback refused: preserve populated claim ledger';
    END IF;
    DROP TABLE dailyloginclaims;
    IF EXISTS (SELECT 1 FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='schema_migrations') THEN
        DELETE FROM schema_migrations WHERE MigrationName='20261029_daily_login_claims.sql';
    END IF;
END$$
CALL aorebirth_daily_login_20261029_empty_rollback()$$
DROP PROCEDURE aorebirth_daily_login_20261029_empty_rollback$$
DELIMITER ;
