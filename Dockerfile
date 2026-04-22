# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ClanGuardBot.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install sqlite3 CLI for DB inspection
RUN apt-get update && apt-get install -y sqlite3 && rm -rf /var/lib/apt/lists/*

# Create directories for persistent data
RUN mkdir -p /app/data /app/logs

COPY --from=build /app .

# The SQLite DB and logs will be stored in mounted volumes
ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "ClanGuardBot.dll"]
