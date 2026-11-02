# Factory World 3D: notes for working on the project

A first-person factory game in Unreal Engine 5.8 (C++ and Slate, no Blueprints for game logic) with its own server
(`Server/`, ASP.NET Core on .NET 10, PostgreSQL). It has nothing in common with the 2D game (repository
`factory-world`): its own server, database, accounts and world. README.md describes the game; this file is the short
version of how to work on it.

## Rules
- Text the player sees is Polish; code, comments, commit messages and PR titles are English.
- Only assets free for commercial use: CC0, SIL OFL, our own (made by the scripts in `Tools/`). List every new one in
  ASSETS.md. Fab and MetaHuman assets stay local (`Content/MetaHumans` and `Content/External` are not in git).
- Never change the 2D game's repository.
- No passwords, keys or tokens in the repository. `Saved/DevLogin.ini` (auto-login) holds a LOCAL TEST account only and
  is not in git; never a real account.
- Nothing is deployed anywhere but this computer without Jakub's say-so.
- Pull requests target `main`; no PR based on another PR's branch. A branch may carry an unmerged PR's commits, then the
  PR says "contains #N, merge #N first".
- Before/after screenshots for every visible change (Tools/dev/autotest.ps1 makes them).

## Paths on this computer
- Unreal Engine: `C:\Program Files\Epic Games\UE_5.8`
- Blender: `D:\Programs\Blender\blender.exe` (asset scripts in `Tools/blender`, `Tools/make_audio.py`)
- PostgreSQL 17: `D:\Programs\PostgreSQL\17\bin`; the 3D game's own cluster `D:\Factory-World\pg3d`, port 5433,
  user `factory`, database `factoryworld3d` (local only)
- Everything at once (database, server, game): `D:\Factory-World\Start-FactoryWorld3D.bat`

## Build
```
powershell -ExecutionPolicy Bypass -File Tools\dev\build.ps1            # client and server
powershell -ExecutionPolicy Bypass -File Tools\dev\build.ps1 -Client
powershell -ExecutionPolicy Bypass -File Tools\dev\build.ps1 -Server    # Release: a running server does not lock it
```
The client is the editor target (`FactoryWorld3DEditor Win64 Development`); the game runs with
`UnrealEditor.exe FactoryWorld3D.uproject -game`. A running game locks the client's DLLs: close it before building.

## Server
- Run: `dotnet run --project Server/src/FactoryWorld.Server --launch-profile http` (port 5081, Development, the local
  database). The database: `D:\Programs\PostgreSQL\17\bin\pg_ctl.exe -D D:\Factory-World\pg3d -o "-p 5433" start`.
- Tests: `dotnet test Server/tests/FactoryWorld.Server.Tests -c Release` (each test class gets its own temporary database
  on port 5433; `--filter "FullyQualifiedName~ClassName"` for one class). About 2 minutes for all.
- Migrations: in the module's folder, `dotnet tool restore`, then
  `dotnet ef migrations add Name --startup-project ../../FactoryWorld.Server --context <Module>DbContext -o Migrations --configuration Release`.
- Simulation is lazy: each system catches up to "now" when asked. Plots run on their own clock (`IPlotClock`): they stand
  still while their players are away.
- Developer commands (`dev.world`, `dev.give`, `dev.tool`, `dev.plot`, `dev.machines`; `Server/src/FactoryWorld.Server/DevCommands.cs`)
  exist only on a Development server with `Dev:Commands` (appsettings.Development.json) and only for connections from
  this computer. In the game: console `FWDevWorld` (money, backpack, axe and pickaxe, three plots, test machines).
- Bots (server-run players, module Bots) are off unless `Bots:Enabled` is true; while off, the server removes the bots it
  made before, with all that is theirs, on start (`Bots:RemoveWhenOff`, `BotRemoval`).

## Testing in the game
`Tools\dev\autotest.ps1 -Script <file> -Tag <name> [-Before <folder>]` runs the game with a script of console commands
(seconds to wait, then the command), saves the `FWShot` screenshots to `Saved\AutoTest\<name>` and, with `-Before`,
side-by-side comparisons. Console commands for tests are the `UFUNCTION(Exec)` ones in
`Source/FactoryWorld3D/FWPlayerController.h` and `Player/FWCharacter.h` (FWShot, FWClose, FWMarket, FWGarage,
FWGoPlot, FWAimAt, FWStrike, FWDevWorld, fw.Register…).

## Code map
- `Source/FactoryWorld3D/FWPlayerController.*`: input, windows, the link to the server; `Player/FWCharacter.*`: walking,
  first-person arms, striking; `World/FWPlot.*`: the plot (terrain, objects, belts); `UI/`: Slate windows
  (`FWStyle` look and font, `FWWidgets` frame, cards, buttons; one `SFW*Window` per window); `Net/`: the WebSocket client.
- `Server/src/Modules/FactoryWorld.Modules.*`: one module per area (World: plots and map, Crafting, Logistics, Power,
  Recycling: the garage, Market, …), each with its own schema and migrations; `Server/tests`: xUnit against the real
  server and a real database.
