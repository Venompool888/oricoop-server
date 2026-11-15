# OriCoop Server (C-server)

> **Prototype (C0). Not connected to the game yet: you cannot play with it.** No OriCoop client release talks to this
> server so far. For playing online today, use the tunnel relay
> [oricoop-relay](https://github.com/Venompool888/oricoop-relay).
>
> **原型阶段（C0）。还没有接入游戏客户端，现在不能用来玩。** 目前没有任何 OriCoop 客户端版本会连接这个服务器。
> 现在要联机，请用隧道中继 [oricoop-relay](https://github.com/Venompool888/oricoop-relay)。

| | |
|---|---|
| Stage / 阶段 | **C0 prototype**: server + arbiter + loopback tests. 原型：服务器、裁判、本机测试 |
| Protocol / 协议 | **C-server wire protocol v1** (`CsJoin.proto = 1`), see [docs/PROTOCOL.md](docs/PROTOCOL.md) |
| Matching client / 对应客户端 | **None yet.** A future OriCoop release will list the version here. 暂无，以后的客户端版本会写在这里 |
| Players per room / 每房人数 | 1-4 |
| Default port / 默认端口 | **UDP 47022** (the tunnel relay uses 47020) |
| Runtime / 运行环境 | .NET 8 (Linux, Docker) or .NET Framework 4.x (Windows `.exe`) |

**AI agents: read [AGENTS.md](AGENTS.md) first.** / **让 AI 部署：让它先读 [AGENTS.md](AGENTS.md)。**

Not affiliated with or endorsed by Moon Studios or Microsoft. No game files here; the server runs no game code.
与 Moon Studios、Microsoft 无关。本仓库不含游戏文件，服务器不运行任何游戏代码。

---

## English

### What it is

OriCoop is a fan-made online co-op mod for *Ori and the Blind Forest: Definitive Edition*. This is its second server
type, an **authority / arbitration server**. Unlike the tunnel relay, which only forwards bytes between two games,
all players (1-4) connect to this server and it decides:

- **seats** (1-4, with reconnect tokens),
- **leases**: which player simulates which object (enemy, boulder, door...), with epochs so stale owners are ignored,
- **state versions**: only the current owner's newer states are accepted and forwarded,
- **claims and events**: "only once" results (pick up this item, open this door) get one global, ordered event id.

It does not run Unity, physics or AI; the games still simulate. Rooms live in memory and are lost on restart.

### Build and test (Windows, no SDK needed)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build-cserver.ps1
.\server\CServer.exe --port 47022 --log .\logs
```

The last line must be `RESULT PASS n/n`. The tests use real UDP sockets and the mod's own `NetPeer` on localhost.

### Deploy

Details, firewall / cloud security groups, upgrade and rollback: [AGENTS.md](AGENTS.md).

```bash
# Docker (build context = repo root)
git clone https://github.com/Venompool888/oricoop-server oricoop-server && cd oricoop-server
docker build -f server/Dockerfile -t oricoop-cserver .
docker run -d --name oricoop-cserver --restart unless-stopped -p 47022:47022/udp oricoop-cserver

# systemd + .NET 8
dotnet publish server/CServer.csproj -c Release -o out --nologo
# then follow AGENTS.md section 5
```

Open **UDP 47022** in the OS firewall and in the cloud provider's security group. Settings: `--port`, `--log <dir>`,
or the environment variables `ORICOOP_CSERVER_PORT`, `ORICOOP_CSERVER_LOG_DIR` (arguments win; under systemd
`LOGS_DIRECTORY` is used when no log folder is set).

### Privacy

- Clients send a SteamID64 and a nickname in `CsJoin`. The server keeps them **in memory only** and shares them with
  the other players in the same room (roster). They are not written to disk.
- The optional per-room event log has one line per accepted event: `id from seq kind keyBase64 dataBase64`. It
  contains **no IP addresses, SteamIDs or names**. Rotate or delete it yourself.

### Layout

```
server/   CServer.cs (UDP hub), CServer.csproj (net8.0), Dockerfile
shared/   Arbiter.cs, CsWire.cs (pure C# 5, also meant for the mod), net/ (copies of the mod's NetPeer.cs, Transport.cs)
tests/    CServerTest.cs (26 tests)
deploy/   oricoop-cserver.service, oricoop-cserver.env.example
docs/     PROTOCOL.md
```

License: [MIT](LICENSE).

---

## 中文

### 这是什么

OriCoop 是《Ori and the Blind Forest: Definitive Edition》的同人联机 mod。这是它的第二种服务器：**权威 / 仲裁服务器**。
隧道中继只在两台游戏之间转发字节；这个服务器则让所有玩家（1–4 人）都连到它，由它来决定：

- **座位**（1–4，带重连 token）；
- **租约**：哪个玩家负责模拟哪个物体（敌人、石头、门……），用 epoch 忽略过期的拥有者；
- **状态版本**：只接受并转发当前拥有者的、版本更新的状态；
- **Claim 和事件**："只能发生一次"的结果（捡这个东西、开这扇门）得到一个全局有序的事件编号。

服务器不运行 Unity、物理或 AI，模拟仍在游戏里。房间只在内存里，重启就没了。

### 构建和测试（Windows，不需要 SDK）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build-cserver.ps1
.\server\CServer.exe --port 47022 --log .\logs
```

最后一行应是 `RESULT PASS n/n`。测试在本机用真实 UDP 和 mod 自己的 `NetPeer`。

### 部署

完整步骤、防火墙和云安全组、升级回滚见 [AGENTS.md](AGENTS.md)。上面英文部分的 Docker / systemd 命令通用。
系统防火墙**和**云服务商安全组都要放行 **UDP 47022**。

### 隐私

- 客户端在 `CsJoin` 里发送 SteamID64 和昵称。服务器**只放在内存里**，并告诉同一房间的其他玩家（名单），不写到磁盘。
- 可选的按房间事件日志每行一个已接受的事件：`id from seq kind keyBase64 dataBase64`，**不含 IP、SteamID 或昵称**。日志请自己轮换或删除。

### 协议和版本

当前是 **C-server 线协议 v1**（见 [docs/PROTOCOL.md](docs/PROTOCOL.md)）。还没有对应的客户端版本；以后接入游戏时会在上方表格写明。

许可证：[MIT](LICENSE)。
