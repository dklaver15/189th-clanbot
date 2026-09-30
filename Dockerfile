# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ClanGuardBot.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install sqlite3 CLI for DB inspection plus the runtime packages SkiaSharp
# needs to render text on Ubuntu. fontconfig provides the font-discovery API;
# the dejavu-core package supplies an actual TrueType font for the renderer
# to use. Without these, ScottPlot charts render empty axis labels (or fail
# outright depending on the SkiaSharp build) because SkiaSharp can't locate
# any typeface to draw with on a minimal aspnet:10.0 base image.
# ffmpeg/ffprobe: VideoUpscaler scales clips up to 1440p before they're sent
# to YouTube (see BotConfig.VideoUploadUpscaleEnabled).
RUN apt-get update && apt-get install -y \
        sqlite3 \
        libfontconfig1 \
        libfreetype6 \
        fontconfig \
        fonts-dejavu-core \
        ffmpeg \
    && rm -rf /var/lib/apt/lists/*

# Create directories for persistent data
RUN mkdir -p /app/data /app/logs

COPY --from=build /app .

# The SQLite DB and logs will be stored in mounted volumes
ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "ClanGuardBot.dll"]