# Batch controller tests

Run from the repository root with .NET 10 SDK:

```powershell
dotnet run --project source/tests/BatchTests.csproj -c Release
```

These 15 offline checks cover sequencing, cooldowns and stop conditions. They do not verify in-game interactions.
