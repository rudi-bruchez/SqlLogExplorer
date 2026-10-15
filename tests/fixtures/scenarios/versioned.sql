-- options: RCSI
CREATE TABLE dbo.vr (id int NOT NULL PRIMARY KEY, a int NULL, b varchar(20) NULL);
INSERT dbo.vr VALUES (1, 10, 'abc');
UPDATE dbo.vr SET a = 11 WHERE id = 1;
UPDATE dbo.vr SET a = 12 WHERE id = 1;
UPDATE dbo.vr SET b = 'abcd' WHERE id = 1;
BEGIN TRAN; SAVE TRAN s; UPDATE dbo.vr SET a = 99 WHERE id = 1; ROLLBACK TRAN s; COMMIT;
DELETE dbo.vr WHERE id = 1;
