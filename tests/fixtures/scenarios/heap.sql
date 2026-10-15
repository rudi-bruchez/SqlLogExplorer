CREATE TABLE dbo.h (id int NOT NULL, v int NOT NULL, pad char(50) NOT NULL DEFAULT 'p', big varchar(7000) NULL);
INSERT dbo.h (id, v) VALUES (1, 10);
UPDATE dbo.h SET v = 11 WHERE id = 1;
DELETE dbo.h WHERE id = 1;
INSERT dbo.h (id, v) VALUES (2, 20);
INSERT dbo.h (id, v) SELECT TOP (100) 100 + ROW_NUMBER() OVER (ORDER BY (SELECT 1)), 1 FROM sys.all_objects;
UPDATE dbo.h SET big = REPLICATE('x', 7000) WHERE id = 101;
