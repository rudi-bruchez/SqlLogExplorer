-- Génère un .trn de test minimal pour LocalDbBackendIntegrationTests.
-- À exécuter une fois sur (localdb)\SqlLogExplorerInstance. Adapter les chemins.
CREATE DATABASE SqlLogExplorer_TestSrc;
GO
ALTER DATABASE SqlLogExplorer_TestSrc SET RECOVERY FULL;
GO
BACKUP DATABASE SqlLogExplorer_TestSrc TO DISK = N'C:\Temp\SqlLogExplorer_TestSrc_full.bak';
GO
USE SqlLogExplorer_TestSrc;
CREATE TABLE dbo.Clients (Id INT NOT NULL PRIMARY KEY, Nom VARCHAR(50) NOT NULL);
INSERT INTO dbo.Clients (Id, Nom) VALUES (1, 'Alice'), (2, 'Bob');
DELETE FROM dbo.Clients WHERE Id = 2;
GO
-- Le .trn à passer via SQLLOGEXPLORER_TEST_TRN :
BACKUP LOG SqlLogExplorer_TestSrc TO DISK = N'C:\Temp\SqlLogExplorer_TestSrc.trn';
GO
