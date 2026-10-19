# Writer tests

These check the code that writes to the database (`XIVFleetCompanion/FleetWriter.cs`). Each test creates the tables it
needs in a throwaway schema (`fleetwriter_test`), writes to them, reads the results back and compares. The schema is
removed when they finish. Some checks build an older database first, using the scripts in
[old-database](old-database/README.md), to make sure the plugin copes with one that's behind.

They run on GitHub for every pull request. To run them yourself, you need the .NET 8 SDK and a **test** PostgreSQL
(never your real one):

```
FLEET_TEST_DB="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet run --project tests/FleetWriterTests
```
