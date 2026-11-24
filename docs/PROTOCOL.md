# C-server wire protocol v2 / 线协议 v2 (multi-seat devices)

**v2 servers reject v1 clients** (`CsWelcome.result = 3`, before the old payload is parsed). Client and server must
come from the same OriCoop release. / **v2 服务器拒绝 v1 客户端**（返回 `result=3`），客户端和服务器必须来自同一个发布版。

Transport: `shared/net/NetPeer.cs` (the OriCoop mod's own UDP layer: "CO" handshake, reliable / unreliable channels,
fragments, ACKs). After the handshake every application message is `[type u8][seat u8][payload]`, the same framing
the mod uses between players.

传输层是 `shared/net/NetPeer.cs`（mod 自己的 UDP 层："CO" 握手、可靠/不可靠、分片、ACK）。连上后每条应用消息是
`[类型 u8][座位 u8][内容]`，和 mod 里玩家之间的格式相同。

C-server control messages use ids 176-192 (0xB0-0xC0). Strings are `u8 length + UTF-8`; integers are little endian.
C-server 控制消息占 176–192（0xB0–0xC0）。字符串 = `u8 长度 + UTF-8`；整数小端。

| id | name | direction | payload |
|---|---|---|---|
| 176 | CsJoin | client → server | `proto u8 (=2)`, `mode u8` (0 new room / 1 join by code / 2 reconnect by token), `code str`, `token 16B`, `count u8` (1-3), then per seat `steam u64, nick str` |
| 177 | CsWelcome | server → client | `result u8` (0 ok, 1 no such room, 2 full, 3 protocol mismatch, 4 bad token, 5 kicked from this room), `count u8 + seats[count] u8` (ascending), `code str`, the connection's `token 16B`, `lastEvent u32` |
| 178 | CsRoster | server → all | `n u8`, then per seat `seat u8, online u8, steam u64, nick str` |
| 179 | CsLease | client → server | `op u8` (1 request, 2 release), `key str`, `score f32`, `active u8` |
| 180 | CsGrant | server → all | `key str`, `owner u8` (0 = nobody), `epoch u32` |
| 181 | CsAction | client → server | `seq u32`, `kind u8`, `flags u8` (bit0 = Claim, bit1 = Release), `key str`, `len u16 + data` |
| 182 | CsEvent | server → all | `id u32`, `from u8`, `seq u32`, `kind u8`, `key str`, `len u16 + data`. A rejected Claim goes only to its sender with `kind=0xFF` and `data` = the original kind |
| 183 | CsState | both | `key str`, `epoch u32`, `ver u32`, `active u8`, `len u16 + blob`. Accepted only from the current owner with a higher version; forwarded to the others |
| 184 | CsSnapshot | server → new seat | all leases (as 180) + all states (as 183); then the events it has not seen are replayed |
| 185 | CsError | server → client | `code u8`, `text str` |
| 186 | CsTo | client → server → the connection owning a seat | `target u8` + a complete inner message |
| 187 | CsSeatAdd | client → server | `steam u64, nick str`: ask for one more seat (lowest free) |
| 188 | CsSeatRelease | client → server | `seat u8`: free one non-primary seat of this connection now; `seat = 0`: this device leaves the room and all its seats are freed now (no 60 s hold) |
| 189 | CsList | client → server | no payload (extra bytes are ignored). Allowed before joining (seat 0) or inside a room; the same connection may still send CsJoin afterwards |
| 190 | CsRoomList | server → client | `count u8` (at most 64), then per room `code str, online u8, capacity u8 (4), nickCount u8, nicks[nickCount] str, text str` |
| 191 | CsRoomInfo | manager device → server | `text str`: the room description shown in the room list (ignored from other devices) |
| 192 | CsKick | manager device → server | `target u8` (1-4): remove the device that owns this seat |

## Room list, room info and kick / 房间列表、房间说明、踢出 (OriCoop v0.2.4)

- **CsList / CsRoomList.** At most one reply per endpoint per second (extra requests are ignored), and none while that
  connection has more than 256 unacknowledged reliable fragments. A connection that only lists and never joins is
  closed 3 s after its last reply. The list holds only rooms with at least one online seat whose manager set a
  non-empty description; `online` counts online seats, nicks are in seat order, held offline seats are not listed and a
  seat without a nick is listed as `P<seat>`. Rooms that should stay private (OriCoop's long-lived rooms joined by
  invite code) never set a description, so they are never listed.
- **CsRoomInfo.** The manager is the device owning the lowest online seat; when it leaves, the next one takes over.
  Descriptions and nicks are cut to 255 UTF-8 bytes on whole characters. The description is cleared when nobody is online.
- **CsKick.** Only the manager may kick, and only another device: all seats of the target device are freed at once and
  a new roster is broadcast. The kicked connection gets `CsError(code=3, text="server.err.kicked")` and is then
  disconnected. A request from a non-manager, for its own device or for an empty seat gets `CsError(code=2)` and changes
  nothing. Every non-zero SteamID of the kicked device is refused (`CsWelcome.result = 5`) for mode 1 and 2 joins while
  the room exists (not stored on disk). SteamID 0 (no Steam) is never banned.
- Strings are decoded as strict UTF-8; a malformed or oversized message closes only the connection that sent it.
  `CsTo` never carries any message in 176-192.
- With connection logging on (`--log-connections`), a kick is logged as event `kick` (target IP, seats, SteamIDs, nicks,
  reason `by seat N`) and a refused rejoin with reason `kicked`.

- **CsList / CsRoomList**：每个端点每秒最多回一次，多余请求忽略；该连接未确认的可靠分片超过 256 个时不回。只查询、从不加入的连接在最后一次回复 3 秒后关闭。
  只列出至少一个座位在线、且管理者设置了非空说明的房间；人数按在线座位计，昵称按座位顺序，离线保留的座位不列，没有昵称的座位显示为 `P<座位>`。
  需要保密的房间（OriCoop 用邀请码加入的长期房间）不设说明，所以不会出现在列表里。
- **CsRoomInfo**：管理者是最低在线座位所在的设备，离开后由下一个接替。说明和昵称按整字符截到 255 个 UTF-8 字节；没人在线时说明清空。
- **CsKick**：只有管理者能踢，而且只能踢别的设备：目标设备的全部座位立即释放，广播新的 Roster。被踢的连接先收到 `CsError(code=3, text="server.err.kicked")` 再断开。
  非管理者、踢自己的设备或空座位的请求回 `CsError(code=2)`，房间不变。被踢设备的所有非 0 SteamID 在房间存续期间不能用 mode 1、2 加入（`CsWelcome.result = 5`，不写盘）；SteamID 0（没有 Steam）从不封禁。
- 字符串按严格 UTF-8 解码；畸形或超长的消息只关闭发送它的连接。`CsTo` 不会转发 176–192 的任何消息。
- 开启连接日志（`--log-connections`）时，踢人记为事件 `kick`（目标 IP、座位、SteamID、昵称，原因 `by seat N`），被拒的重进记原因 `kicked`。

## Multi-seat devices / 一台设备多个座位 (v2)

- One connection = one device, which can hold several seats. A room still has **4 seats** (not 4 connections).
  PrimarySeat = the connection's lowest seat. / 一个连接代表一台设备，可带多个座位；房间仍是 4 座。
- The header sender of normal and control messages must be a seat owned by that connection; otherwise the message is
  dropped (`DroppedSenders`). The server no longer rewrites the sender. / sender 必须属于该连接，否则丢弃，不再改写。
- Other message types (1-65 etc.) are forwarded once to each other connection. CsTo checks the outer and inner sender,
  the inner message must be a normal game message, and it is routed to the target seat's connection (never back to
  the sender's own connection). State echo suppression is per connection.
- CsSeatAdd / CsSeatRelease reply with CsWelcome (the full new seat set, same token) and then broadcast Roster; no new
  Snapshot. Add when full returns `result=2` (set unchanged); an invalid Release returns CsError(code=2). The last
  seat, and the primary while other seats remain, cannot be released.
- CsSeatRelease with `seat = 0` (a deliberate leave): the device goes offline (leases move as for a disconnect), then
  all its seats are freed at once and Roster is broadcast; its token is no longer valid. Older servers answer it with
  CsError(code=2) and keep the 60 s hold. / `seat = 0` 表示主动离开：先像断线一样迁移租约，再立即释放全部座位并广播 Roster，
  token 作废；旧服务器回 CsError(code=2)，照旧保留 60 秒。
- A reconnect restores the seat set stored for the token; the request's count/identities do not overwrite it.

## Optional HTTPS diagnostics after a successful join

A client with permission for the joined game address can send one v1 report to
`https://<joined-host>/oricoop/diagnostics/v1`. The HTTPS service and the UDP game server
share a short-lived grant for the accepted seat. Both HTTP requests carry `X-OriCoop-Join`,
the lowercase hex HMAC-SHA256 of the ASCII string `oricoop-diagnostics-v1/request` keyed by the
16-byte welcome token. The GET response must be HTTP 200 with the exact ASCII body
`oricoop-diagnostics-v1.` followed by the lowercase hex HMAC for `oricoop-diagnostics-v1/capability`.
After verifying the body, the client collects environment and probe fields and POSTs a single
`application/x-www-form-urlencoded` body of at most 2048 bytes. Required fields are `v=1` and
`probe=ok` or `probe=failed`. A successful probe also supplies `provider` and `ip`. On acceptance
the server consumes the grant and returns HTTP 204. The raw welcome token stays in the game protocol.

## Arbiter rules / 裁判规则 (each one has a test)

1. Seats: a join asks for n = 1-3 seats and gets the n lowest free seats 1-4 atomically, or `full` with none.
   Each connection gets one random 16-byte token shared by its seats; CsSeatAdd can grow it up to the room limit.
   入座：请求 1–3 个座位，原子分配最低空座，不够就 `full` 且一个都不给；每个连接一个 token，所有本地座位共用。
2. Disconnect: all seats of the connection go offline together (then leases move) and are kept 60 s; reconnecting
   with the token restores all of them. An empty room is deleted after 60 s.
   断线：该连接的全部座位一起离线（再迁移租约），保留 60 秒；用 token 重连恢复全部座位；空房间 60 秒后删除。
3. Lease request: granted (epoch + 1, broadcast) if nobody owns the key, the owner is offline, or the owner sent no
   active state for 300 ms, has held it ≥ 500 ms and the requester's score is at least as good (lower or equal) as
   the owner's latest request score; after 2 s without active state anyone takes over. Otherwise the requester's
   interest (score) is recorded. One lost heartbeat no longer hands the key to a farther seat.
   租约请求：没人拥有 / 拥有者离线 / 拥有者 300 ms 内没有 active 状态、已持有 ≥500 ms 且请求者分数不比拥有者差（更低或相等）→ 授予
   （epoch+1，广播）；拥有者 2 秒没有 active 状态时谁都可以接手；否则记录兴趣。丢一个心跳不再把租约交给更远的座位。
4. Owner offline or release: the lease goes to the online interested seat with the lowest score (epoch + 1); with no
   interest the owner becomes 0. / 拥有者离线或释放：交给 score 最小的在线有兴趣者；没人就变成 0。
5. State: accepted only if `epoch` is current, the sender is the owner and `ver` is higher than the stored state of
   the same epoch (a new owner's first state is accepted even if the old owner's version was higher).
   状态：epoch 是当前、发送者是拥有者、ver 比同一 epoch 已存的大才收（新拥有者的第一个状态即使版本号比旧拥有者小也收）。
6. Actions: a repeated `(seat, seq)` is dropped. Dedup is a sliding window of the last 4096 seqs per seat; anything
   older counts as already seen. A seat's window is cleared when it rejoins (a restarted client starts its seq
   again). Claim: the first one for a key wins, later ones get `0xFF`. Release (flags bit1):
   only the winner can free a claimed key (anyone, once the winner's seat has expired); the release is broadcast as an
   event. Things that can happen again can instead use generation keys such as `p:<guid>#<gen>`. Accepted actions get
   a global event id and are broadcast to everyone, the sender included.
   动作：`(座位, seq)` 去重（每座位只记最近 4096 个 seq，更老的当重复）；Claim 先到先得；Release（bit1）只有赢家能释放
   （赢家座位过期后任何人可释放），释放也作为事件广播；会重生的东西可用代数 key `p:<guid>#<代数>`。
   通过的动作分配全局事件编号并广播给所有人（含发起者）。
7. Late join / reconnect: snapshot first (leases + states), then the events after `lastEvent` (the server keeps the
   latest 4096). / 晚加入或重连：先发快照，再补发之后的事件（保留最近 4096 条）。
8. Seat add/release does not take the device's other seats offline: their leases, epochs, states and dedup stay
   valid. A released seat's leases move as in rule 4. / 加减座位不影响该设备其他座位；释放座位的租约按第 4 条迁移。

## Prototype details / 原型细节

- Snapshot: `u16 leaseCount` + lease payloads (CsGrant without headers), then `u16 stateCount` + state payloads
  (CsState without headers).
- Join carries no client event cursor. Replay starts at 0 for a new seat, or at the server's event number when the
  seat went offline for a reconnect. `Welcome.lastEvent` is the server's newest event id.
- `State.ver` keeps increasing across epochs; a lease move keeps the newest state with its original epoch. Only
  `CsState.active` refreshes the activity window.
- `CsTo` delivers the inner message unchanged (its sender must belong to the sending connection); nested control
  requests are not executed.
- Interest is removed on grant, release or disconnect; tied scores pick the lowest seat.
- Reconnecting with the token of a connection that is still online first runs Offline (lease hand-over, roster), then
  the new connection takes over all its seats; the old one is disconnected with `replaced`.
