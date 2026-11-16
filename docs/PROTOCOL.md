# C-server wire protocol v1 / 线协议 v1 (prototype C0)

Transport: `shared/net/NetPeer.cs` (the OriCoop mod's own UDP layer: "CO" handshake, reliable / unreliable channels,
fragments, ACKs). After the handshake every application message is `[type u8][seat u8][payload]`, the same framing
the mod uses between players.

传输层是 `shared/net/NetPeer.cs`（mod 自己的 UDP 层："CO" 握手、可靠/不可靠、分片、ACK）。连上后每条应用消息是
`[类型 u8][座位 u8][内容]`，和 mod 里玩家之间的格式相同。

C-server control messages use ids 176-186 (0xB0-0xBA). Strings are `u8 length + UTF-8`; integers are little endian.
C-server 控制消息占 176–186（0xB0–0xBA）。字符串 = `u8 长度 + UTF-8`；整数小端。

| id | name | direction | payload |
|---|---|---|---|
| 176 | CsJoin | client → server | `proto u8 (=1)`, `mode u8` (0 new room / 1 join by code / 2 reconnect by token), `code str`, `token 16B`, `steam u64`, `nick str` |
| 177 | CsWelcome | server → client | `result u8` (0 ok, 1 no such room, 2 full, 3 protocol mismatch, 4 bad token), `seat u8`, `code str`, `token 16B`, `lastEvent u32` |
| 178 | CsRoster | server → all | `n u8`, then per seat `seat u8, online u8, steam u64, nick str` |
| 179 | CsLease | client → server | `op u8` (1 request, 2 release), `key str`, `score f32`, `active u8` |
| 180 | CsGrant | server → all | `key str`, `owner u8` (0 = nobody), `epoch u32` |
| 181 | CsAction | client → server | `seq u32`, `kind u8`, `flags u8` (bit0 = Claim, bit1 = Release), `key str`, `len u16 + data` |
| 182 | CsEvent | server → all | `id u32`, `from u8`, `seq u32`, `kind u8`, `key str`, `len u16 + data`. A rejected Claim goes only to its sender with `kind=0xFF` and `data` = the original kind |
| 183 | CsState | both | `key str`, `epoch u32`, `ver u32`, `active u8`, `len u16 + blob`. Accepted only from the current owner with a higher version; forwarded to the others |
| 184 | CsSnapshot | server → new seat | all leases (as 180) + all states (as 183); then the events it has not seen are replayed |
| 185 | CsError | server → client | `code u8`, `text str` |
| 186 | CsTo | client → server → one seat | `target u8` + a complete inner message |

All other message types (1-65 etc.) are forwarded to every other seat in the room with the seat byte rewritten to the
real sender. / 其他类型（1–65 等）服务器改写座位字节后转发给房间里其他所有座位。

## Arbiter rules / 裁判规则 (each one has a test)

1. Seats: the lowest free seat 1-4; the 5th player gets `full`. Each seat gets a random 16-byte token.
   入座：最低空座 1–4；第 5 人 `full`。每个座位一个随机 16 字节 token。
2. Disconnect: the seat is marked offline and kept 60 s; reconnecting with the token returns to the same seat. An
   empty room is deleted after 60 s. / 断线保留座位 60 秒，用 token 重连回原座位；空房间 60 秒后删除。
3. Lease request: granted (epoch + 1, broadcast) if nobody owns the key, the owner is offline, or the owner sent no
   active state for 300 ms and has held it ≥ 500 ms. Otherwise the requester's interest (score) is recorded.
   租约请求：没人拥有 / 拥有者离线 / 拥有者 300 ms 内没有 active 状态且已持有 ≥500 ms → 授予（epoch+1，广播）；否则记录兴趣。
4. Owner offline or release: the lease goes to the online interested seat with the lowest score (epoch + 1); with no
   interest the owner becomes 0. / 拥有者离线或释放：交给 score 最小的在线有兴趣者；没人就变成 0。
5. State: accepted only if `epoch` is current, the sender is the owner and `ver` is higher than stored.
   状态：epoch 是当前、发送者是拥有者、ver 递增才收。
6. Actions: a repeated `(seat, seq)` is dropped. Dedup is a sliding window of the last 4096 seqs per seat; anything
   older counts as already seen. Claim: the first one for a key wins, later ones get `0xFF`. Release (flags bit1):
   only the winner can free a claimed key (anyone, once the winner's seat has expired); the release is broadcast as an
   event. Things that can happen again can instead use generation keys such as `p:<guid>#<gen>`. Accepted actions get
   a global event id and are broadcast to everyone, the sender included.
   动作：`(座位, seq)` 去重（每座位只记最近 4096 个 seq，更老的当重复）；Claim 先到先得；Release（bit1）只有赢家能释放
   （赢家座位过期后任何人可释放），释放也作为事件广播；会重生的东西可用代数 key `p:<guid>#<代数>`。
   通过的动作分配全局事件编号并广播给所有人（含发起者）。
7. Late join / reconnect: snapshot first (leases + states), then the events after `lastEvent` (the server keeps the
   latest 4096). / 晚加入或重连：先发快照，再补发之后的事件（保留最近 4096 条）。

## Prototype details / 原型细节

- Snapshot: `u16 leaseCount` + lease payloads (CsGrant without headers), then `u16 stateCount` + state payloads
  (CsState without headers).
- Join carries no client event cursor. Replay starts at 0 for a new seat, or at the server's event number when the
  seat went offline for a reconnect. `Welcome.lastEvent` is the server's newest event id.
- `State.ver` keeps increasing across epochs; a lease move keeps the newest state with its original epoch. Only
  `CsState.active` refreshes the activity window.
- `CsTo` delivers the inner message with its seat corrected; nested control requests are not executed.
- Interest is removed on grant, release or disconnect; tied scores pick the lowest seat.
- Reconnecting with the token of a seat that is still online first runs Offline (lease hand-over, roster), then the
  new connection takes over; the old one is disconnected with `replaced`.
