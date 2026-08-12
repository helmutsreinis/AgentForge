# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/AgentForge.Host/AgentForge.Host.csproj --locked-mode \
    && dotnet restore src/AgentForge.Cli/AgentForge.Cli.csproj --locked-mode \
    && dotnet restore src/AgentForge.PluginWorker/AgentForge.PluginWorker.csproj --locked-mode
RUN dotnet publish src/AgentForge.Host/AgentForge.Host.csproj -c Release --no-restore -o /out/host \
    && dotnet publish src/AgentForge.Cli/AgentForge.Cli.csproj -c Release --no-restore -o /out/cli \
    && dotnet publish src/AgentForge.PluginWorker/AgentForge.PluginWorker.csproj -c Release --no-restore -o /out/worker \
    && cp packaging/linux/appsettings.Production.json /out/host/appsettings.Production.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS runtime
WORKDIR /app
COPY --from=build /out/host ./host
COPY --from=build /out/cli ./cli
COPY --from=build /out/worker ./worker
RUN mkdir -p /var/lib/agentforge && chown -R "$APP_UID:$APP_UID" /var/lib/agentforge
USER $APP_UID
ENV DOTNET_ENVIRONMENT=Production \
    AGENTFORGE_ENDPOINT=http://127.0.0.1:5047 \
    AgentForge__Installation__DataDirectory=/var/lib/agentforge \
    AgentForge__Plugins__PluginWorkerExecutable=/app/worker/agentforge-plugin-worker
VOLUME ["/var/lib/agentforge"]
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 CMD ["/app/cli/agentforge", "health-probe"]
ENTRYPOINT ["/app/host/AgentForge.Host"]
