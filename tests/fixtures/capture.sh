#!/usr/bin/env bash
# Fige les fixtures (spec03 §10.1) : pour chaque version et chaque scénario, base fx neuve, scénario, capture JSON.
# Usage : capture.sh [version…]   (défaut : 2017 2019 2022 2025)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out="${SLE_FIXTURE_OUT:-$here/../SqlLogExplorer.Tests/Fixtures}"
PASSWORD='Fixture#2026x'
if [ $# -eq 0 ]; then set -- 2017 2019 2022 2025; fi
versions=("$@")
snapshot_proc=$(sed 's/@AllocUnitId IS NULL OR //' "$here/../../Backends/Sql/TableSchema.sql" | sed 's/AND (au.allocation_unit_id = @AllocUnitId)//')
for v in "${versions[@]}"; do
  name="sle-it-capture-$v-$$"
  if [ "$v" = 2017 ]; then sq=(/opt/mssql-tools/bin/sqlcmd); else sq=(/opt/mssql-tools18/bin/sqlcmd -C); fi
  # -y 0 (pas de troncature des nvarchar(max)) est incompatible avec -h et -W : il ne sert qu'à la capture JSON.
  run() { podman exec -i "$name" "${sq[@]}" -S localhost -U sa -P "$PASSWORD" -b -I "$@"; }
  podman run -d --name "$name" -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PASSWORD" "mcr.microsoft.com/mssql/server:${v}-latest" >/dev/null
  trap 'podman rm -f -t 2 "$name" >/dev/null' EXIT
  until run -Q 'SELECT 1' >/dev/null 2>&1; do sleep 2; done
  mkdir -p "$out/$v"
  for f in "$here"/scenarios/*.sql; do
    scenario=$(basename "$f" .sql)
    opts=$(head -1 "$f" | sed -n 's/^-- options: //p')
    if [ "$v" = 2017 ] && [[ "$opts" == *ADR* ]]; then continue; fi
    run -Q "IF DB_ID('fx') IS NOT NULL BEGIN ALTER DATABASE fx SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE fx; END; CREATE DATABASE fx; ALTER DATABASE fx SET RECOVERY FULL; BACKUP DATABASE fx TO DISK = '/var/opt/mssql/data/fx.bak' WITH INIT;" >/dev/null
    [[ "$opts" == *RCSI* ]] && run -Q "ALTER DATABASE fx SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;" >/dev/null
    [[ "$opts" == *ADR* ]] && run -Q "ALTER DATABASE fx SET ACCELERATED_DATABASE_RECOVERY = ON;" >/dev/null
    { echo "CREATE TABLE dbo.SchemaSnapshot (AllocUnitId bigint, SchemaName sysname, TableName sysname, IndexId int, AllocUnitType int, PartitionColumnId int, ColumnName sysname NULL, LeafOffset int, LeafNullBit int, LeafBitPosition int, MaxInrowLength int, SystemTypeId int, [Precision] int, Scale int, CodePage int NULL, IsNullable bit, IsDropped bit, IsUniqueifier bit, HasDefault bit, DefaultValue nvarchar(4000) NULL, KeyOrdinal int NULL);"
      echo "GO"
      echo "CREATE PROCEDURE dbo.SnapshotSchema AS INSERT dbo.SchemaSnapshot $snapshot_proc"
      echo "GO"; } | run -d fx -i /dev/stdin >/dev/null
    start=$(run -d fx -h -1 -W -Q "SET NOCOUNT ON; SELECT MAX([Current LSN]) FROM sys.fn_dblog(NULL, NULL);" | tr -d '\r' | head -1)
    start_dec=$(python3 -I -c "import sys; print(':'.join(str(int(p,16)) for p in sys.argv[1].split(':')))" "$start")
    if [ "$v" = 2017 ]; then grep -v -- '-- 2019+' "$f"; else cat "$f"; fi | run -d fx -i /dev/stdin >/dev/null
    run -d fx -y 0 -v StartLsn="$start_dec" -i /dev/stdin < "$here/capture.sql" | tr -d '\r' | grep '^{' > "$out/$v/$scenario.json"
    python3 -I -c "import json,sys; json.load(open(sys.argv[1]))" "$out/$v/$scenario.json"
  done
  podman rm -f -t 2 "$name" >/dev/null; trap - EXIT
done
