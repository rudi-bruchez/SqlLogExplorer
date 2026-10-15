CREATE TABLE dbo.co (id int NOT NULL PRIMARY KEY, gr varchar(10) COLLATE Greek_CI_AS, ja varchar(10) COLLATE Japanese_CI_AS);
INSERT dbo.co VALUES (1, N'αβγ', N'日本語');
CREATE TABLE dbo.u8 (id int NOT NULL PRIMARY KEY, s varchar(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8); -- 2019+
INSERT dbo.u8 VALUES (1, N'Grüße 日本'); -- 2019+
