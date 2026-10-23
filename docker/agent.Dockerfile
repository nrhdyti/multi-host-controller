FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/MultiHost.Shared/ src/MultiHost.Shared/
COPY src/MultiHost.Agent/ src/MultiHost.Agent/
RUN dotnet publish src/MultiHost.Agent/MultiHost.Agent.csproj -c Release -o /app /p:UseAppHost=false

# The agent runs as root on purpose: playbooks install packages, create users and manage services.
FROM mcr.microsoft.com/dotnet/runtime:8.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ansible-core ca-certificates curl procps util-linux passwd \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV Agent__StateFile=/var/lib/multihost-agent/state.json \
    Agent__LogDirectory=/var/log/multihost-agent
VOLUME ["/var/lib/multihost-agent"]
ENTRYPOINT ["dotnet", "MultiHost.Agent.dll"]
