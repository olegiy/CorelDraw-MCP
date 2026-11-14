import test from 'node:test';
import assert from 'node:assert/strict';
import {verifyCopies} from '../scripts/benchmark-correctness.mjs';
test('Проверка копий отвергает верное количество с неверной геометрией или стилем',()=>{
  const source={static_id:1,bounds_mm:{left:10,bottom:20,width:30,height:40},rotation:0,type:1,layer:'Слой',appearance:{fill_color:{r:255,g:0,b:0}}};
  const copy={...structuredClone(source),static_id:2,bounds_mm:{...source.bounds_mm,bottom:70}};
  verifyCopies([source],[source,copy],[2],0,50);
  const wrong=structuredClone(copy);wrong.bounds_mm.bottom=20;
  assert.throws(()=>verifyCopies([source],[source,wrong],[2],0,50));
  wrong.bounds_mm.bottom=70;wrong.appearance.fill_color.r=0;
  assert.throws(()=>verifyCopies([source],[source,wrong],[2],0,50));
  assert.throws(()=>verifyCopies([source],[source,copy],[1],0,50));
});
