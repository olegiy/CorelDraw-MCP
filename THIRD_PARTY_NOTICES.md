# Заимствования панели CorelDRAW

Донор: CAD-Agent-Next, локальный репозиторий `C:\Users\olegiy\CAD-Agent-Next`.
Исходный commit: `37608087bfa52c58f8a9cb48ebda39ad0c1ebcae`.

Выборочная адаптация: `ui/src/MessageContent.tsx`, визуальные правила
`ui/src/styles.css`, независимая конфигурация HTTP-клиента из
`cadnext/provider.py`, принципы immutable settings из `cadnext/settings.py`.
Runtime, Corel MCP adapter, access/policy, durable ledger и launcher реализуются
отдельно. SolidWorks executor, mesh, автоматическая остановка при pagehide/SSE
disconnect, restore_python и чужие пользовательские данные не переносятся.

## MIT Notice

MIT License

Copyright (c) 2026 AI for You

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
