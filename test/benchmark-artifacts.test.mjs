import test from 'node:test';
import assert from 'node:assert/strict';
import {crc32,deflateSync} from 'node:zlib';
import {decodePng} from '../scripts/benchmark-artifacts.mjs';
function png(interlace=0) {
  const chunk=(kind,data)=>{const header=Buffer.alloc(4),tail=Buffer.alloc(4),body=Buffer.concat([Buffer.from(kind),data]);header.writeUInt32BE(data.length);tail.writeUInt32BE(crc32(body));return Buffer.concat([header,body,tail]);};
  const ihdr=Buffer.alloc(13);ihdr.writeUInt32BE(1,0);ihdr.writeUInt32BE(1,4);ihdr[8]=8;ihdr[9]=2;ihdr[12]=interlace;
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',ihdr),chunk('IDAT',deflateSync(Buffer.from([0,255,0,0]))),chunk('IEND',Buffer.alloc(0))]);
}
test('benchmark reads decoded pixels and rejects corrupt image data',()=>{
  const input=png(),image=decodePng(input);assert.equal(image.width,1);assert.equal(image.height,1);assert.deepEqual([...image.rgba],[255,0,0,255]);
  const broken=Buffer.from(input);broken[broken.length-18]^=1;assert.throws(()=>decodePng(broken));
  assert.deepEqual([...decodePng(png(1)).rgba],[255,0,0,255]);
});
