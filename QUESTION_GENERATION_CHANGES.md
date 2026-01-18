# Question Generation Strategy Changes

## Summary

Revised the question generation strategy to be more efficient and dynamic:

1. **Generate only 1 tour ahead** - No longer pre-generate questions for all tours
2. **Skip generation if pool has questions** - Don't generate if there are already unused questions
3. **Probability-based topic selection** - 70% from Topics list, 30% completely random/new topics

## Changes Made

### 1. AIQuestionProvider.cs

Added a new static method for probabilistic topic selection:

```csharp
public static string SelectTopicWithProbability(IReadOnlyList<string> suggestedTopics, Random? random = null)
```

**Logic:**
- 70% chance: Select a random topic from the suggested Topics list
- 30% chance: Return empty string (signals AI to generate its own random topic)

### 2. PreparePoolCommandHandler.cs (/prepare_pool command)

**Before:**
- Generated questions for ALL tours (e.g., 6 tours × 30 questions = 180 questions)
- Always generated even if pool had questions
- Used predefined topics from session.Topics list in order

**After:**
- Checks if pool has enough unused questions (≥ requiredPerTour)
- If pool has questions: Skip generation, inform user, mark session as ready
- If pool is empty: Generate only 1 tour worth of questions
- Uses probability-based topic selection (70% from Topics, 30% random)
- Adds ALL generated questions to unused pool (not to session)
- Questions are selected dynamically during gameplay

**User Experience:**
```
✅ В пуле уже есть 45 неиспользованных вопросов (нужно 30 на тур).
Генерация не требуется. Используйте /begin для старта игры.
```

Or if generation is needed:
```
🤖 Генерирую вопросы для 1 тура: "Шахматы"...
✅ Пул готов! Добавлено 35 новых вопросов в общий пул.
Вопросы будут автоматически выбираться для каждого тура во время игры.
```

### 3. GameLifecycleService.cs - PrepareNextTourQuestionsAsync()

**Before:**
- Tried to get questions by specific topic from session.Topics
- Generated if not enough for that specific topic
- Assigned topic based on tour index

**After:**
- Checks pool stats first
- Gets questions from pool (ANY topic, not filtered by specific topic)
- If enough questions in pool: Use them as-is with their existing topics
- If not enough: Generate with probability-based topic selection
  - 70% chance: Pick random topic from Topics list
  - 30% chance: Let AI choose a completely new random topic
- Stores surplus questions in pool for future use

**Benefits:**
- More variety - topics aren't tied to specific tour numbers
- Better resource usage - reuses any available questions
- Dynamic topic selection throughout the game

## How It Works Now

### Initial Setup (/prepare_pool)

1. User runs `/prepare_pool`
2. Bot checks: "Do we have ≥30 unused questions?"
   - **YES**: "Pool ready, no generation needed. Use /begin"
   - **NO**: Generate 1 tour (e.g., 30 questions) with random topic selection
3. Questions go into general unused pool

### During Gameplay (between tours)

1. Tour completes, bot needs questions for next tour
2. Checks unused pool: "Do we have ≥30 questions?"
   - **YES**: Take 30 questions (any topics) from pool
   - **NO**: Generate 1 tour with probability-based topic:
     - 70% chance: Pick from ["Шахматы", "Космос", "История", "Наука", "Литература", "Фильмы", "Фантастика", "Спорт"]
     - 30% chance: Let AI choose completely new random topic (e.g., "Архитектура", "Музыка", "Биология")
3. Use questions for current tour, save surplus to pool

### Example Game Flow

**Game Start:**
- Pool: 0 questions
- Run `/prepare_pool`
- Generates 35 questions on "Литература" (70% probability - from Topics list)
- Pool: 35 questions

**Tour 1:**
- Uses 30 questions from pool
- Pool: 5 questions remaining

**After Tour 1 (between tours):**
- Pool: 5 questions (not enough for Tour 2)
- Auto-generates 35 questions on random topic (30% probability - AI chose "Музыка")
- Pool: 5 + 35 = 40 questions

**Tour 2:**
- Uses 30 questions (mix of Literatura and Muzyka topics)
- Pool: 10 questions remaining

**And so on...**

## Benefits

### 1. Cost Savings
- **Before**: Generate 180 questions upfront (6 tours × 30 questions)
- **After**: Generate ~35 questions at a time, only when needed
- Typical 6-tour game: ~70-105 questions generated (vs 180)
- **Savings**: ~40-60% fewer API calls

### 2. Better Question Variety
- 30% of the time, AI generates completely new topics not in the configured list
- Keeps games fresh and unpredictable
- Players experience diverse topics across multiple games

### 3. Resource Efficiency
- Doesn't waste questions on tours that might not be played
- Games often end early (via elimination) before reaching all 6 tours
- Questions are reused across multiple games

### 4. Dynamic Gameplay
- Topics aren't tied to specific tour numbers
- Each game can have different topic orders
- More replay value

## Configuration

The Topics list in appsettings.json or .env now serves as a **suggestion pool** rather than a rigid schedule:

```json
"Game": {
  "Topics": ["Шахматы", "Космос", "История", "Наука", "Литература", "Фильмы", "Фантастика", "Спорт"]
}
```

Or in .env:
```env
GAME__TOPICS=Шахматы,Космос,История,Наука,Литература,Фильмы,Фантастика,Спорт
```

**Probability:**
- 70% of the time: Random selection from this list
- 30% of the time: AI generates a new topic (e.g., "Архитектура", "Кулинария", "Мода", "Технологии")

## Backward Compatibility

✅ Existing question pools will continue to work
✅ Games in progress won't be affected
✅ All existing commands work the same way
✅ Topics configuration remains the same format

The only visible changes:
- `/prepare_pool` might skip generation if pool has questions
- Topics may appear in different orders across games
- New unexpected topics may appear (30% probability)

## Testing Recommendations

1. **Test /prepare_pool with empty pool** - Should generate 1 tour
2. **Test /prepare_pool with full pool** - Should skip generation
3. **Run multiple games** - Verify topics vary and new topics appear occasionally
4. **Check pool stats** - Use `/pool_status` to verify questions are being reused
5. **Monitor costs** - Should see 40-60% reduction in generation API calls

## Monitoring

Track these metrics to verify the changes are working:

- Average questions generated per game (should be 70-105 vs 180)
- Pool reuse rate (should see questions reused across games)
- Topic diversity (should see topics outside the configured list ~30% of the time)
- Generation API calls (should decrease by 40-60%)
