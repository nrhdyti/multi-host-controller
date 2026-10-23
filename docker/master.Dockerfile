FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/MultiHost.Shared/ src/MultiHost.Shared/
COPY src/MultiHost.Master/ src/MultiHost.Master/
RUN dotnet publish src/MultiHost.Master/MultiHost.Master.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY playbooks /playbooks
ENV ASPNETCORE_URLS=http://+:8080 \
    Playbooks__Directory=/playbooks
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "MultiHost.Master.dll"]
