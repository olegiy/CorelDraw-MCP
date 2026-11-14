import * as z from 'zod';
import { toolDefinitions } from '../../../dist/tools.js';
console.log(JSON.stringify(Object.entries(toolDefinitions).map(([name, tool]) => ({
  name, description: tool.description, input_schema: z.toJSONSchema(tool.schema, { io: 'input' }),
  annotations: { readOnlyHint: tool.readOnly === true },
}))));
