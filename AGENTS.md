# Working on Dave the Diver Multiplayer

Notes for coding agents (Claude Code, Codex and others) working in this repository.

## How to work (Karpathy guidelines)

These follow [Andrej Karpathy's observations](https://x.com/karpathy/status/2015883857489522876) on common LLM coding mistakes. They favour care over speed; use judgment on trivial tasks.

### 1. Think before coding
- State your assumptions. When something is unclear, name it.
- The owner prefers no questions mid-task: make the call, write the assumption into the commit message or `BUGS.md`, and keep going. Ask only when a choice is truly theirs (publishing, deleting, anything outward-facing).
- If a simpler approach exists, say so and take it.
- Read the evidence before guessing. Player logs are in the game folder at `BepInEx\LogOutput.log` and `BepInEx\DTMP-logs\*.log`. One real log line beats a theory: three times in a row the first guess at an online bug was wrong, and the log showed the real cause.

### 2. Simplicity first
- Write the minimum code that solves the problem. Add nothing speculative and no configurability nobody asked for.
- No abstractions for single-use code, and no error handling for impossible cases.
- If 200 lines could be 50, rewrite it.

### 3. Surgical changes
- Touch only what the task needs. Match the surrounding style (comment density, naming, idiom).
- Don't refactor or "improve" working code nearby. Mention unrelated dead code (for example the unused `Diver*Replicator` classes) instead of deleting it.
- Remove only what your own change made unused.
- Every changed line should trace back to the request.

### 4. Goal-driven execution
- Turn each task into a check that can fail. "Fix the bug" means reproduce it in the lab, fix it, and show the check passing.
- Every lab check has a **mutant**: a `LabMode.IsMutant("name")` switch that turns the fix off, and the check must fail with it on. A test that can't fail proves nothing.
- For multi-step work, state the plan as step → verify.

## Project facts

- **Game:** Dave the Diver, Steam app 1868140. Unity 6000.0.52f1 with IL2CPP, BepInEx 6 `6.0.0-be.790`. Build against a lab copy's `BepInEx\interop` (`-p:GameInstallPath=...`).
- **Credits:** the mod was originally made by S1ii ([Coop-Divers](https://github.com/S1ii/Coop-Divers), MIT). This fork is maintained by Multiplayer Everything. Keep the credit in the README and the original license.
- **What's where:** `README.md` (how it works, test table), `BUGS.md` (fixed, open, refuted), `src/Lab` (lab mode, active only with `-dtmp-lab`).

## Lab

- `scripts\make-lab.ps1` creates isolated copies in `private\lab-*` (gitignored). They link to the install, have their own BepInEx, and redirect saves to `lab-saves`.
- `tests\lab-suite.ps1` starts a host and a client (real game copies) and checks what each logged:
  - `-Steam` goes through SteamMock;
  - `-Chaos` adds lag and loss;
  - `-StopAfter <step>` and `-Mutant <names>` limit the run.
- `tests\mutation-suite.ps1` runs every mutant; each one must fail its target check.
- Lab windows start minimized (`SW_SHOWMINNOACTIVE`) and come back at 640x360 on the right edge, at 30 fps, lowest quality, BelowNormal priority. Run one tour at a time. Never send keys or mouse input to the desktop.
- **Known blind spot:** both lab copies use one Steam account, so anything that differs between accounts, or depends on real mouse and keyboard input on the real menus, only shows up in a real two-player test. Ask the players for their logs.

## Never commit

Game files, decompiled code, interop assemblies, `private/`, `deps/`, build outputs, tokens or `discord.json`.

## Releases

- Package with `build\package.ps1`. It writes `artifacts\DaveTheDiverMP-<version>.zip` with `/` paths and a `.sha256` next to it.
- Bump the version in both `DaveTheDiverMP.csproj` and the `BepInPlugin` attribute in `Plugin.cs`. Players on different versions can't connect.
- Push and publish only when the owner says so. Releases stay pre-releases until a real two-account session works. Never delete the pinned `bepinex-6.0.0-be.790` loader release.
- After a release, send the Website session the games.json fields (site id `davethediver`).
