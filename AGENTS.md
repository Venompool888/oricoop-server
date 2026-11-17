# AGENTS.md: deploying OriCoop Server / C-server (for AI agents)

You are an AI agent asked to build, test or deploy this server for a human. Follow this file in order.

**First, tell the human the stage:** this is **experimental**. The OriCoop client release v0.2.0-2026.11.17
can connect to it (menu: Server → 服务器（平等模式）), but server-mode gameplay sync is still partly done. The most
tested way to play online is the tunnel relay [oricoop-relay](https://github.com/Venompool888/oricoop-relay) (same
release tag; see its AGENTS.md). Server and client must come from the same release tag. Say this in your first reply.

What it is: one .NET process, one UDP port (**default 47022**), 1-4 players per room, rooms in memory only. No
database, no TLS, no TCP port. Protocol: C-server wire protocol v1 ([docs/PROTOCOL.md](docs/PROTOCOL.md)).

---

## 0. Ground rules

- Read-only checks are fine. Ask before anything in the "needs confirmation" list (section 1).
- Never print, log or commit secrets (SSH keys, cloud credentials). This server needs no keys of its own.
- Never commit the human's real server address, host name or SSH alias anywhere public. Use `203.0.113.10` /
  `cserver.example.com` in anything you write back to the repo.
- Do not touch other services on the host. It can run next to oricoop-relay (47020/udp); pick another port if 47022
  is taken.
- Do not disable the firewall. Open exactly one UDP port.

## 1. Ask the human first

1. Do they understand it is experimental, and which OriCoop client release do their players use? (Must be v0.2.0-2026.11.17.)
2. Where: SSH host / user (sudo?), or "this machine".
3. Port: UDP 47022 unless they want another.
4. Install method: systemd + .NET 8 (default) or Docker.
5. Event log: off (default) or on (`/var/log/oricoop-cserver`, one file per room, no IPs or names).
6. Cloud provider, and whether you may change its firewall / security group.

**Needs explicit confirmation:** installing packages or .NET / Docker, running install scripts, `useradd`, writing to
`/etc` or `/opt`, enabling or restarting services (a restart deletes all rooms), firewall and security-group changes,
deleting old builds or logs, anything that costs money.

## 2. Inspect the host (read-only)

```bash
uname -m; cat /etc/os-release; nproc; free -m; df -h /
command -v dotnet && dotnet --list-sdks && dotnet --list-runtimes
command -v docker && docker --version
sudo ss -ulpn | grep -w 47022 || echo "port 47022 free"
curl -s -4 https://ifconfig.me          # public IPv4 (the server binds IPv4)
sudo ufw status 2>/dev/null; sudo firewall-cmd --state 2>/dev/null
```

x86_64 and aarch64 are supported by .NET 8. Needs about 1 vCPU and 512 MB RAM.

## 3. Get the code and build

```bash
sudo mkdir -p /opt/oricoop-cserver && sudo chown "$USER" /opt/oricoop-cserver
git clone https://github.com/Venompool888/oricoop-server /opt/oricoop-cserver/src
```

Install the **.NET 8 SDK** (needed once to build): `sudo apt-get install -y dotnet-sdk-8.0` (Ubuntu 22.04+),
`sudo dnf install -y dotnet-sdk-8.0` (Fedora / RHEL 9), Debian 12 via Microsoft's package repo, or any distro with
`bash dotnet-install.sh --channel 8.0 --install-dir /opt/oricoop-cserver/dotnet` (then use that `dotnet` path below
and in the unit's `ExecStart=`).

```bash
cd /opt/oricoop-cserver/src
dotnet publish server/CServer.csproj -c Release -o /opt/oricoop-cserver/app-new --nologo
timeout 5 dotnet /opt/oricoop-cserver/app-new/CServer.dll --port 47022; echo "exit $?"
```

Expected: `OriCoop C-server listening on UDP port 47022`, then `exit 124` (stopped by timeout = it ran).
`C-server failed: ...` = read the message (usually the port is taken).

The full test suites (CServerTest 36, AuthorityTest 31, CServerClientTest 17; real UDP on localhost) run on Windows
with `build-cserver.ps1`. On Linux the CServerTest code is compiled into the binary but not run by publish; if the
human wants test results, run `build-cserver.ps1` on a Windows machine.

## 4. Install (systemd)

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin oricoop-cserver   # skip if it exists
cd /opt/oricoop-cserver
[ -d app ] && mv app app-old
mv app-new app && sudo chown -R root:root app
sudo cp src/deploy/oricoop-cserver.service /etc/systemd/system/
sudo install -m 600 -o root -g root src/deploy/oricoop-cserver.env.example /etc/oricoop-cserver.env
sudo systemctl daemon-reload
sudo systemctl enable --now oricoop-cserver
```

- `ExecStart=` assumes `/usr/bin/dotnet`; change it for a dotnet-install.sh install.
- Port: `ORICOOP_CSERVER_PORT` in `/etc/oricoop-cserver.env` (or `--port` in `ExecStart`, which wins).
- Event log: `LogsDirectory=oricoop-cserver` creates `/var/log/oricoop-cserver`; the server uses it through
  `LOGS_DIRECTORY` automatically. It does not rotate the files; set up logrotate or a cleanup timer if the human
  wants (ask first).
- Overrides: `sudo systemctl edit oricoop-cserver` (drop-in) instead of editing the unit. systemd does not allow
  comments at the end of a value line.

## 5. Open the UDP port

Both layers, **UDP** (not TCP), inbound from 0.0.0.0/0:

```bash
sudo ufw allow 47022/udp                                                  # ufw (keep OpenSSH allowed!)
sudo firewall-cmd --permanent --add-port=47022/udp && sudo firewall-cmd --reload   # firewalld
```

Cloud security group (examples; ask before changing):

- **GCP**: `gcloud compute firewall-rules create oricoop-cserver --direction=INGRESS --action=ALLOW --rules=udp:47022 --source-ranges=0.0.0.0/0 --target-tags=oricoop-cserver`, then tag the VM.
- **AWS**: `aws ec2 authorize-security-group-ingress --group-id <sg-id> --protocol udp --port 47022 --cidr 0.0.0.0/0`
- **Azure**: `az network nsg rule create -g <rg> --nsg-name <nsg> -n oricoop-cserver --priority 1020 --direction Inbound --access Allow --protocol Udp --destination-port-ranges 47022`
- **Alibaba Cloud 阿里云**: 安全组 → 入方向 → 添加 UDP 47022/47022，授权对象 0.0.0.0/0（或 `aliyun ecs AuthorizeSecurityGroup ... --IpProtocol udp --PortRange 47022/47022 --SourceCidrIp 0.0.0.0/0`）。
- **Tencent Cloud 腾讯云**: 安全组 → 入站规则 → 添加 `UDP:47022`，来源 0.0.0.0/0；轻量服务器在实例的"防火墙"页添加。
- **Oracle Cloud**: security list ingress UDP 47022, plus `sudo iptables -I INPUT -p udp --dport 47022 -j ACCEPT` (persist it).

More detail per provider: oricoop-relay's AGENTS.md section 6 (same steps, port 47020 there).

## 6. Verify

```bash
systemctl is-active oricoop-cserver
journalctl -u oricoop-cserver -n 20 --no-pager      # OriCoop C-server listening on UDP port 47022
sudo ss -ulpn | grep -w 47022
```

From **outside** the server (a Windows PC with the repo, after `build-cserver.ps1`), run the black-box checks over
the real network; testing from the server itself does not prove the cloud firewall is open:

```powershell
.\tests\CServerTest.exe --host <PUBLIC_IP_OR_DOMAIN>:47022     # 12 checks; must end with RESULT PASS
```

They create short-lived test rooms on that server (in memory; they disappear by themselves). If they fail from
outside but the service is listening, a firewall layer (section 5) is closed. No Windows PC available: then a
successful deploy is service active + listening line + port listening + firewall rules in place; say that the
external check was not run.

## 7. Upgrade and roll back

A restart deletes all rooms, so confirm the timing.

```bash
cd /opt/oricoop-cserver/src && git fetch && git log --oneline HEAD..origin/main   # show the human
git pull --ff-only
dotnet publish server/CServer.csproj -c Release -o /opt/oricoop-cserver/app-new --nologo
cd /opt/oricoop-cserver && sudo rm -rf app-old && sudo mv app app-old && sudo mv app-new app
sudo systemctl restart oricoop-cserver && journalctl -u oricoop-cserver -n 5 --no-pager
```

Roll back: `cd /opt/oricoop-cserver && sudo rm -rf app && sudo mv app-old app && sudo systemctl restart oricoop-cserver`.

If the protocol version changes (see README), clients and server must match; ask the human before upgrading.

## 8. Docker / Windows

Docker (build context is the repo root):
```bash
docker build -f server/Dockerfile -t oricoop-cserver .
docker run -d --name oricoop-cserver --restart unless-stopped -p 47022:47022/udp \
  -v oricoop-cserver-logs:/logs -e ORICOOP_CSERVER_LOG_DIR=/logs oricoop-cserver   # drop -v/-e for no log
docker logs oricoop-cserver
```
`/udp` in `-p` matters. For another port, change the mapping and add `--port <port>` after the image name (the image's
default `CMD` sets `--port 47022`, which overrides `ORICOOP_CSERVER_PORT`).

Windows:
```powershell
.\build-cserver.ps1                              # build + all tests
.\server\CServer.exe --port 47022 --log .\logs   # Ctrl+C stops it
New-NetFirewallRule -DisplayName 'OriCoop C-server' -Direction Inbound -Protocol UDP -LocalPort 47022 -Action Allow   # admin, confirm first
```
A home PC also needs router port forwarding (UDP 47022) and a public IPv4 (no CGNAT).

## 9. Common mistakes

| Symptom | Cause / fix |
|---|---|
| Client cannot connect / version error | Client and server must be the same release tag (v0.2.0-2026.11.17). Older clients have no server mode. |
| Opened TCP 47022 | The server is UDP only. |
| Docker `COPY failed` | Build context must be the repo root: `docker build -f server/Dockerfile .` |
| Unit fails with 203/EXEC | Wrong dotnet path in `ExecStart=`. |
| `dotnet publish` fails: no SDK | Install `dotnet-sdk-8.0`, not just the runtime. |
| `C-server failed: ...address already in use` | Port taken (`ss -ulpn`); choose another and update env file and firewall. |
| Event log not written | Under Docker you need a writable mount and `ORICOOP_CSERVER_LOG_DIR`; under systemd keep `LogsDirectory=`. |

## 10. Report back to the human

Address and port (`host:47022`), install method and paths, firewall changes made and any left for them, service status
and the listening line, whether the event log is on and where, how to upgrade / roll back, and which client release
(v0.2.0-2026.11.17) players need.
