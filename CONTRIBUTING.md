# Contributing

Bug reports and fixes are welcome: open an issue, or a pull request against `main`.

- **Needs:** the .NET 10 SDK, Slay the Spire 2 (beta branch) and Combat Statistics from the Workshop. `build.cs` finds
  the game through Steam, or `STS2_STEAMAPPS`.
- **Test:** `dotnet run tools/dev.cs test` runs the self-test in a separate copy of the game (your saves aren't
  touched). `dotnet run build.cs --dist`, then `dotnet run tools/dev.cs smoke`, checks the release build.
- **Keep it a fix:** none of Combat Statistics' code or files go in here. Patch it at runtime, and make each patch
  switch off when Combat Statistics no longer needs it.
