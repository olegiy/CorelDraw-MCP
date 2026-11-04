import test from 'node:test';
import assert from 'node:assert/strict';
import { toolDefinitions } from '../dist/tools.js';

test('preview accepts target page|selection and keeps range compatibility', () => {
  const schema = toolDefinitions.corel_preview.schema;
  assert.equal(schema.parse({ document_id: 'doc' }).target, 'page');
  assert.equal(schema.parse({ document_id: 'doc', target: 'selection' }).target, 'selection');
  assert.equal(schema.parse({ document_id: 'doc', range: 'selection' }).range, 'selection');
  assert.throws(() => schema.parse({ document_id: 'doc', target: 'page', range: 'selection' }));
  assert.throws(() => schema.parse({ document_id: 'doc', target: 'other' }));
});
