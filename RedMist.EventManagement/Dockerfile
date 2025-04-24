# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 80


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["RedMist.EventManagement/RedMist.EventManagement.csproj", "RedMist.EventManagement/"]
COPY ["bigmission-shared/BigMission.Shared/BigMission.Shared.csproj", "bigmission-shared/BigMission.Shared/"]
COPY ["RedMist.Backend.Shared/RedMist.Backend.Shared.csproj", "RedMist.Backend.Shared/"]
COPY ["RedMist.ControlLogs/RedMist.ControlLogs.csproj", "RedMist.ControlLogs/"]
COPY ["RedMist.Database/RedMist.Database.csproj", "RedMist.Database/"]
COPY ["redmist-timing-common/RedMist.TimingCommon/RedMist.TimingCommon.csproj", "redmist-timing-common/RedMist.TimingCommon/"]
RUN dotnet restore "./RedMist.EventManagement/RedMist.EventManagement.csproj"
COPY . .
WORKDIR "/src/RedMist.EventManagement"
RUN dotnet build "./RedMist.EventManagement.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./RedMist.EventManagement.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RedMist.EventManagement.dll"]