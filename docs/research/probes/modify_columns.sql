-- Sonde tâche 4 : formes de LOP_MODIFY_COLUMNS sans versionnement.
CREATE TABLE dbo.mw (id int NOT NULL PRIMARY KEY, a int NULL, x char(50) NULL, b int NULL, e int NULL, y char(50) NULL,
                     c varchar(20) NULL, w varchar(100) NULL, d varchar(20) NULL);
INSERT dbo.mw VALUES (1, 10, 'x', 20, 30, 'y', 'cc', 'abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghij', 'dd');
UPDATE dbo.mw SET a = 11, b = 21, e = 31 WHERE id = 1;
UPDATE dbo.mw SET c = 'cccc', d = 'ee' WHERE id = 1;
UPDATE dbo.mw SET a = 12, c = 'c' WHERE id = 1;
UPDATE dbo.mw SET a = NULL, c = NULL WHERE id = 1;
UPDATE dbo.mw SET a = 13, c = 'back' WHERE id = 1;
UPDATE dbo.mw SET b = NULL, e = NULL WHERE id = 1;
UPDATE dbo.mw SET b = 22, e = 32 WHERE id = 1;
UPDATE dbo.mw SET a = 1, b = 2, e = 3, c = 'x', d = 'y' WHERE id = 1;
UPDATE dbo.mw SET d = NULL, a = 7 WHERE id = 1;
UPDATE dbo.mw SET d = 'dback', a = 8 WHERE id = 1;
BEGIN TRAN; UPDATE dbo.mw SET c = 'rollback-long', d = 'r' WHERE id = 1; ROLLBACK;
BEGIN TRAN; UPDATE dbo.mw SET a = 500, b = 600 WHERE id = 1; ROLLBACK;
BEGIN TRAN; SAVE TRAN s; UPDATE dbo.mw SET a = 501, d = 'savepoint' WHERE id = 1; ROLLBACK TRAN s; COMMIT;
UPDATE dbo.mw SET x = 'xx', y = 'yy' WHERE id = 1;
CREATE TABLE dbo.mh (id int NOT NULL, a int NULL, x char(50) NULL, b int NULL, c varchar(20) NULL);
INSERT dbo.mh VALUES (1, 10, 'x', 20, 'cc');
UPDATE dbo.mh SET a = 11, b = 21;
UPDATE dbo.mh SET a = 12, c = 'cccc';
