# OriCoop C-server (prototype C0).
# Build (from the repo root; the context must be the repo root):  docker build -f server/Dockerfile -t oricoop-cserver .
# Run:    docker run -d --name oricoop-cserver --restart unless-stopped -p 47022:47022/udp oricoop-cserver
# Event log in a volume: add  -v oricoop-cserver-logs:/logs -e ORICOOP_CSERVER_LOG_DIR=/logs
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY server/ server/
COPY shared/ shared/
COPY tests/CServerTest.cs tests/
RUN dotnet publish server/CServer.csproj -c Release -o /out --nologo

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
WORKDIR /app
COPY --from=build /out ./
# Folder for the optional event-log volume, writable by the non-root user.
RUN mkdir -p /logs && chown app /logs
USER app
EXPOSE 47022/udp
ENTRYPOINT ["dotnet", "CServer.dll"]
CMD ["--port", "47022"]
