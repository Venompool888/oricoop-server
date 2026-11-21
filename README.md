# OriCoop Server (C-server)

> **Experimental.** Matches OriCoop client release **v0.2.2-2026.11.21** (menu: Your Own Server / 自建服务器). Online play
> in OriCoop v0.2.2 is a LAN host or this server; the tunnel relay is gone (T-159). Server-mode gameplay sync is still
> being built. The release ships a linux-x64 package with this server and the room save service (TCP 47021).
>
> **实验阶段。** 对应 OriCoop 客户端发布版 **v0.2.2-2026.11.21**（菜单：自建服务器）。v0.2.2 的联机方式是局域网主机或本服务器，
> 隧道中继已移除（T-159）。服务器模式的玩法同步仍在完善中。同名发布附带 linux-x64 安装包：本服务器加房间存档服务（TCP 47021）。

| | |
|---|---|
| Stage / 阶段 | **Experimental**: server + arbiter (C0), client authority layer (C1/C2), menu entry in the client; gameplay sync over the server is partly done. 实验：服务器、裁判、客户端权威层和菜单已有，玩法同步部分完成 |
| Protocol / 协议 | **C-server wire protocol v2** (`CsJoin.proto = 2`, multi-seat devices), see [docs/PROTOCOL.md](docs/PROTOCOL.md). **v2 servers reject v1 clients.** / 协议 v2，v2 服务器拒绝 v1 客户端 |
| Matching client / 对应客户端 | OriCoop release **v0.2.2-2026.11.21** (the same tag in the client repo; client protocol net9). 对应 OriCoop 发布版 **v0.2.2-2026.11.21**（客户端仓库同一个标签；客户端协议 net9） |
| Logs / 日志 | Connection log with each player's IP, SteamID and nickname (T-170), see [Privacy](#privacy) below. 连接日志记录每个玩家的 IP、SteamID 和昵称（T-170） |
| Players per room / 每房人数 | 1-4 |
| Default port / 默认端口 | **UDP 47022** |
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

Three test sets run (`CServerTest`, `AuthorityTest`, `CServerClientTest`); each must end with `RESULT PASS n/n`. They
use real UDP sockets and the mod's own `NetPeer` on localhost. Check a deployed server over the real network:
`.\tests\CServerTest.exe --host <server>[:47022]` (12 black-box checks).

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

- **Connection log:** the server writes one line per connection event (connect, join with seats, refused join and
  why, token rejoin, leave, timeout, goodbye to a stale session: at most one line per endpoint per minute, with `count=N`) with the time (UTC), the player's **IP address and
  port, room code, seats, SteamID64(s) and nickname(s)**. The line always goes to stdout (journald / `docker logs`)
  and, when a log folder is set, is appended to `connections.log` there. Nicknames are escaped (quotes, newlines and
  other control characters), so one line is always one event. Example:
  `2026-11-21T09:11:38.893Z join ip=203.0.113.7:55376 room=725822 seats=2 steam=76561190000000001 nick="Alpha" reason=-`
- **Tell your players** that your server logs their IP address and SteamID (and nickname), and how long you keep the
  logs. `deploy/oricoop-cserver.logrotate` keeps about six months (weekly, 26 files, compressed).
- SteamIDs and nicknames are also shared with the other players in the same room (roster).
- The optional per-room event log has one line per accepted event: `id from seq kind keyBase64 dataBase64`, with no
  IP addresses, SteamIDs or names.

### Layout

```
server/   CServer.cs (UDP hub), CServer.csproj (net8.0), Dockerfile
shared/   pure C# 5 files also compiled into the mod: Arbiter.cs, CsWire.cs (server side), IAuthority.cs,
          AuthorityClient.cs, HostArbiter.cs, INetRouteServer.cs, CServerClient.cs (client side);
          net/ (copies of the mod's NetPeer.cs, Transport.cs)
tests/    CServerTest.cs (67 + 12 --host checks), AuthorityTest.cs (31), CServerClientTest.cs (32)
deploy/   oricoop-cserver.service, oricoop-cserver.env.example, oricoop-cserver.logrotate
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

- **连接日志**：每个连接事件（连接、加入及座位、加入被拒及原因、凭令牌重连、离开、超时、给失效会话回告别：每个端点每分钟最多一行，带 `count=N`）写一行，
  内容是时间（UTC）、玩家的 **IP 和端口、房间码、座位、SteamID64、昵称**。这一行总是写到标准输出（journald / `docker logs`），
  设置了日志目录时还追加到该目录的 `connections.log`。昵称会转义（引号、换行和其他控制字符），一行永远是一个事件。
- **请告知玩家**：你的服务器会记录他们的 IP 地址和 SteamID（以及昵称），并说明保留多久。`deploy/oricoop-cserver.logrotate`
  约保留半年（每周轮换、保留 26 份、压缩）。
- SteamID 和昵称也会告诉同一房间的其他玩家（名单）。
- 可选的按房间事件日志每行一个已接受的事件：`id from seq kind keyBase64 dataBase64`，不含 IP、SteamID 或昵称。

### 协议和版本

当前是 **C-server 线协议 v2**（一台设备可带多个座位，见 [docs/PROTOCOL.md](docs/PROTOCOL.md)），对应 OriCoop 发布版 **v0.2.2-2026.11.21**。
v2 服务器拒绝 v1 客户端；OriCoop v0.2.0-2026.11.17 的客户端请用本仓库的标签 `v0.2.0-2026.11.17` 部署。

许可证：[MIT](LICENSE)。
