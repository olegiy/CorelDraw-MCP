import { defineConfig } from '@playwright/test';

export default defineConfig({ testDir: './tests', fullyParallel: false, workers: 1,
  use: { baseURL: 'http://127.0.0.1:18879', channel: 'msedge', trace: 'retain-on-failure' },
  webServer: [
    { command: 'npm run dev -- --port 18879', url: 'http://127.0.0.1:18879', reuseExistingServer: false },
    { command: '..\\webhost\\.venv\\Scripts\\python.exe ..\\webhost\\tests\\browser_host.py', url: 'http://127.0.0.1:18880/', reuseExistingServer: false },
  ],
});
