import test from 'node:test';
import assert from 'node:assert/strict';
import {verifyCopies,requireCompleteBenchmark} from '../scripts/benchmark-correctness.mjs';

test('incomplete pairs budget stops errors or pending cleanup cannot pass benchmark acceptance',()=>{
  const good=completeReport();
  assert.doesNotThrow(()=>requireCompleteBenchmark(good));
  for(const changed of [{completed:false},{full_pairs_completed:false},{stopped:[{reason:'budget'}]},
    {cleanup:[{closed:false}]},{error:'native failure'}])
    assert.throws(()=>requireCompleteBenchmark({...good,...changed}));
});

function completeReport() {
  const samples = [
    { case: 'tiles20', mode: 'csharp', iteration: -1, warmup: true, document_id: 'cw', total_ms: 20, correctness: 'Все цели проверены' },
    { case: 'tiles20', mode: 'hybrid', iteration: -1, warmup: true, document_id: 'hw', total_ms: 10, correctness: 'Все цели проверены' },
    { case: 'tiles20', mode: 'csharp', iteration: 0, warmup: false, document_id: 'c1', total_ms: 20, correctness: 'Все цели проверены' },
    { case: 'tiles20', mode: 'hybrid', iteration: 0, warmup: false, document_id: 'h1', total_ms: 10, correctness: 'Все цели проверены' },
  ];
  return { completed: true, full_pairs_completed: true, stopped: [], requested_cases: ['tiles20'],
    requested_samples: 1, budget_ms: 300000, samples,
    cleanup: samples.map(({case: name, mode, iteration, document_id}) => ({case:name, mode, iteration, document_id, closed:true})) };
}

test('actual sample rows and fixture bindings must prove complete pairs despite claimed flags', () => {
  const changes = [
    report => report.samples.pop(),
    report => report.samples.push({...report.samples[0]}),
    report => { report.samples[3].iteration = -1; },
    report => { report.samples[3].document_id = 'c1'; },
    report => { report.samples[3].total_ms = NaN; },
    report => { report.samples[3].total_ms = 300001; },
    report => { delete report.samples[3].correctness; },
    report => { report.cleanup[3].document_id = 'wrong'; },
    report => { report.requested_samples = 2; },
    report => { report.requested_cases = ['tiles20', 'tiles20']; },
  ];
  for (const change of changes) {
    const report = completeReport(); change(report);
    assert.throws(() => requireCompleteBenchmark(report));
  }
});
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
