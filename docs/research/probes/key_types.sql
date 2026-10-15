-- Sonde tâche 4 : enregistrement d'index de RowLog Contents 2, un type de clé par table.
CREATE TABLE dbo.kt_int (k int NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_int VALUES (305419896, 1); UPDATE dbo.kt_int SET v = 2;
CREATE TABLE dbo.kt_bigint (k bigint NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_bigint VALUES (1311768467463790320, 1); UPDATE dbo.kt_bigint SET v = 2;
CREATE TABLE dbo.kt_smallint (k smallint NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_smallint VALUES (-2, 1); UPDATE dbo.kt_smallint SET v = 2;
CREATE TABLE dbo.kt_tinyint (k tinyint NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_tinyint VALUES (200, 1); UPDATE dbo.kt_tinyint SET v = 2;
CREATE TABLE dbo.kt_decimal (k decimal(10,3) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_decimal VALUES (-1234.567, 1); UPDATE dbo.kt_decimal SET v = 2;
CREATE TABLE dbo.kt_date (k date NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_date VALUES ('2026-10-15', 1); UPDATE dbo.kt_date SET v = 2;
CREATE TABLE dbo.kt_datetime2 (k datetime2(7) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_datetime2 VALUES ('2026-10-15T12:34:56.1234567', 1); UPDATE dbo.kt_datetime2 SET v = 2;
CREATE TABLE dbo.kt_datetimeoffset (k datetimeoffset(7) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_datetimeoffset VALUES ('2026-10-15T12:34:56.1234567+02:00', 1); UPDATE dbo.kt_datetimeoffset SET v = 2;
CREATE TABLE dbo.kt_uniqueidentifier (k uniqueidentifier NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_uniqueidentifier VALUES ('01234567-89AB-CDEF-0123-456789ABCDEF', 1); UPDATE dbo.kt_uniqueidentifier SET v = 2;
CREATE TABLE dbo.kt_char (k char(5) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_char VALUES ('ab', 1); UPDATE dbo.kt_char SET v = 2;
CREATE TABLE dbo.kt_varchar (k varchar(10) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_varchar VALUES ('abc', 1); UPDATE dbo.kt_varchar SET v = 2;
CREATE TABLE dbo.kt_nchar (k nchar(3) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_nchar VALUES (N'é', 1); UPDATE dbo.kt_nchar SET v = 2;
CREATE TABLE dbo.kt_nvarchar (k nvarchar(10) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_nvarchar VALUES (N'éa', 1); UPDATE dbo.kt_nvarchar SET v = 2;
CREATE TABLE dbo.kt_binary (k binary(4) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_binary VALUES (0xABCD, 1); UPDATE dbo.kt_binary SET v = 2;
CREATE TABLE dbo.kt_varbinary (k varbinary(10) NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.kt_varbinary VALUES (0xABCDEF, 1); UPDATE dbo.kt_varbinary SET v = 2;
-- Bits de nullité : clé (c, a, b) non unique, colonnes déclarées a, b, c.
CREATE TABLE dbo.kn (a varchar(5) NULL, b int NULL, c int NULL, v int NULL);
CREATE CLUSTERED INDEX cx ON dbo.kn (c, a, b);
INSERT dbo.kn VALUES (NULL, 1, 1, 1), ('x', NULL, 1, 2), ('x', 1, NULL, 3), ('x', 1, 1, 4), ('x', 1, 1, 5);
UPDATE dbo.kn SET v = v + 10;
-- Bits de clé : deux bits dans la clé, à des positions différentes de celles de la ligne de données.
CREATE TABLE dbo.kb (id int NOT NULL, c bit NULL, d bit NULL, flag bit NOT NULL, g bit NOT NULL, v int NULL, CONSTRAINT pk_kb PRIMARY KEY (id, g, flag));
INSERT dbo.kb VALUES (1, 1, 1, 0, 1, 1), (2, 0, 0, 1, 0, 2), (3, 1, 1, 1, 1, 3);
UPDATE dbo.kb SET v = v + 10;
UPDATE dbo.kb SET c = 0, d = 1;
UPDATE dbo.kb SET v = v + 10;
-- Clé décroissante sur une colonne variable et une colonne fixe.
CREATE TABLE dbo.kdv (k varchar(10) NOT NULL, n int NOT NULL, v int NULL, PRIMARY KEY (k DESC, n DESC));
INSERT dbo.kdv VALUES ('abc', 5, 1), ('abd', 6, 2);
UPDATE dbo.kdv SET v = v + 10;
-- Uniqueifier sur clé variable, trois doublons.
CREATE TABLE dbo.ku (k varchar(5) NOT NULL, v int NULL);
CREATE CLUSTERED INDEX cx ON dbo.ku (k);
INSERT dbo.ku VALUES ('dup', 1), ('dup', 2), ('dup', 3);
UPDATE dbo.ku SET v = v + 10;
