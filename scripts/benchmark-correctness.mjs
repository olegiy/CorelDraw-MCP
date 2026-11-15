import assert from 'node:assert/strict';
const near=(a,b)=>assert(Math.abs(a-b)<=.001,`${a} != ${b}`);
export function requireCompleteBenchmark(report) {
  assert.equal(report.completed,true,'Серия не завершена');
  assert.equal(report.full_pairs_completed,true,'Не все пары измерений завершены');
  assert.equal(report.stopped.length,0,'Серия остановлена по бюджету');
  assert(!report.error,'Серия содержит ошибку');
  assert(report.cleanup.length>0&&report.cleanup.every(item=>item.closed===true),'Cleanup серии не подтверждён');
  assert(Array.isArray(report.requested_cases)&&report.requested_cases.length>0&&new Set(report.requested_cases).size===report.requested_cases.length);
  assert(Number.isInteger(report.requested_samples)&&report.requested_samples>=1&&report.requested_samples<=5);
  assert(Number.isInteger(report.budget_ms)&&report.budget_ms>=1&&report.budget_ms<=1800000);
  const count=report.requested_cases.length*2*(report.requested_samples+1);
  assert.equal(report.samples.length,count,'Неверное число samples');
  assert.equal(report.cleanup.length,count,'Неверное число cleanup');
  assert.equal(new Set(report.samples.map(row=>row.document_id)).size,count,'Fixture повторена');
  const keys=new Set();
  for(const row of report.samples) {
    assert(report.requested_cases.includes(row.case)&&['csharp','hybrid'].includes(row.mode));
    assert(Number.isInteger(row.iteration)&&row.iteration>=-1&&row.iteration<report.requested_samples);
    assert.equal(row.warmup,row.iteration===-1);
    assert(typeof row.document_id==='string'&&row.document_id.length>0);
    assert(Number.isFinite(row.total_ms)&&row.total_ms>0&&row.total_ms<=report.budget_ms);
    assert(typeof row.correctness==='string'&&row.correctness.length>0,'Нет postcheck');
    const key=`${row.case}:${row.mode}:${row.iteration}`;assert(!keys.has(key),'Повторный sample');keys.add(key);
    assert.equal(report.cleanup.filter(item=>item.case===row.case&&item.mode===row.mode&&item.iteration===row.iteration&&item.document_id===row.document_id&&item.closed===true).length,1,'Cleanup относится к другой fixture');
  }
}
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
