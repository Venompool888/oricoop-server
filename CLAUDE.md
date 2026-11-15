# CLAUDE.md

Read [AGENTS.md](AGENTS.md) before doing anything in this repo: it says what to ask the human, which steps need their
confirmation, and how to build, deploy, verify and upgrade. Note the stage: this is the C0 prototype and no game
client uses it yet.

Code: `server/CServer.cs` (UDP hub), `shared/Arbiter.cs` + `shared/CsWire.cs` (pure C# 5, no Unity, .NET 3.5 APIs so
the mod can compile the same files), `shared/net/` (copies of the mod's NetPeer.cs / Transport.cs; change them in the
OriCoop mod, not here). Tests: `build-cserver.ps1` on Windows. Protocol: `docs/PROTOCOL.md`. Never commit real server
addresses, host names or keys.
