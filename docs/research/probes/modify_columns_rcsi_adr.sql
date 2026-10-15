-- options: RCSI ADR
-- Sonde tâche 4 : mises à jour d'une colonne sous RCSI et ADR, NULL et retour, compensations.
CREATE TABLE dbo.vr2 (id int NOT NULL PRIMARY KEY, a int NULL, b varchar(20) NULL, c int NULL);
INSERT dbo.vr2 VALUES (1, 10, 'abc', 30);
UPDATE dbo.vr2 SET a = 11 WHERE id = 1;
UPDATE dbo.vr2 SET a = 12 WHERE id = 1;
UPDATE dbo.vr2 SET a = NULL WHERE id = 1;
UPDATE dbo.vr2 SET a = 13 WHERE id = 1;
UPDATE dbo.vr2 SET b = NULL WHERE id = 1;
UPDATE dbo.vr2 SET b = 'back' WHERE id = 1;
UPDATE dbo.vr2 SET b = 'a longer value' WHERE id = 1;
UPDATE dbo.vr2 SET b = 's' WHERE id = 1;
UPDATE dbo.vr2 SET a = 14, c = 31 WHERE id = 1;
BEGIN TRAN; UPDATE dbo.vr2 SET b = 'rolled back long' WHERE id = 1; ROLLBACK;
BEGIN TRAN; UPDATE dbo.vr2 SET a = 99 WHERE id = 1; ROLLBACK;
UPDATE dbo.vr2 SET c = 32 WHERE id = 1;
DELETE dbo.vr2 WHERE id = 1;
