SET NOCOUNT ON;
DECLARE @log nvarchar(max) = (
  SELECT [Current LSN] AS Lsn, [Operation] AS Operation, [Context] AS Context, [Transaction ID] AS TransactionId,
         [AllocUnitName] AS AllocUnitName, [AllocUnitId] AS AllocUnitId, [PartitionId] AS PartitionId,
         [Page ID] AS PageId, [Slot ID] AS SlotId, [Offset in Row] AS OffsetInRow, [Modify Size] AS ModifySize,
         [Num Elements] AS NumElements, [Description] AS Description, [Transaction Name] AS TransactionName,
         [RowLog Contents 0] AS Rlc0, [RowLog Contents 1] AS Rlc1, [RowLog Contents 2] AS Rlc2,
         [RowLog Contents 3] AS Rlc3, [RowLog Contents 4] AS Rlc4, [RowLog Contents 5] AS Rlc5,
         [Lock Information] AS LockInformation, [Log Record] AS LogRecord
  FROM sys.fn_dblog('$(StartLsn)', NULL)
  WHERE ISNULL([AllocUnitName], N'') NOT LIKE N'sys.%' AND ISNULL([AllocUnitName], N'') NOT LIKE N'dbo.SchemaSnapshot%'
  FOR JSON PATH, INCLUDE_NULL_VALUES);
EXEC dbo.SnapshotSchema;
DECLARE @schema nvarchar(max) = (SELECT DISTINCT * FROM dbo.SchemaSnapshot WHERE TableName <> N'SchemaSnapshot' FOR JSON PATH, INCLUDE_NULL_VALUES);
CREATE TABLE #page (ParentObject nvarchar(255), Object nvarchar(255), Field nvarchar(255), Value nvarchar(max));
CREATE TABLE #pages (Style int, PageId varchar(20), ParentObject nvarchar(255), Object nvarchar(255), Field nvarchar(255), Value nvarchar(max));
DECLARE @f int, @p int, @sql nvarchar(400);
DECLARE pc CURSOR LOCAL FAST_FORWARD FOR
  SELECT allocated_page_file_id, allocated_page_page_id
  FROM sys.dm_db_database_page_allocations(DB_ID(), NULL, NULL, NULL, 'DETAILED')
  WHERE is_allocated = 1 AND page_type_desc = 'DATA_PAGE'
    AND OBJECTPROPERTY(object_id, 'IsMsShipped') = 0 AND object_id <> OBJECT_ID('dbo.SchemaSnapshot');
OPEN pc; FETCH NEXT FROM pc INTO @f, @p;
WHILE @@FETCH_STATUS = 0
BEGIN
  DELETE #page; SET @sql = N'DBCC PAGE (fx, ' + CAST(@f AS nvarchar(10)) + N', ' + CAST(@p AS nvarchar(10)) + N', 1) WITH TABLERESULTS, NO_INFOMSGS';
  INSERT #page EXEC (@sql);
  INSERT #pages SELECT 1, FORMAT(@f, '0000') + ':' + FORMAT(@p, 'X8'), ParentObject, Object, Field, Value FROM #page;
  DELETE #page; SET @sql = REPLACE(@sql, N', 1) WITH', N', 3) WITH');
  INSERT #page EXEC (@sql);
  INSERT #pages SELECT 3, FORMAT(@f, '0000') + ':' + FORMAT(@p, 'X8'), ParentObject, Object, Field, Value FROM #page;
  FETCH NEXT FROM pc INTO @f, @p;
END
CLOSE pc; DEALLOCATE pc;
SELECT (SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(40)) AS ProductVersion,
               JSON_QUERY(@log) AS Log, JSON_QUERY(@schema) AS [Schema],
               JSON_QUERY((SELECT * FROM #pages FOR JSON PATH)) AS Pages
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
