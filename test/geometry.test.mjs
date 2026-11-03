import test from 'node:test';
import assert from 'node:assert/strict';
import { measureSnapshot } from '../dist/geometry.js';

const line = (a,b) => ({kind:'line',p0:a,p1:b});

test('измеряет повернутый прямоугольник и агрегат', () => {
  const s= {objects:[{static_id:7,segments:[
    line({x:0,y:0},{x:18,y:24}), line({x:18,y:24},{x:2,y:36}),
    line({x:2,y:36},{x:-16,y:12}), line({x:-16,y:12},{x:0,y:0})
  ]}]};
  const r=measureSnapshot(s,.001); const o=r.objects[0];
  assert.equal(r.units,'mm'); assert.equal(o.static_id,7);
  assert.ok(Math.abs(o.rectangle.area_mm2-600)<1e-6);
  assert.ok(Math.abs(o.rectangle.width_mm-30)<1e-6);
  assert.ok(Math.abs(o.rectangle.height_mm-20)<1e-6);
  assert.ok(o.min_width_mm>19.99 && o.min_width_mm<20.01);
  assert.equal(r.aggregate.rectangle.area_mm2,o.rectangle.area_mm2);
});

test('адаптивно аппроксимирует кубическую кривую по отклонению от хорды', () => {
  const s={objects:[{static_id:1,segments:[{kind:'cubic',p0:{x:0,y:0},p1:{x:0,y:10},p2:{x:10,y:10},p3:{x:10,y:0}}]}]};
  const r=measureSnapshot(s,.01); assert.ok(r.objects[0].rectangle.height_mm>7.4);
});

test('отклоняет NaN, неверную tolerance и превышение лимита', () => {
  assert.throws(()=>measureSnapshot({objects:[{static_id:1,segments:[line({x:NaN,y:0},{x:1,y:0})]}]},.01), e=>e.code==='GEOMETRY_INVALID_INPUT');
  assert.throws(()=>measureSnapshot({objects:[]},.0001), e=>e.code==='GEOMETRY_INVALID_INPUT');
  assert.throws(()=>measureSnapshot({objects:Array.from({length:101},(_,i)=>({static_id:i+1,segments:[]}))},.01), e=>e.code==='GEOMETRY_LIMIT_EXCEEDED');
});

const poly = (points,id=1)=>({static_id:id,segments:points.map((p,i)=>line(p,points[(i+1)%points.length]))});
const approx=(actual,expected,tol=1e-8)=>assert.ok(Math.abs(actual-expected)<=tol,`${actual} != ${expected}`);
test('сохраняет оба конца несвязанных сегментов и различает объекты/агрегат',()=>{
 const r=measureSnapshot({objects:[{static_id:1,segments:[line({x:0,y:0},{x:10,y:0}),line({x:0,y:2},{x:1,y:2})]},poly([{x:20,y:0},{x:21,y:0},{x:21,y:1},{x:20,y:1}],2)]},.001);
 approx(r.objects[0].rectangle.area_mm2,20);approx(r.aggregate.rectangle.area_mm2,42);
});
test('не принимает лишние поля, повтор StaticID, пустые объекты и опасные числа',()=>{
 const valid={objects:[poly([{x:0,y:0},{x:1,y:0},{x:0,y:1}])]};
 const invalid=[{...valid,extra:1},{objects:[{...valid.objects[0],extra:1}]},{objects:[valid.objects[0],valid.objects[0]]},{objects:[{static_id:0,segments:[]}]},{objects:[]}, {objects:[{static_id:1,segments:[line({x:0,y:0,extra:1},{x:0,y:1})]}]}];
 for(const s of invalid)assert.throws(()=>measureSnapshot(s,.01),e=>e.code==='GEOMETRY_INVALID_INPUT');
 assert.throws(()=>measureSnapshot({objects:[poly([{x:1e100,y:0},{x:1e100,y:1}])]},.01),e=>e.code==='GEOMETRY_NUMERIC_LIMIT');
});
test('точки, линии и повторы имеют устойчивый результат',()=>{
 let r=measureSnapshot({objects:[poly([{x:3,y:4},{x:3,y:4}])]},.01).objects[0];
 approx(r.min_width_mm,0);assert.equal(r.rectangle.corners.length,4);
 r=measureSnapshot({objects:[poly([{x:0,y:0},{x:10,y:0},{x:5,y:0}])]},.01).objects[0];
 approx(r.rectangle.width_mm,10);approx(r.rectangle.height_mm,0);approx(r.rectangle.angle_deg,0);
});
test('S-кривая с серединой на хорде сохраняет истинный размах',()=>{
 const r=measureSnapshot({objects:[{static_id:1,segments:[{kind:'cubic',p0:{x:0,y:0},p1:{x:10,y:20},p2:{x:20,y:-20},p3:{x:30,y:0}}]}]},.001);
 // Независимый эталон: точная формула кубики и плотные отсчёты, затем полный перебор направлений.
 const ps=Array.from({length:1001},(_,i)=>{const t=i/1000;return {x:30*t,y:60*t*(1-t)*(1-2*t)}});
 const expected=exhaustive(ps);approx(r.objects[0].min_width_mm,expected.width,.003);approx(r.objects[0].rectangle.area_mm2,expected.area,.2);
});
function exhaustive(points){
 // Независимый brute-force hull: поддерживающие пары, без использования production hull.
 const hullEdges=[];
 for(let i=0;i<points.length;i++)for(let j=i+1;j<points.length;j++){
  const a=points[i],b=points[j];if(a.x===b.x&&a.y===b.y)continue; let pos=false,neg=false;
  for(const p of points){const c=(b.x-a.x)*(p.y-a.y)-(b.y-a.y)*(p.x-a.x);if(c>1e-9)pos=true;if(c<-1e-9)neg=true;if(pos&&neg)break;}
  if(!(pos&&neg))hullEdges.push([a,b]);
 }
 let width=Infinity,area=Infinity;
 for(const [a,b] of hullEdges){const dx=b.x-a.x,dy=b.y-a.y,d=Math.hypot(dx,dy),us=points.map(p=>(p.x*dx+p.y*dy)/d),vs=points.map(p=>(p.y*dx-p.x*dy)/d);const w=Math.max(...us)-Math.min(...us),h=Math.max(...vs)-Math.min(...vs);width=Math.min(width,h);area=Math.min(area,w*h);}
 return {width,area};
}
test('calipers совпадает с независимым полным перебором малых hull',()=>{
 let seed=93841;const rand=()=>((seed=(1664525*seed+1013904223)>>>0)/4294967296);
 for(let i=0;i<50;i++){const ps=Array.from({length:14},()=>({x:rand()*100,y:rand()*100}));const e=exhaustive(ps),r=measureSnapshot({objects:[poly(ps)]},.001).objects[0];approx(r.min_width_mm,e.width,1e-8);approx(r.rectangle.area_mm2,e.area,1e-6);assert.ok(r.rectangle.width_mm>=r.rectangle.height_mm);assert.ok(r.rectangle.angle_deg>=0&&r.rectangle.angle_deg<180);}
});
test('полный круг обрабатывает 200000 вершин без квадратичного прохода',{timeout:20000},()=>{
 const ps=Array.from({length:200000},(_,i)=>({x:100*Math.cos(2*Math.PI*i/200000),y:100*Math.sin(2*Math.PI*i/200000)}));
 const r=measureSnapshot({objects:[poly(ps)]},.001).objects[0];approx(r.min_width_mm,200,1e-5);approx(r.rectangle.area_mm2,40000,.01);
});
test('лимит общий для объектов и flattening, допуск не ухудшается',()=>{
 const p=Array.from({length:100001},(_,i)=>({x:i,y:Math.sin(i)}));
 assert.throws(()=>measureSnapshot({objects:[poly(p,1),poly(p,2)]},.001),e=>e.code==='GEOMETRY_LIMIT_EXCEEDED');
 assert.throws(()=>measureSnapshot({objects:[{static_id:1,segments:Array.from({length:10},()=>({kind:'cubic',p0:{x:0,y:0},p1:{x:1000000,y:1000000},p2:{x:-1000000,y:1000000},p3:{x:0,y:0}}))}]},.001),e=>e.code==='GEOMETRY_LIMIT_EXCEEDED');
});

test('повёрнутый кубический эллипс проверяется по аналитическим диаметрам',()=>{
 const a=50,b=20,k=4*(Math.sqrt(2)-1)/3,phi=.731;
 const tr=([x,y])=>({x:x*Math.cos(phi)-y*Math.sin(phi)+123,y:x*Math.sin(phi)+y*Math.cos(phi)-41});
 const cs=[[[a,0],[a,k*b],[k*a,b],[0,b]],[[0,b],[-k*a,b],[-a,k*b],[-a,0]],[[-a,0],[-a,-k*b],[-k*a,-b],[0,-b]],[[0,-b],[k*a,-b],[a,-k*b],[a,0]]];
 const segments=cs.map(ps=>({kind:'cubic',p0:tr(ps[0]),p1:tr(ps[1]),p2:tr(ps[2]),p3:tr(ps[3])}));
 const r=measureSnapshot({objects:[{static_id:1,segments}]},.001),m=r.objects[0];
 approx(m.min_width_mm,2*b,.002);approx(m.rectangle.width_mm,2*a,.002);approx(m.rectangle.height_mm,2*b,.002);approx(m.rectangle.area_mm2,4*a*b,.3);
 assert.equal(r.boundary_error_mm,.001);assert.equal(r.dimension_error_mm,.002);
});
test('канонический угол и порядок углов квадрата не зависят от обхода',()=>{
 const ps=[{x:0,y:0},{x:10,y:0},{x:10,y:10},{x:0,y:10}];
 for(const points of [ps,ps.toReversed(),[...ps.slice(2),...ps.slice(0,2)]]){const r=measureSnapshot({objects:[poly(points)]},.001).objects[0];approx(r.rectangle.angle_deg,0);approx(r.min_width_angle_deg,0);approx(Math.hypot(r.rectangle.corners[1].x-r.rectangle.corners[0].x,r.rectangle.corners[1].y-r.rectangle.corners[0].y),r.rectangle.width_mm);}
});
test('конечные огромные смещения не создают ложное обещание точности',()=>{
 assert.throws(()=>measureSnapshot({objects:[poly([{x:1e10,y:0},{x:1e10+1,y:0},{x:1e10,y:1}])]},.001),e=>e.code==='GEOMETRY_NUMERIC_LIMIT');
});

test('валидирует границы tolerance и точные лимиты объектов/сегментов',()=>{
 const tri=poly([{x:0,y:0},{x:1,y:0},{x:0,y:1}]);
 for(const t of [.000999,1.001,NaN,Infinity,-1])assert.throws(()=>measureSnapshot({objects:[tri]},t),e=>e.code==='GEOMETRY_INVALID_INPUT');
 const hundred={objects:Array.from({length:100},(_,i)=>poly([{x:i,y:0},{x:i+1,y:0},{x:i,y:1}],i+1))};assert.equal(measureSnapshot(hundred,.01).objects.length,100);
 assert.throws(()=>measureSnapshot({objects:Array.from({length:101},(_,i)=>poly([{x:i,y:0},{x:i+1,y:0},{x:i,y:1}],i+1))},.01),e=>e.code==='GEOMETRY_LIMIT_EXCEEDED');
});
test('дистанция до хорды учитывает дальние контрольные точки даже при midpoint на хорде',()=>{
 const r=measureSnapshot({objects:[{static_id:1,segments:[{kind:'cubic',p0:{x:-10,y:0},p1:{x:-5,y:100},p2:{x:5,y:-100},p3:{x:10,y:0}}]}]},.01);
 assert.ok(r.objects[0].rectangle.width_mm>50);assert.ok(r.objects[0].rectangle.height_mm>15);
});

test('направление вырожденного прямоугольника соответствует порядку углов',()=>{
 const r=measureSnapshot({objects:[poly([{x:0,y:10},{x:10,y:0}])]},.001).objects[0].rectangle;
 const [a,b]=r.corners;const ang=Math.atan2(b.y-a.y,b.x-a.x)*180/Math.PI;
 approx(ang,r.angle_deg);approx(r.angle_deg,135);
});
test('общий лимит flattened points проверяется раньше получения всех hull',()=>{
 const objects=Array.from({length:2},(_,i)=>({static_id:i+1,segments:Array.from({length:60000},(_,j)=>line({x:j*2,y:i},{x:j*2+1,y:i}))}));
 assert.throws(()=>measureSnapshot({objects},.001),e=>e.code==='GEOMETRY_LIMIT_EXCEEDED');
});
