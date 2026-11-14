import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
test('every fixed native entrypoint catches errors before they can open a modal VBA dialog',()=>{
  for(const [file,name] of [['M_Bridge','Handshake'],['M_Batch','ValidateFonts'],['M_Batch','Apply'],['M_Geometry','Extract']]) {
    const source=readFileSync(new URL(`../worker/vba/${file}.bas`,import.meta.url),'utf8');
    const body=source.match(new RegExp(`Public Function ${name}\\([^]*?End Function`))[0];
    assert.match(body,/On Error GoTo Failed/,`${file}.${name}: нет обработчика внешней границы`);
    assert.match(body,/\nFailed:/,`${file}.${name}: нет возврата ошибки`);
  }
});
