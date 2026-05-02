FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update && \
    apt-get install -y --no-install-recommends libldap2 \
    libldap-common \
    libsasl2-2 \
    libsasl2-modules && \
    rm -rf /var/lib/apt/lists/*

#bug dotnet requires libldap-2.5.so.0 library
RUN ln -sf /usr/lib/x86_64-linux-gnu/libldap.so.2 /usr/lib/x86_64-linux-gnu/libldap-2.5.so.0 && \
    ln -sf /usr/lib/x86_64-linux-gnu/liblber.so.2 /usr/lib/x86_64-linux-gnu/liblber-2.5.so.0

ARG USERNAME=wekode
ARG UID
ARG GID
RUN groupadd --gid $GID $USERNAME \
    && useradd --uid $UID --gid $GID -m $USERNAME
USER $USERNAME
WORKDIR /app
EXPOSE 8080

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["RegistrationApp/RegistrationApp.csproj", "RegistrationApp/"]
RUN dotnet restore "./RegistrationApp/RegistrationApp.csproj"
COPY . .
WORKDIR "/src/RegistrationApp"
RUN dotnet build "./RegistrationApp.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./RegistrationApp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RegistrationApp.dll"]