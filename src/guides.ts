import { readFileSync } from 'node:fs';
import { AutomationFailure } from './types.js';

const definitions = {
  skill: { title: 'Порядок работы', path: '../skills/coreldraw-automation/SKILL.md' },
  tools: { title: 'Инструменты', path: '../skills/coreldraw-automation/references/tools.md' },
  operations: { title: 'Готовые операции', path: '../skills/coreldraw-automation/references/operations.md' },
  api: { title: 'API CorelDRAW 27', path: '../skills/coreldraw-automation/references/api-v27.md' },
  recipes: { title: 'Рецепты', path: '../skills/coreldraw-automation/references/recipes.md' },
  pitfalls: { title: 'Ограничения', path: '../skills/coreldraw-automation/references/pitfalls.md' },
  install: { title: 'Установка', path: '../README.md' },
};
export const guideTopics = Object.entries(definitions).map(([topic, entry]) => ({
  topic,
  title: entry.title,
  uri: `coreldraw://guide/${topic}`,
}));
export function readGuide(topic: string): string {
  if (!Object.hasOwn(definitions, topic))
    throw new AutomationFailure('GUIDE_NOT_FOUND', 'Неизвестный раздел справки.');
  return readFileSync(
    new URL(definitions[topic as keyof typeof definitions].path, import.meta.url),
    'utf8',
  ).replace(/^\uFEFF/, '');
}
export function guide(topic?: string, offset = 0, limit = 8000): object {
  if (!topic) return { topics: guideTopics };
  const all = readGuide(topic);
  return {
    topic,
    text: all.slice(offset, offset + limit),
    total_characters: all.length,
    next_offset: offset + limit < all.length ? offset + limit : null,
  };
}
