# Scripts

| Script | Purpose |
|---|---|
| `deploy.sh` | CI/CD: run tests, bump version, rebuild, restart |
| `start.bat` | Start with existing image; `start.bat rebuild` to rebuild first |
| `start-fresh.bat` | `git pull` + clean `--no-cache` rebuild + start |
| `stop.bat` | Stop the container |
| `status.bat` | Container status + last 20 log lines |
| `logs.bat` | Live log tail |
| `diagnose.bat` | Health check + last 50 lines + optional clean rebuild |
| `debug.bat` | `debug.bat enable/disable/view` — all debug mode in one |
