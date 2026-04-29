# Topics Configuration Guide

## Single Source of Truth: .env File

Topics are now configured in **ONE place only**: the `.env` file.

### ✅ Correct Way (use this)

**In `.env` file:**
```env
GAME__TOPICS=Шахматы,Космос,История,Наука,Литература,Фильмы,Фантастика,Спорт
```

Or for English:
```env
GAME__TOPICS=Chess,Space,History,Science,Literature,Movies,Fantasy,Sports
```

### ❌ Don't Configure Here

- **NOT in appsettings.json** - This file no longer contains Topics configuration
- **NOT in appsettings.Development.json** - Use .env for all environments
- **NOT in GameOptions.cs** - The array there is just a fallback default

## Configuration Hierarchy

The bot uses this precedence (later overrides earlier):

1. **GameOptions.cs** (hardcoded fallback) - Only used if nothing else is configured
2. **appsettings.json** - ~~No longer contains Topics~~ ✅ Removed
3. **.env file** - **PRIMARY CONFIGURATION** ⭐ Use this!

## How Topics Work Now

### Probability-Based Selection

When the bot needs a topic for a tour:
- **70% chance**: Selects random topic from your `GAME__TOPICS` list
- **30% chance**: AI generates a completely new random topic

### Dynamic, Not Sequential

Topics are **NOT** tied to specific tour numbers:
- ❌ Old way: Tour 1 = first topic, Tour 2 = second topic, etc.
- ✅ New way: Each tour randomly selects from the pool or generates new topic

### Examples

**Your .env file:**
```env
GAME__TOPICS=Шахматы,Космос,История,Наука,Литература,Фильмы,Фантастика,Спорт
```

**Game 1 might use:**
1. Литература (70% - from list)
2. Музыка (30% - AI generated)
3. Космос (70% - from list)
4. Фильмы (70% - from list)

**Game 2 might use:**
1. Спорт (70% - from list)
2. История (70% - from list)
3. Архитектура (30% - AI generated)
4. Наука (70% - from list)

Each game has a different mix of topics and order!

## Configuration Steps

### For Development (.NET)

1. Copy template:
   ```bash
   copy env_template.txt .env
   ```

2. Edit `.env` and set your topics:
   ```env
   GAME__TOPICS=Шахматы,Космос,История,Наука,Литература,Фильмы,Фантастика,Спорт
   ```

3. Run the bot:
   ```bash
   dotnet run --project src/StrongLink.Worker
   ```

### For Production (Docker)

1. Edit `.env` and set your topics:
   ```env
   GAME__TOPICS=Шахматы,Космос,История,Наука,Литература,Фильмы,Фантастика,Спорт
   ```

2. Start container:
   ```bash
   docker-compose up -d
   ```

## Default Topics

If you don't configure `GAME__TOPICS` anywhere, the bot will use this fallback from `GameOptions.cs`:

```csharp
["Шахматы", "Космос", "История", "Наука", "Литература", "Фильмы", "Фантастика", "Спорт"]
```

**Recommendation:** Always explicitly configure topics in `.env` for clarity.

## Topic Customization Examples

### Family-Friendly Topics (Russian)
```env
GAME__TOPICS=Сказки,Мультфильмы,Животные,Космос,История,Природа,Путешествия,Спорт
```

### Mature Content (Russian)
```env
GAME__TOPICS=Эротика,Литература,Фильмы,Фантастика,История,Искусство,Мифология,Психология
```

### Educational Topics (English)
```env
GAME__TOPICS=Science,History,Geography,Mathematics,Literature,Art,Technology,Biology
```

### Pop Culture (English)
```env
GAME__TOPICS=Movies,Music,TV Shows,Video Games,Comics,Sports,Celebrities,Fashion
```

### Mixed Topics (Russian + diverse)
```env
GAME__TOPICS=Шахматы,Космос,История,Наука,Литература,Фильмы,Кулинария,Мода,Технологии,Музыка
```

## Why This Change?

### Before (Multiple Configuration Points)

```
GameOptions.cs: ["Шахматы", "Литература", ...]
       ↓ (overridden by)
appsettings.json: ["Эротика", "Литература", ...]
       ↓ (overridden by)
.env: GAME__TOPICS=Шахматы,Космос,...
```

**Problems:**
- Confusing - where are topics actually configured?
- Easy to have conflicts between files
- Hard to track what's actually being used

### After (Single Source)

```
.env: GAME__TOPICS=Шахматы,Космос,История,Наука,...
  └─> Used by bot (with 70/30 probability selection)
```

**Benefits:**
- ✅ Clear single source of truth
- ✅ Easy to change (just edit .env)
- ✅ Works consistently across environments
- ✅ Environment-specific customization without code changes

## Troubleshooting

### How do I know which topics are being used?

Check the bot logs during game start:
```
[INFO] Selected topic for generation: 'Космос' (random: False)
[INFO] Selected topic for generation: 'Музыка' (random: True)
```

- `random: False` = Selected from your GAME__TOPICS list (70%)
- `random: True` = AI generated new topic (30%)

### I changed topics in .env but bot still uses old ones

1. **For .NET development:**
   - Restart the bot: `Ctrl+C` and run again
   - The .env file is loaded at startup

2. **For Docker:**
   ```bash
   docker-compose restart
   ```
   - Container needs to restart to reload .env

### Topics are in Russian but I want English

Replace the topics in `.env`:
```env
# Before
GAME__TOPICS=Шахматы,Космос,История,Наука,Литература,Фильмы,Фантастика,Спорт

# After
GAME__TOPICS=Chess,Space,History,Science,Literature,Movies,Fantasy,Sports
```

### Can I use both Russian and English topics?

Yes! Mix them freely:
```env
GAME__TOPICS=Шахматы,Space,История,Science,Фильмы,Movies,Спорт,Technology
```

The AI will generate questions in the configured game language regardless of topic language.

### How many topics should I configure?

**Recommendation:** 6-10 topics

- **Too few (1-3):** Less variety, players may see same topics often
- **Good (6-10):** Nice balance of variety and familiarity
- **Too many (20+):** Topics selected less frequently, harder to prepare for

Remember: 30% of the time, AI generates completely new topics anyway!

## Migration from Old Configuration

If you previously had topics in `appsettings.json`:

1. **Find your topics** in `appsettings.json`:
   ```json
   "Topics": ["Эротика", "Литература", "Фильмы", "Фантастика", "Анекдоты"]
   ```

2. **Move to .env**:
   ```env
   GAME__TOPICS=Эротика,Литература,Фильмы,Фантастика,Анекдоты
   ```

3. **Remove from appsettings.json** (already done - the Topics field has been removed)

4. **Restart bot** to pick up new configuration

## Summary

✅ **DO:**
- Configure topics in `.env` file only
- Use comma-separated format: `GAME__TOPICS=Topic1,Topic2,Topic3`
- Restart bot/container after changing topics
- Use 6-10 topics for good variety

❌ **DON'T:**
- Configure topics in appsettings.json (field removed)
- Try to control which tour gets which topic (it's random now)
- Expect same topics in same order across games (dynamic selection)
- Forget to restart after changing configuration
