# Writer tests

Runs the plugin's database writing code (`XIVFleetCompanion/FleetWriter.cs`) against a scratch
Postgres: each table is created in a throwaway schema (`fleetwriter_test`) with the same column
types as the real database, written to, read back and compared. The schema is removed afterwards.

These run automatically on GitHub for every pull request (the `writer-tests` job in
`.github/workflows/pr-build.yml`). To run them yourself you need the .NET 8 SDK and a scratch
Postgres (never the real one):

```
FLEET_TEST_DB="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet run --project tests/FleetWriterTests
```
