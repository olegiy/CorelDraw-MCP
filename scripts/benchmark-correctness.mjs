import assert from 'node:assert/strict';
const near=(a,b)=>assert(Math.abs(a-b)<=.001,`${a} != ${b}`);
export function verifyCopies(before,after,createdIds,dx,dy) {
  assert.equal(createdIds.length,before.length);
  assert.equal(new Set(createdIds).size,createdIds.length);
  assert.equal(after.length,before.length*2);
  const map=new Map(after.map(s=>[s.static_id,s]));assert.equal(map.size,after.length);
  before.forEach((source,i)=>{
    assert.deepEqual(map.get(source.static_id),source,'Оригинал изменился при копировании');
    assert(!before.some(s=>s.static_id===createdIds[i]));
    const copy=map.get(createdIds[i]);assert(copy,'Созданный ID отсутствует в независимой инспекции');
    near(copy.bounds_mm.left,source.bounds_mm.left+dx);near(copy.bounds_mm.bottom,source.bounds_mm.bottom+dy);
    near(copy.bounds_mm.width,source.bounds_mm.width);near(copy.bounds_mm.height,source.bounds_mm.height);
    near(copy.rotation,source.rotation);assert.equal(copy.type,source.type);assert.equal(copy.layer,source.layer);
    assert.deepEqual(copy.appearance,source.appearance);assert.equal(copy.text,source.text);
  });
}
