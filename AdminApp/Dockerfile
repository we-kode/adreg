FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
ARG USERNAME=wekode
ARG UID
ARG GID
run groupadd --gid $gid $username \
    && useradd --uid $uid --gid $gid -m $username
user $username
WORKDIR /app
EXPOSE 8080

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["AdminApp/AdminApp.csproj", "AdminApp/"]
RUN dotnet restore "./AdminApp/AdminApp.csproj"
COPY . .
WORKDIR "/src/AdminApp"
RUN dotnet build "./AdminApp.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./AdminApp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AdminApp.dll"]