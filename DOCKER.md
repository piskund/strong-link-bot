# Docker Deployment Guide for StrongLink Bot

This guide explains how to containerize and run the StrongLink Bot using Docker.

## Windows Users - Desktop Scripts (Easiest)

If you're on Windows, use the convenient batch scripts instead of manual commands:

1. **First time:** Double-click `setup.bat`
2. **Daily use:** Pin `start-fresh.bat` to desktop and double-click it

See [SCRIPTS_GUIDE.md](SCRIPTS_GUIDE.md) for complete documentation on all scripts.

---

## Quick Start (Manual)

1. **Create `.env` file with your credentials:**
   ```bash
   TELEGRAM_BOT_TOKEN=your_actual_bot_token
   OPENAI_API_KEY=your_actual_openai_key
   ```

3. **Start the bot:**
   ```bash
   docker-compose up -d
   ```

4. **Check logs:**
   ```bash
   docker-compose logs -f
   ```

## Files Overview

- **Dockerfile** - Multi-stage build for .NET 9.0 application
- **docker-compose.yml** - Docker Compose configuration with volumes and resource limits
- **.dockerignore** - Excludes unnecessary files from the Docker build context
- **.env** - Environment variables with your configuration (required)

## Building the Docker Image

### Using Docker Compose (Recommended)

```bash
docker-compose build
```

### Manual Build

```bash
docker build -t stronglink-bot:latest .
```

The Dockerfile uses a multi-stage build:
- **Stage 1 (build)**: Uses `mcr.microsoft.com/dotnet/sdk:9.0` to compile the application
- **Stage 2 (final)**: Uses `mcr.microsoft.com/dotnet/aspnet:9.0` for a smaller runtime image

## Running the Container

### Using Docker Compose (Recommended)

```bash
# Start in detached mode
docker-compose up -d

# View logs
docker-compose logs -f

# Stop the bot
docker-compose down

# Restart
docker-compose restart
```

### Manual Docker Run

```bash
docker run -d \
  --name stronglink-bot \
  --env-file .env \
  -v $(pwd)/data/pool:/app/data/pool \
  -v $(pwd)/data/state:/app/data/state \
  -v $(pwd)/data/results:/app/data/results \
  -v $(pwd)/logs:/app/logs \
  --restart unless-stopped \
  stronglink-bot:latest
```

## Configuration

### Environment Variables

All configuration can be provided via environment variables in the `.env` file:

```env
# Required
TELEGRAM_BOT_TOKEN=your_bot_token_here
OPENAI_API_KEY=your_openai_key_here

# Optional - Bot Settings
BOT__DEFAULTLANGUAGE=ru
BOT__QUESTIONSOURCE=AI
BOT__ADMINUSERIDS__0=123456789

# Optional - Game Settings
GAME__TOURS=999
GAME__ROUNDSPERTOUR=10
GAME__ANSWERTIMEOUTSECONDS=30
GAME__TOPICS=Эротика,Литература,Фильмы

# Optional - OpenAI Settings
OPENAI__MODEL=gpt-5.2
OPENAI__ANSWERVALIDATIONMODEL=gpt-4o-mini
OPENAI__IMAGEPERCENTAGE=30
```

See `.env` for a complete list of available configuration options.

### Volume Mounts

The Docker Compose configuration mounts the following directories:

- **`./data/pool:/app/data/pool`** - Persists question pools (unused and archived questions)
- **`./data/state:/app/data/state`** - Persists active game sessions for recovery after restart
- **`./data/results:/app/data/results`** - Persists completed game results and statistics
- **`./logs:/app/logs`** - Persists application logs (optional)

These directories are automatically created on the host when you start the container.

**Important:** Without these volume mounts:
- Question pools would need to be regenerated after each restart (`/prepare_pool` or `/fetch_pool`)
- Active games would be lost if the container restarts during gameplay
- Game history would not be preserved

### Resource Limits

Default limits in `docker-compose.yml`:
- **Memory**: 512MB limit, 128MB reservation
- **CPU**: 1.0 limit, 0.25 reservation

Adjust these in `docker-compose.yml` under `deploy.resources` if needed:

```yaml
deploy:
  resources:
    limits:
      cpus: '2'
      memory: 1G
    reservations:
      cpus: '0.5'
      memory: 256M
```

## Security Features

The Docker image includes several security best practices:

1. **Non-root user**: Runs as `stronglink` user (not root)
2. **Minimal base image**: Uses `aspnet:9.0` runtime (not SDK)
3. **Layer caching**: Optimized layer order for faster rebuilds
4. **No secrets in image**: All sensitive data via environment variables

## Health Check

The container includes a basic health check:
- **Interval**: 30 seconds
- **Timeout**: 10 seconds
- **Start period**: 5 seconds
- **Retries**: 3

Check container health:
```bash
docker inspect --format='{{.State.Health.Status}}' stronglink-bot
```

## Troubleshooting

### View Logs

```bash
# Docker Compose
docker-compose logs -f

# Docker CLI
docker logs -f stronglink-bot

# Last 100 lines
docker logs --tail 100 stronglink-bot
```

### Check Container Status

```bash
# Docker Compose
docker-compose ps

# Docker CLI
docker ps -a | grep stronglink
```

### Restart Container

```bash
# Docker Compose
docker-compose restart

# Docker CLI
docker restart stronglink-bot
```

### Access Container Shell (Debugging)

```bash
# Start a shell in the running container
docker exec -it stronglink-bot /bin/bash

# If bash is not available, use sh
docker exec -it stronglink-bot /bin/sh
```

### Rebuild from Scratch

```bash
# Docker Compose
docker-compose down
docker-compose build --no-cache
docker-compose up -d

# Docker CLI
docker stop stronglink-bot
docker rm stronglink-bot
docker rmi stronglink-bot:latest
docker build --no-cache -t stronglink-bot:latest .
docker run -d --name stronglink-bot --env-file .env stronglink-bot:latest
```

### Common Issues

**Issue: Container exits immediately**
- Check logs: `docker logs stronglink-bot`
- Verify `.env` file exists and contains valid `TELEGRAM_BOT_TOKEN`
- Ensure the bot token is not the placeholder value

**Issue: Bot doesn't respond to commands**
- Verify the bot is added to your Telegram group
- Check that privacy mode is disabled in @BotFather
- Confirm network connectivity from container

**Issue: Permission denied errors**
- Ensure mounted volumes have correct permissions
- The container runs as UID/GID of the `stronglink` user
- On Linux: `sudo chown -R 1000:1000 data logs`

## Production Deployment

For production environments, consider:

1. **Use secrets management** instead of `.env` files:
   - Docker Secrets (Swarm mode)
   - Kubernetes Secrets
   - HashiCorp Vault

2. **Set up log aggregation**:
   - ELK Stack (Elasticsearch, Logstash, Kibana)
   - Loki + Grafana
   - Cloud logging (AWS CloudWatch, Azure Monitor)

3. **Monitor container health**:
   - Prometheus + Grafana
   - Datadog
   - New Relic

4. **Use container orchestration**:
   - Docker Swarm
   - Kubernetes
   - AWS ECS/Fargate

5. **Regular backups** of the `data` directory:
   ```bash
   # Example backup command
   tar -czf stronglink-backup-$(date +%Y%m%d).tar.gz data/
   ```

## Updating the Bot

1. **Pull latest changes:**
   ```bash
   git pull
   ```

2. **Rebuild and restart:**
   ```bash
   docker-compose down
   docker-compose build
   docker-compose up -d
   ```

3. **Verify the update:**
   ```bash
   docker-compose logs -f
   ```

## Cleanup

### Remove Container and Image

```bash
# Docker Compose
docker-compose down --rmi all

# Docker CLI
docker stop stronglink-bot
docker rm stronglink-bot
docker rmi stronglink-bot:latest
```

### Remove Volumes (⚠️ WARNING: Deletes ALL data)

This will permanently delete:
- All question pools (you'll need to regenerate them)
- Active game sessions
- Game history and results
- All logs

```bash
# Backup first (recommended)
tar -czf stronglink-backup-$(date +%Y%m%d).tar.gz data/ logs/

# Then remove
rm -rf data/ logs/
```

## Development vs Production

**Development** (using .NET SDK locally):
```bash
dotnet run --project src/StrongLink.Worker
```

**Production** (using Docker):
```bash
docker-compose up -d
```

Use Docker for production to ensure:
- Consistent environment across deployments
- Isolation from host system
- Easy updates and rollbacks
- Better resource management

## Support

For issues related to:
- **Docker setup**: Check this guide and Docker logs
- **Bot functionality**: See main README.md
- **Configuration**: See configuration section in README.md

Contact: dmytro.piskun@gmail.com
