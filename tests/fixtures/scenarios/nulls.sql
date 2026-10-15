CREATE TABLE dbo.nulls (id int NOT NULL PRIMARY KEY, n1 int NULL, v1 varchar(10) NULL, v2 varchar(10) NULL, v3 varchar(10) NULL);
INSERT dbo.nulls VALUES (1, 30, NULL, NULL, NULL);
INSERT dbo.nulls VALUES (2, NULL, 'V1', NULL, 'V3');
INSERT dbo.nulls VALUES (3, NULL, 'V1', '', 'V3');
INSERT dbo.nulls VALUES (4, 7, 'V1', NULL, NULL);
UPDATE dbo.nulls SET n1 = NULL WHERE id = 1;
INSERT dbo.nulls VALUES (5, NULL, NULL, NULL, NULL);
