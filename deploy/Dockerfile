FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/StormConnect.Protocol/ src/StormConnect.Protocol/
COPY src/StormConnect.Relay/ src/StormConnect.Relay/
RUN dotnet publish src/StormConnect.Relay/StormConnect.Relay.csproj -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV STORM_RELAY_PORT=7443
EXPOSE 7443 7444/udp
ENTRYPOINT ["dotnet", "StormConnect.Relay.dll"]
