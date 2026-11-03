# corel-mcp

Standalone Windows MCP server and Codex skill for CorelDRAW **27**.

- Source: TypeScript in src; Windows PowerShell 5.1 STA worker in powershell; skill in skills/coreldraw-automation.
- Use Node.js 24. `npm test` builds and tests MCP/job behavior; `npm run test:integration` tests actual CorelDRAW using temporary documents only.
- Verify CorelDRAW signatures through Context7 library `/websites/community_coreldraw_sdk_api_draw`, API version 27.
- Never print logs to server stdout. Never pass user arguments through a shell or Invoke-Expression.
- Keep COM operations serialized under the named mutex. Polling a job must never call COM.
- Preserve request_id idempotency and unknown-outcome handling. Never replay an orphaned or timed-out mutation automatically.
- Use exact document IDs and stable Shape.StaticID. Do not alter unrelated installed GMS projects or call Application.Quit.
- Integration tests must retain diagnostics and close only the documents they created. Existing user drawings must remain intact.
- Keep the skill, tool schemas, installer and documentation consistent when changing the public interface.
