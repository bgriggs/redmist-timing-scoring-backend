FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["tools/RedMist.StatusLoadTest/RedMist.StatusLoadTest.csproj", "tools/RedMist.StatusLoadTest/"]
RUN dotnet restore "./tools/RedMist.StatusLoadTest/RedMist.StatusLoadTest.csproj"
COPY tools/RedMist.StatusLoadTest/ tools/RedMist.StatusLoadTest/
WORKDIR "/src/tools/RedMist.StatusLoadTest"
RUN dotnet publish "./RedMist.StatusLoadTest.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RedMist.StatusLoadTest.dll"]
