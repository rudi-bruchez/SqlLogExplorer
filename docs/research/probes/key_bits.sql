-- Sonde tâche 4 : place et numéro des bits de clé dans l'enregistrement d'index de RowLog Contents 2.
CREATE TABLE dbo.kbb (b1 bit NOT NULL, id int NOT NULL, b2 bit NOT NULL, x bit NULL, v int NULL, PRIMARY KEY (b1, id, b2));
INSERT dbo.kbb VALUES (1, 1, 0, 1, 1), (0, 2, 1, 1, 2), (1, 3, 1, 0, 3);
UPDATE dbo.kbb SET v = v + 10;
UPDATE dbo.kbb SET x = 1 - x;
UPDATE dbo.kbb SET v = v + 10;
CREATE TABLE dbo.kbr (b1 bit NOT NULL, b2 bit NOT NULL, v int NULL, PRIMARY KEY (b2, b1));
INSERT dbo.kbr VALUES (1, 0, 1), (0, 1, 2);
UPDATE dbo.kbr SET v = v + 10;
CREATE TABLE dbo.kbn (id int NOT NULL, b bit NULL, v int NULL);
CREATE CLUSTERED INDEX cx ON dbo.kbn (id, b);
INSERT dbo.kbn VALUES (1, NULL, 1), (1, 1, 2), (1, 1, 3);
UPDATE dbo.kbn SET v = v + 10;
