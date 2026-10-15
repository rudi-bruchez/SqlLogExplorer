#!/usr/bin/env bash
# Démarre ou arrête un SQL Server de test sous podman (spec03 §10.1).
# up <2017|2019|2022|2025> <nom> : attend que le serveur réponde, puis imprime les lignes export
#   SQLLOGEXPLORER_RUN_INTEGRATION et SQLLOGEXPLORER_TEST_SQL (à évaluer dans la MÊME invocation du shell).
# down <nom> : supprime le conteneur.
set -euo pipefail
PASSWORD='Fixture#2026x'
sqlcmd_path() { if [ "$1" = 2017 ]; then echo "/opt/mssql-tools/bin/sqlcmd"; else echo "/opt/mssql-tools18/bin/sqlcmd -C"; fi; }
case "${1:-}" in
  up)
    version=$2 name=$3
    podman run -d --name "$name" -p 127.0.0.1::1433 -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PASSWORD" \
      "mcr.microsoft.com/mssql/server:${version}-latest" >/dev/null
    for _ in $(seq 1 90); do
      podman exec "$name" $(sqlcmd_path "$version") -S localhost -U sa -P "$PASSWORD" -Q 'SELECT 1' >/dev/null 2>&1 && break
      sleep 2
    done
    port=$(podman port "$name" 1433 | head -1 | cut -d: -f2)
    echo "export SQLLOGEXPLORER_RUN_INTEGRATION=1"
    echo "export SQLLOGEXPLORER_TEST_SQL='Server=127.0.0.1,$port;User ID=sa;Password=$PASSWORD;TrustServerCertificate=True;Database=master'" ;;
  down) podman rm -f -t 2 "$2" >/dev/null ;;
  *) echo "usage: $0 up <version> <name> | down <name>" >&2; exit 2 ;;
esac
