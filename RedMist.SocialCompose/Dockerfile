# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["RedMist.SocialCompose/RedMist.SocialCompose.csproj", "RedMist.SocialCompose/"]
COPY ["RedMist.Social/RedMist.Social.csproj", "RedMist.Social/"]
COPY ["RedMist.Backend.Shared/RedMist.Backend.Shared.csproj", "RedMist.Backend.Shared/"]
COPY ["RedMist.Database/RedMist.Database.csproj", "RedMist.Database/"]
COPY ["version", "version"]
RUN dotnet restore "./RedMist.SocialCompose/RedMist.SocialCompose.csproj"
COPY . .
WORKDIR "/src/RedMist.SocialCompose"
RUN dotnet build "./RedMist.SocialCompose.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./RedMist.SocialCompose.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Chromium, for photographing the timing page. Installed as root because --with-deps apt-installs the
# shared libraries Chromium needs, then dropped back to the app user to run.
#
# PLAYWRIGHT_BROWSERS_PATH is set rather than left to default: without it the browser lands in the
# installing user's home cache, which is root's, and the app user cannot read it at runtime. It has to
# stay set for the running process too, which is why it is an ENV and not a build argument.
#
# Installed through the driver's own bundled node rather than playwright.ps1, which would mean adding
# PowerShell to the image for one command.
USER root
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN ./.playwright/node/linux-x64/node ./.playwright/package/cli.js install --with-deps chromium \
    && chmod -R a+rX /ms-playwright \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID

ENTRYPOINT ["dotnet", "RedMist.SocialCompose.dll"]
