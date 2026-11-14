export const MAX_WORKER_RECORD_BYTES = 4 * 1024 * 1024;

function checkString(value: string): void {
  for (let i = 0; i < value.length; i++) {
    const unit = value.charCodeAt(i);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = value.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) throw new Error('Некорректный UTF-16 в протоколе.');
    } else if (unit >= 0xdc00 && unit <= 0xdfff) throw new Error('Некорректный UTF-16 в протоколе.');
  }
}

function checkValue(value: unknown, depth = 0, seen = new Set<object>()): void {
  if (depth > 64) throw new Error('Превышена глубина JSON-протокола.');
  if (typeof value === 'string') { checkString(value); return; }
  if (value === null || typeof value === 'boolean') return;
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) throw new Error('Протокол требует конечных чисел.');
    return;
  }
  if (typeof value !== 'object') throw new Error('Значение не представимо в JSON-протоколе.');
  if (seen.has(value)) throw new Error('Циклическое значение не представимо в JSON.');
  if (!Array.isArray(value) && ![Object.prototype, null].includes(Object.getPrototypeOf(value)))
    throw new Error('Протокол принимает только JSON-объекты.');
  seen.add(value);
  if (Array.isArray(value)) for (const item of value) checkValue(item, depth + 1, seen);
  else for (const [key, item] of Object.entries(value)) { checkString(key); checkValue(item, depth + 1, seen); }
  seen.delete(value);
}

export function parseProtocolJson(source: string): unknown {
  if (Buffer.byteLength(source, 'utf8') > MAX_WORKER_RECORD_BYTES) throw new Error('Запись протокола превышает 4 МиБ.');
  // JSON.parse discards duplicate keys. Check their original spelling before parsing,
  // unescaping names so that "x" and "\u0078" are the same key.
  const scopes: ({ kind: 'object'; keys: Set<string>; key: boolean } | { kind: 'array' })[] = [];
  for (let i = 0; i < source.length; i++) {
    const char = source[i];
    if (char === '"') {
      const start = i;
      for (i++; i < source.length; i++) {
        if (source[i] === '\\') { i++; continue; }
        if (source[i] === '"') break;
      }
      const name: unknown = JSON.parse(source.slice(start, i + 1));
      const scope = scopes.at(-1);
      if (scope?.kind === 'object' && scope.key) {
        if (scope.keys.has(name as string)) throw new Error('Повтор имени свойства в JSON-протоколе.');
        scope.keys.add(name as string);
        scope.key = false;
      }
    } else if (char === '{') scopes.push({ kind: 'object', keys: new Set(), key: true });
    else if (char === '[') scopes.push({ kind: 'array' });
    else if (char === '}' || char === ']') scopes.pop();
    else if (char === ',') { const scope = scopes.at(-1); if (scope?.kind === 'object') scope.key = true; }
    if (scopes.length > 64) throw new Error('Превышена глубина JSON-протокола.');
  }
  const value: unknown = JSON.parse(source);
  checkValue(value);
  return value;
}

export function encodeProtocolJson(value: unknown): string {
  checkValue(value);
  const source = JSON.stringify(value);
  if (Buffer.byteLength(source, 'utf8') > MAX_WORKER_RECORD_BYTES) throw new Error('Запись протокола превышает 4 МиБ.');
  return source;
}

export class JsonLineDecoder {
  private readonly buffer = Buffer.allocUnsafe(MAX_WORKER_RECORD_BYTES + 1);
  private readonly decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
  private size = 0;
  private dropping = false;
  private ended = false;
  private readonly message: (value: unknown) => void;
  private readonly error: (message: string) => void;
  constructor(message: (value: unknown) => void, error: (message: string) => void) {
    this.message = message;
    this.error = error;
  }
  feed(buffer: Uint8Array): void {
    if (this.ended) return;
    for (let offset = 0; offset < buffer.length;) {
      const newline = buffer.indexOf(10, offset);
      const end = newline === -1 ? buffer.length : newline;
      const length = end - offset;
      if (!this.dropping) {
        if (this.size + length > this.buffer.length) {
          this.size = 0;
          this.dropping = true;
          this.error('Запись протокола превышает 4 МиБ; ожидается граница следующей записи.');
        } else {
          this.buffer.set(buffer.subarray(offset, end), this.size);
          this.size += length;
        }
      }
      if (newline === -1) return;
      if (!this.dropping) {
        const size = this.size > 0 && this.buffer[this.size - 1] === 13 ? this.size - 1 : this.size;
        let value: unknown;
        let valid = false;
        try {
          if (size > MAX_WORKER_RECORD_BYTES) throw new Error('Запись протокола превышает 4 МиБ.');
          value = parseProtocolJson(this.decoder.decode(this.buffer.subarray(0, size)));
          valid = true;
        } catch { this.error('Некорректная запись JSONL-протокола; операция не считается завершённой.'); }
        this.size = 0;
        if (valid) this.message(value);
      }
      this.dropping = false;
      this.size = 0;
      offset = end + 1;
    }
  }
  end(): void {
    if (this.ended) return;
    this.ended = true;
    if (this.size > 0 && !this.dropping) this.error('EOF внутри записи протокола; результат не получен.');
    this.size = 0;
  }
}
