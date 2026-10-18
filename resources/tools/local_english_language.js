'use strict';
// KartGameUserSettings constructor builds two parallel FString arrays.
// Extend them using the game's own deep-copy allocator; never transfer Frida
// allocations into native ownership. Existing Korean/Chinese indices stay put.
const game = Process.getModuleByName('KartDrift-Win64-Shipping.exe');
const extraCodes = globalThis.kdSimplified === true ? ['en-US', 'zh-Hans'] : ['en-US'];
const extraNames = globalThis.kdSimplified === true ? ['English', '简体中文'] : ['English'];
let installed = false, expanded = 0, failure = null, hooks = [], minimalPatches = 0;
function bytes(p, n) {
  return Array.from(new Uint8Array(p.readByteArray(n)), x => x.toString(16).padStart(2, '0')).join('');
}
function readArray(p) {
  const count = p.add(8).readS32(), data = p.readPointer();
  if (count < 0 || count > 16 || (count && data.isNull())) throw new Error('Invalid language array');
  const values = [];
  for (let i = 0; i < count; i++) {
    const entry = data.add(i * 16), length = entry.add(8).readS32();
    if (length < 1 || length > 80) throw new Error('Invalid language string');
    values.push(entry.readPointer().readUtf16String(length - 1));
  }
  return values;
}
function extend(settings) {
  const codes = settings.add(0x638), names = settings.add(0x648);
  const before = readArray(codes), labels = readArray(names);
  if (JSON.stringify(before) === JSON.stringify(['ko-kr', 'zh-hant', ...extraCodes]) &&
      JSON.stringify(labels) === JSON.stringify(['한국어', '繁體中文', ...extraNames])) return;
  if (JSON.stringify(before) !== JSON.stringify(['ko-kr', 'zh-hant']) ||
      JSON.stringify(labels) !== JSON.stringify(['한국어', '繁體中文'])) throw new Error('Language layout/version mismatch');
  const copy = new NativeFunction(game.base.add(0x1461cb0), 'pointer', ['pointer', 'pointer']);
  const free = new NativeFunction(game.base.add(0x25ca440), 'void', ['pointer']);
  function prepare(values) {
    const strings = values.map(value => Memory.allocUtf16String(value));
    const items = Memory.alloc(values.length * 16), range = Memory.alloc(16), result = Memory.alloc(16);
    values.forEach((value, i) => {
      const entry = items.add(i * 16);
      entry.writePointer(strings[i]); entry.add(8).writeS32(value.length + 1); entry.add(12).writeS32(value.length + 1);
    });
    range.writePointer(items); range.add(8).writePointer(items.add(values.length * 16));
    copy(result, range);
    return result;
  }
  const newCodes = prepare([...before, ...extraCodes]), newNames = prepare([...labels, ...extraNames]);
  function replace(target, replacement) {
    const old = target.readPointer(), count = target.add(8).readS32();
    Memory.copy(target, replacement, 16);
    for (let i = 0; i < count; i++) free(old.add(i * 16).readPointer());
    free(old);
  }
  replace(codes, newCodes); replace(names, newNames);
  expanded++;
  send({kind: 'english_language_added', codes: readArray(codes), names: readArray(names)});
}
function safeExtend(settings) { try { extend(settings); } catch (error) { failure = String(error); send({kind: 'english_language_error', error: failure}); } }
function install() {
  if (bytes(game.base.add(0x1a8c8e0), 16) !== '48895c24084889742410574883ec308b') return false;
  const expected = [
    [0x1461cb0, '48896c242057415441564883ec20'],
    [0x1a71050, '48896c242057415441564883ec20'],
    [0x24289e0, '48895c241855565741564157488dac24'],
    [0x25e8f70, '48895c24084889742410574883ec20'],
    [0x25f32ca, '4180be98000000000f85'],
  ];
  for (const [rva, value] of expected) if (bytes(game.base.add(rva), value.length / 2) !== value) {
    failure = 'Native English version mismatch at ' + rva.toString(16); throw new Error(failure);
  }
  hooks.push(Interceptor.attach(game.base.add(0x24289e0), {onEnter(a) { this.settings = a[0]; }, onLeave() { safeExtend(this.settings); }}));
  hooks.push(Interceptor.attach(game.base.add(0x1a71050), {onEnter(a) { safeExtend(a[0]); }}));
  // The game's runtime Polyglot data has no English entries. Its default
  // native-string fallback overwrites the loaded locres after login.
  // Minimal patches retain existing resources when a culture is absent.
  hooks.push(Interceptor.attach(game.base.add(0x25e8f70), {
    onEnter(a) { this.value = this.returnAddress.equals(game.base.add(0x1b959b6)) ? a[0] : null; },
    onLeave() {
      if (!this.value) return;
      try {
        const p = this.value, ns = p.add(0x18).readPointer().readUtf16String();
        if (globalThis.kdDisablePolyglotFix !== true && p.readU8() === 0 && (ns === 'String' || ns === 'GOT')) {
          p.add(0x98).writeU8(1); minimalPatches++;
        }
      } catch (error) { failure = String(error); }
    }
  }));
  installed = true; send({kind: 'english_language_hooks_ready'}); return true;
}
let attempts = 0;
const timer = setInterval(() => {
  try {
    if (installed || install()) clearInterval(timer);
    else if (++attempts >= 300) { failure = 'Timed out waiting for the verified client code'; clearInterval(timer); }
  }
  catch (error) { failure = String(error); clearInterval(timer); send({kind: 'english_language_error', error: failure}); }
}, 100);
rpc.exports = {status() { return {installed, expanded, failure, minimalPatches}; }};
