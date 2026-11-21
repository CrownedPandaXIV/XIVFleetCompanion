# Plugin tests

These check the plugin's database code: what it writes (`FleetWriter.cs`, `DetailsWriter.cs`), what its pages read,
its edits, the fleet file (Just this PC), moving the fleet, backups and setup. Many compare the plugin's answers with
the app's: the app is the reference, and its answers are kept here as `*-fixture.json` files (see below). Some checks
build an older database first, using the scripts in [old-database](old-database/README.md), to make sure the plugin
copes with one that's behind.

They run on GitHub for every pull request, twice: in UTC and in New Zealand time (a day ahead of UTC).

## Running them yourself

**Use test PostgreSQL servers only, never your real one.** The tests make and drop databases and logins with fixed
names, with `DROP DATABASE ... WITH (FORCE)`, which throws out anyone connected to them.

You need:

- the .NET 8 SDK;
- two scratch PostgreSQL 16 servers: `FLEET_TEST_DB` and `FLEET_TEST_DB2` (the second plays "this PC" when a database
  is moved to it; without the variable the tests look for one on port 55434, and the move checks fail if there's none);
- PostgreSQL 16's `pg_dump` and `pg_restore` on the PATH (backups and moves use them);
- PowerShell (`pwsh`) for the check of the setup's administrator script. Without it that check is skipped, and the
  output says so. Set `FLEET_PWSH` if it isn't in /usr/bin, /usr/local/bin or /opt/microsoft/powershell/7.

```
FLEET_TEST_DB="Host=localhost;Username=postgres;Password=postgres;Database=postgres" \
FLEET_TEST_DB2="Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres" \
  dotnet run --project tests/FleetWriterTests
```

The last line says `ALL n CHECKS PASSED`, or lists what failed.

### What they make on the servers

Databases (dropped and made again each run, and dropped at the end): `twin_pg`, `big_pg`, `move_pg`, `move_pg2`,
`move_old`, `fleet_moved_check`, `fleet_edits_plugin`, `fleet_fight_club_plugin`, `backup_check`, `pdb_plugin_*`,
`setup_made`, `setup_not_leftover` and `setup_kept_before_move_*`. Logins: `pdb_reader`, `setup_maker`, `move_user`.
The writer checks also use a schema, `fleetwriter_test`, in the `FLEET_TEST_DB` database, removed when they finish.
Files go in the system's temp folder.

## The app's answers (`*-fixture.json` and `XIVFleetCompanion/Database/`)

`XIVFleetCompanion/Database/` is a copy of the app's `db/` folder (its tables, updates and views), and the fixtures are
the app's own answers for made-up fleets. When the app changes any of them, refresh the copies from the app's folder
(with a scratch PostgreSQL in `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`):

```
t=../XIVFleetCompanion/tests/FleetWriterTests
for f in parts routes trends edits fight-club calculator; do node tests/db/$f-fixture.js $t/$f-fixture.json; done
node tests/themes-fixture.js $t/themes-fixture.json
node tests/market-fixture.js $t/market-fixture.json
node tests/db/plugin-database-fixture.js ../XIVFleetCompanion
```

GitHub does the same with the app's branch of the same name (else `main`) before running the tests, and fails the
pull request when the copies here are out of date. `remote-access-fixture.json` (the setup's file edits) is written
by hand from the app's `setup/remote-access.js`.
