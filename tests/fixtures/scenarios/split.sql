CREATE TABLE dbo.sp (id int NOT NULL PRIMARY KEY, a int NULL, data char(1000) NOT NULL DEFAULT 'd');
INSERT dbo.sp (id, a) SELECT v * 2, v FROM (VALUES (1),(2),(3),(4),(5),(6),(7)) AS x(v);
INSERT dbo.sp (id, a) VALUES (3, 3);
UPDATE dbo.sp SET a = 40 WHERE id = 4;
CREATE TABLE dbo.ov (id int NOT NULL PRIMARY KEY, v1 varchar(5000) NULL, v2 varchar(5000) NULL, m varchar(max) NULL);
INSERT dbo.ov VALUES (1, REPLICATE('c', 5000), REPLICATE('d', 5000), REPLICATE(CONVERT(varchar(max), 'm'), 10000));
