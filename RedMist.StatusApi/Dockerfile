# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
# QuestPDF's export renderer needs no packages added here: libQuestPdfSkia.so and libqpdf.so link
# only libc, libm, libgcc_s, libstdc++ and libz, all of which the runtime-deps layer of this image
# already carries, and QuestPDF is configured to use its embedded typeface rather than enumerate
# system fonts - so there is no fontconfig dependency to satisfy either.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["RedMist.StatusApi/RedMist.StatusApi.csproj", "RedMist.StatusApi/"]
COPY ["RedMist.Backend.Shared/RedMist.Backend.Shared.csproj", "RedMist.Backend.Shared/"]
COPY ["RedMist.Database/RedMist.Database.csproj", "RedMist.Database/"]
RUN dotnet restore "./RedMist.StatusApi/RedMist.StatusApi.csproj"
COPY . .
WORKDIR "/src/RedMist.StatusApi"
RUN dotnet build "./RedMist.StatusApi.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./RedMist.StatusApi.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RedMist.StatusApi.dll"]
