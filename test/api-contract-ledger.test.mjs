import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const read = relative => JSON.parse(readFileSync(join(root, relative), 'utf8').replace(/^\uFEFF/, ''));
const ledger = read('worker/api-contract.json');
// describe_api-equivalent dump: static reflection over the generated interop assembly pinned by
// worker/generated/fingerprint.json (see the fixture provenance block for how it was produced).
const fixture = read('test/fixtures/native-fills-describe-api.json');
const byKey = new Map(ledger.signatures.map(entry => [`${entry.interface}.${entry.member}`, entry]));
const key = entry => `${entry.interface}.${entry.member}`;

// All 12 live tests of worker/CorelMcp.Worker.Tests/LiveNativeFillsTests.cs, pinned: a ledger citation
// outside this list is a typo or an unreviewed change of the live suite.
const pinnedLiveTests = [
  'MultiStopFountainFillsApplyAndReadBackOnRectangleEllipseAndCurve',
  'RadialFountainCenterOffsetsArePassedAsPercentInMmAndInchDocuments',
  'AbsentFountainOptionsRoundTripApiTypicalDefaults',
  'FountainStyleStaysOnCSharpAboveTheNativeThresholdInHybridMode',
  'UniformAndFountainTransparencyApplyReadBackAndRemoveThroughTheKindUnion',
  'FountainTransparencyAcceptsRadialCenterPercentsAndSquareAngle',
  'GroupTransparencyAppliesToEverySupportedLeafAndRefusesMixedGroupsBeforeMutation',
  'TransparencyRefusalsProtectFrozenLockedLensAndBarrierTargetsBeforeMutation',
  'SquareFountainFillEdgePadAndAngleBehaviorIsProbedLive',
  'DropShadowAppliesToRectTextAndGroupMemberAndReportsIdMappings',
  'DropShadowRefusalsProtectRepeatedGroupLockedBitmapAndExistingEffects',
  'DropShadowNoneRemovesTheEffectAndRestoresTheOriginalTarget',
];

// Live gates that cover the new surface: worker/CorelMcp.Worker.Tests/LiveNativeFillsTests.cs.
const citations = {
  'IVGFill.ApplyFountainFill': 'LiveNativeFillsTests.MultiStopFountainFillsApplyAndReadBackOnRectangleEllipseAndCurve',
  'IVGFountainColors.get_Item': 'LiveNativeFillsTests.MultiStopFountainFillsApplyAndReadBackOnRectangleEllipseAndCurve',
  'IVGFountainColors.Add': 'LiveNativeFillsTests.MultiStopFountainFillsApplyAndReadBackOnRectangleEllipseAndCurve',
  'IVGTransparency.ApplyUniformTransparency': 'LiveNativeFillsTests.UniformAndFountainTransparencyApplyReadBackAndRemoveThroughTheKindUnion',
  'IVGTransparency.set_AppliedTo': 'LiveNativeFillsTests.UniformAndFountainTransparencyApplyReadBackAndRemoveThroughTheKindUnion',
  'IVGShape.CreateDropShadow': 'LiveNativeFillsTests.DropShadowAppliesToRectTextAndGroupMemberAndReportsIdMappings',
  'IVGShape.ClearEffect': 'LiveNativeFillsTests.DropShadowNoneRemovesTheEffectAndRestoresTheOriginalTarget',
};

test('ledger contains every native-fills signature exactly as reflected from the pinned interop assembly', () => {
  for (const entry of fixture.signatures) {
    const actual = byKey.get(key(entry));
    assert.ok(actual, `ledger is missing ${key(entry)}`);
    assert.equal(actual.clr_signature, entry.clr_signature, `${key(entry)} clr_signature must match the interop reflection`);
    assert.equal(actual.sdk_url, entry.sdk_url, `${key(entry)} sdk_url must follow the ledger convention`);
    assert.deepEqual(actual.parameters, entry.parameters, `${key(entry)} parameters must match the interop reflection`);
  }
});

test('native-fills ledger entries cite covering live tests or state how they were verified', () => {
  for (const entry of fixture.signatures) {
    const live = byKey.get(key(entry))?.live_status;
    assert.ok(typeof live === 'string' && live.length > 0, `${key(entry)} needs a live_status`);
    for (const match of live.matchAll(/Live([A-Za-z]+)Tests\.([A-Za-z]+)/g)) {
      assert.equal(`Live${match[1]}Tests`, 'LiveNativeFillsTests', `${key(entry)} cites an unexpected suite Live${match[1]}Tests`);
      assert.ok(pinnedLiveTests.includes(match[2]), `${key(entry)} cites unpinned LiveNativeFillsTests.${match[2]}`);
    }
    if (live.includes('verified_live')) {
      assert.ok(live.includes('LiveNativeFillsTests.'), `${key(entry)} claims live verification without citing a live test`);
    } else {
      // Reflected contract members that were not individually exercised live must say so.
      assert.match(live, /static_reflection|probed_live/, `${key(entry)} must say how it was verified`);
    }
    const cited = citations[key(entry)];
    if (cited) assert.ok(live.includes(cited), `${key(entry)} must cite ${cited}`);
  }
  assert.deepEqual([...ledger.native_fills.covering_live_tests].sort(), [...pinnedLiveTests].sort(),
    'native_fills.covering_live_tests must list exactly the pinned live suite');
});

test('native-fills enum values match the pinned interop reflection', () => {
  for (const [name, value] of Object.entries(fixture.enum_values)) {
    assert.equal(ledger.enum_values[name], value, `enum ${name} must match the interop reflection`);
  }
});

test('observed live behavior from gates T4-T6 is recorded in the ledger', () => {
  const block = ledger.native_fills;
  assert.ok(block, 'ledger needs a native_fills evidence block');
  assert.deepEqual(block.observed_defaults, {
    steps: 256, edge_pad: 0, mid_point: 50, blend_type: 'direct', spread: 'pad', smooth_blend: false,
  });
  const facts = JSON.stringify(block);
  const required = [
    'Item is 0-based',                     // IVGFountainColors.Item is 0-based (unlike IVGShapes)
    'blend_type=custom',                   // multi-stop fountains read back as custom
    'EdgePad', '0->15', '20->29', 'angle 0 round-trips', 'conical', // renormalization (linear AND square)
    'Apply -> AppliedTo/MergeMode -> Freeze', // transparency properties exist only after Apply*
    'does not wrap', 'result_static_ids:[target, group, child]',
    'ClearEffect(cdrDropShadow)', 'IVGEffect.Clear()',
    'perspective 0/0 round-trips', 'fade round-trips even on flat',
    'percents passed through without unit conversion',
  ];
  for (const fact of required) assert.ok(facts.includes(fact), `native_fills must record: ${fact}`);
  assert.ok(Array.isArray(block.covering_live_tests) && block.covering_live_tests.length >= 12,
    'native_fills must list the covering LiveNativeFillsTests tests');
  assert.ok(block.covering_live_tests.every(name => /^[A-Za-z]+$/.test(name)), 'test names must be plain LiveNativeFillsTests member names');
});

test('typelib binding is unchanged and pinned to the generated interop fingerprint', () => {
  // worker/generated/ is a build artifact (gitignored): a clean checkout without a worker build has no
  // fingerprint.json, and a missing file must not fail this file on ENOENT. Following the synthesis
  // precedent of test/worker-package.test.mjs:104-105, the pinned typelib identity is rebuilt from the
  // committed fixture provenance when the generated artifact is absent.
  const built = existsSync(join(root, 'worker/generated/fingerprint.json'));
  const fingerprint = built ? read('worker/generated/fingerprint.json') : {
    source_sha256: fixture.provenance.typelib.source_sha256,
    typelib_guid: fixture.provenance.typelib.guid,
    major: fixture.provenance.typelib.major,
    minor: fixture.provenance.typelib.minor,
  };
  assert.equal(ledger.typelib.source_sha256, fingerprint.source_sha256, 'typelib source_sha256 must stay bound to the generated interop');
  assert.equal(ledger.typelib.guid, fingerprint.typelib_guid);
  assert.equal(ledger.typelib.major, fingerprint.major);
  assert.equal(ledger.typelib.minor, fingerprint.minor);
  assert.equal(ledger.typelib.source_sha256, fixture.provenance.typelib.source_sha256, 'fixture and ledger must describe the same typelib');
});

test('the tool catalog stays at 41 and ledger signature keys stay unique', () => {
  assert.equal(ledger.hybrid_runtime.catalog_tools, 41);
  const keys = ledger.signatures.map(entry => key(entry));
  assert.equal(new Set(keys).size, keys.length, 'duplicate interface.member entries in the ledger');
});
