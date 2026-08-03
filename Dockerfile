# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder

WORKDIR /src

# Copy project files
COPY ["MikroTik.UpdateServer.csproj", "./"]

# Restore dependencies
RUN dotnet restore "MikroTik.UpdateServer.csproj"

# Copy source code
COPY . .

# Build application
RUN dotnet build "MikroTik.UpdateServer.csproj" -c Release -o /app/build

# Publish stage
FROM builder AS publisher

WORKDIR /src
RUN dotnet publish "MikroTik.UpdateServer.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy published application from publisher stage
COPY --from=publisher /app/publish .

# Create logs directory
RUN mkdir -p logs && chmod -R 777 logs

# Create routeros directory for update files
RUN mkdir -p routeros && chmod -R 777 routeros

# Expose port
EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

# Environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Run application
ENTRYPOINT ["dotnet", "MikroTik.UpdateServer.dll"]
