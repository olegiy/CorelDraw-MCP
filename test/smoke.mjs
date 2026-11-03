import { connect, operation } from './helpers.mjs';
const session = await connect();
try { console.log(JSON.stringify(await operation(session, 'corel_status'), null, 2)); }
finally { console.error(session.stderr()); await session.close(); }
