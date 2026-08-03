# Docker & Container Setup Guide

## Overview

This guide covers containerization of MikroTik UpdateServer using Docker.

## Prerequisites

- Docker 20.10+
- Docker Compose 2.0+ (optional, for local development)
- Git

## Quick Start

### Build Docker Image

```bash
# Build image locally
docker build -t mikrotik-updateserver:latest .

# Build with specific tag
docker build -t mikrotik-updateserver:1.0.0 .
```

### Run Container

```bash
# Run with default settings
docker run -d \
  --name mikrotik-updateserver \
  -p 5000:5000 \
  -v ./logs:/app/logs \
  -v ./routeros:/app/routeros \
  mikrotik-updateserver:latest

# Run with environment variables
docker run -d \
  --name mikrotik-updateserver \
  -p 5000:5000 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -v ./logs:/app/logs \
  -v ./routeros:/app/routeros \
  mikrotik-updateserver:latest
```

### Using Docker Compose

```bash
# Build and start all services
docker-compose up -d

# View logs
docker-compose logs -f mikrotik-updateserver

# Stop services
docker-compose down

# Stop and remove volumes
docker-compose down -v
```

## Docker Compose Configuration

The `docker-compose.yml` file includes:

- **Image Build**: Builds from Dockerfile in current directory
- **Port Mapping**: Port 5000 (HTTP)
- **Volumes**:
  - `./logs` - Application logs (read/write)
  - `./routeros` - UpdateOS files (read/write)
  - `./appsettings.json` - App configuration (read-only)
- **Environment**:
  - `ASPNETCORE_ENVIRONMENT=Development`
  - `ASPNETCORE_URLS=http://+:5000`
- **Health Check**: Validates HTTP endpoint every 30 seconds
- **Auto Restart**: Restarts unless manually stopped

## Dockerfile Stages

### 1. Builder Stage
- Base: `mcr.microsoft.com/dotnet/sdk:9.0`
- Purpose: Restore and build application
- Output: Compiled binaries

### 2. Publisher Stage
- Extends: Builder stage
- Purpose: Publish application
- Output: Deployment-ready files

### 3. Runtime Stage
- Base: `mcr.microsoft.com/dotnet/aspnet:9.0`
- Purpose: Run application in minimal container
- Features:
  - Includes curl for health checks
  - Creates logs and routeros directories
  - Exposes port 5000
  - Health check configured

## Volume Mounting

### Production Setup

```bash
docker run -d \
  --name mikrotik-updateserver \
  -p 5000:5000 \
  -v /data/mikrotik/logs:/app/logs \
  -v /data/mikrotik/routeros:/app/routeros \
  -v /data/mikrotik/appsettings.json:/app/appsettings.json:ro \
  -e ASPNETCORE_ENVIRONMENT=Production \
  mikrotik-updateserver:latest
```

### Directory Structure on Host

```
/data/mikrotik/
├── logs/                    (Application logs)
│   ├── app-20241202.json
│   ├── app-20241202.txt
│   ├── errors-20241202.txt
│   └── ...
├── routeros/               (RouterOS update files)
│   ├── LATEST.6
│   ├── LATEST.7
│   ├── 6.48.1-arm
│   ├── 7.13-arm64
│   └── ...
└── appsettings.json        (Configuration)
```

## Environment Variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Execution environment (Development/Production) |
| `ASPNETCORE_URLS` | `http://+:5000` | Listening URL and port |
| `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` | `false` | Enable globalization support |

## Health Check

Container includes built-in health check:

```bash
# Manual health check
curl http://localhost:5000/health

# View health status
docker ps

# Check health logs
docker inspect --format='{{json .State.Health}}' mikrotik-updateserver
```

## Logging in Container

Logs are automatically written to:
- **Console**: Real-time output visible via `docker logs`
- **File**: Stored in mounted `/app/logs` directory

```bash
# View container logs
docker logs mikrotik-updateserver

# Follow logs in real-time
docker logs -f mikrotik-updateserver

# View last 100 lines
docker logs --tail 100 mikrotik-updateserver

# View logs with timestamps
docker logs -t mikrotik-updateserver
```

## Network Configuration

### Port Mapping

```bash
# Map to different host port
docker run -p 8080:5000 mikrotik-updateserver:latest

# Map to specific host IP
docker run -p 192.168.1.10:5000:5000 mikrotik-updateserver:latest
```

### Docker Compose Network

- Network Name: `mikrotik-network` (bridge)
- Service Address: `mikrotik-updateserver:5000`
- External Access: `localhost:5000`

## Troubleshooting

### Container won't start

```bash
# Check container logs
docker logs mikrotik-updateserver

# Inspect container status
docker inspect mikrotik-updateserver

# Check for port conflicts
netstat -tulpn | grep 5000
```

### Permission issues with volumes

```bash
# Fix directory permissions
sudo chown -R 1000:1000 ./logs
sudo chown -R 1000:1000 ./routeros
chmod -R 755 ./logs
chmod -R 755 ./routeros
```

### Disk space issues

```bash
# Check Docker disk usage
docker system df

# Remove unused images
docker image prune -a

# Remove unused volumes
docker volume prune

# Remove all unused objects
docker system prune -a
```

## Performance Optimization

### Build Optimization

The Dockerfile uses multi-stage build to:
- Minimize final image size
- Reduce runtime dependencies
- Speed up container startup

### Runtime Optimization

- **Health Check**: 30-second interval (adjustable)
- **Restart Policy**: `unless-stopped`
- **Resource Limits**: Can be set via compose or run command

```yaml
services:
  mikrotik-updateserver:
    # ... other config ...
    deploy:
      resources:
        limits:
          cpus: '2'
          memory: 512M
        reservations:
          cpus: '1'
          memory: 256M
```

## Security Considerations

### Best Practices

1. **Use read-only filesystems where possible**
   ```bash
   docker run --read-only \
     --tmpfs /tmp \
     -v ./appsettings.json:/app/appsettings.json:ro \
     mikrotik-updateserver:latest
   ```

2. **Run as non-root user** (handled in Dockerfile)

3. **Limit container capabilities**
   ```bash
   docker run --cap-drop=ALL \
     --cap-add=NET_BIND_SERVICE \
     mikrotik-updateserver:latest
   ```

4. **Scan images for vulnerabilities**
   ```bash
   # Using Trivy
   trivy image mikrotik-updateserver:latest
   ```

5. **Use environment files instead of inline variables**
   ```bash
   docker run --env-file .env mikrotik-updateserver:latest
   ```

## CI/CD Integration

### GitHub Actions

Workflows are provided in `.github/workflows/`:

1. **docker-build.yml** - Builds and pushes Docker image to ghcr.io
2. **dotnet-build.yml** - Builds .NET project and tests

Triggers:
- Push to main/develop branches
- Pull requests to main/develop
- Tag push (version tags like v1.0.0)

## Image Size

Expected image sizes:
- SDK stage: ~750MB (build only, not in final image)
- Runtime stage: ~200-250MB (final deployable image)

## Advanced Usage

### Multi-architecture builds

```bash
# Build for multiple architectures
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t mikrotik-updateserver:latest \
  --push .
```

### Registry push

```bash
# Tag for registry
docker tag mikrotik-updateserver:latest myregistry.azurecr.io/mikrotik-updateserver:latest

# Push to Azure Container Registry
docker push myregistry.azurecr.io/mikrotik-updateserver:latest

# Push to Docker Hub
docker tag mikrotik-updateserver:latest username/mikrotik-updateserver:latest
docker push username/mikrotik-updateserver:latest
```

## Support

For issues or questions:
1. Check container logs: `docker logs mikrotik-updateserver`
2. Review Dockerfile for build issues
3. Check GitHub Issues for known problems
4. Consult .NET documentation for runtime issues
