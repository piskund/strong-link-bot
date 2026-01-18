# Use .NET 9.0 SDK for build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Set metadata for easier identification in Rancher Desktop
LABEL maintainer="your-email@example.com"
LABEL description="StrongLink Bot - Telegram Bot for Strong Link Game"
LABEL version="2.0.0"

# Set working directory
WORKDIR /src

# Copy solution and project files first for better Docker layer caching
COPY StrongLink.sln ./
COPY src/StrongLink.Worker/StrongLink.Worker.csproj ./src/StrongLink.Worker/

# Restore dependencies (only for the main project, not tests)
RUN dotnet restore src/StrongLink.Worker/StrongLink.Worker.csproj

# Copy the rest of the source code
COPY . .

# Build the project
WORKDIR /src/src/StrongLink.Worker
RUN dotnet build -c Release -o /app/build

# Publish the application
RUN dotnet publish -c Release -o /app/publish --no-restore

# Use .NET 9.0 runtime for final stage (smaller image)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

# Set working directory
WORKDIR /app

# Set environment variables
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_EnableDiagnostics=0

# Copy published application from build stage
COPY --from=build /app/publish .

# Create data directories for game results, logs, and debug logs with proper permissions
RUN mkdir -p /app/data/results /app/logs /app/debug-logs && \
    chmod 755 /app/data /app/data/results /app/logs /app/debug-logs

# Note: Running as root for simplicity with volume mounts
# This is acceptable for a personal bot with local usage

# Health check for monitoring
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD dotnet --info > /dev/null || exit 1

# Expose port for future use (if needed for metrics/health endpoints)
EXPOSE 8080

# Start the worker service
ENTRYPOINT ["dotnet", "StrongLink.Worker.dll"]
